using System.Text.Json.Nodes;
using Lockbox.App.Services;
using Lockbox.Core.Model;
using Lockbox.Core.Tools;
using Lockbox.Core.Web;

namespace Lockbox.App.ViewModels;

/// <summary>
/// Answers the Lockbox browser extension. Runs on the UI thread. The rules:
/// <list type="bullet">
/// <item>Only our extension (by ID, as reported by the browser) and only browsers you approved once (pairing).</item>
/// <item>Nothing while the vault is locked.</item>
/// <item>The extension asks for logins by page address. It only gets logins saved for that site, and a password only
///       when you pick a login (filling on a different site needs you to confirm it in the extension).</item>
/// <item>Protected items ask for the master password here in Lockbox first.</item>
/// <item>Saving and updating only touch logins for the site you're on, and old passwords go to the history.</item>
/// </list>
/// </summary>
public sealed class BrowserRequestHandler(MainWindowViewModel main)
{
    public const int MaxSearchResults = 50;
    public const int MaxPasswordLength = 1024;
    public const int MaxUsernameLength = 512;

    private bool _pairing;

    public static string AppVersion => typeof(BrowserRequestHandler).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public async Task<JsonObject> HandleAsync(JsonObject req)
    {
        JsonNode? id = req["id"];
        string type = Str(req, "type");

        if (!BrowserBridge.IsTrustedCaller(Str(req, "caller")))
            return Reply.Error(id, "untrusted", "Lockbox only answers the Lockbox extension.");
        if (!main.Settings.BrowserExtensionEnabled)
            return Reply.Error(id, "disabled", "The browser extension is turned off in Lockbox's Settings.");

        string client = Str(req, "client");
        bool paired = BrowserBridge.IsValidClientId(client) && main.Settings.BrowserClients.Any(c => c.Id == client);
        var vault = main.ActiveVault;

        switch (type)
        {
            case "status":
                return Reply.Ok(id, new JsonObject
                {
                    ["state"] = !paired ? "not-paired" : vault == null ? "locked" : "unlocked",
                    ["vault"] = paired ? vault?.VaultName : null,
                    ["version"] = AppVersion,
                });
            case "pair":
                return await PairAsync(id, client, Str(req, "browser"));
            case "show":
                main.Platform.BringToFront();
                return Reply.Ok(id);
        }

        if (!paired) return Reply.Error(id, "not-paired", "Connect this browser to Lockbox first.");
        if (vault == null) return Reply.Error(id, "locked", "Lockbox is locked.");

        return type switch
        {
            "logins" => Logins(id, vault, Str(req, "url")),
            "search" => Search(id, vault, Str(req, "query")),
            "fill" => await FillAsync(id, vault, req),
            "checkSave" => CheckSave(id, vault, req),
            "save" => await SaveAsync(id, vault, req),
            "generate" => Reply.Ok(id, new JsonObject { ["password"] = PasswordGenerator.Generate(new PasswordOptions()) }),
            _ => Reply.Error(id, "bad-request", $"Lockbox doesn't know the request \"{type}\"."),
        };
    }

    // ───────────── pairing ─────────────

    private async Task<JsonObject> PairAsync(JsonNode? id, string client, string browser)
    {
        if (!BrowserBridge.IsValidClientId(client)) return Reply.Error(id, "bad-request", "The extension sent an invalid ID.");
        if (main.Settings.BrowserClients.Any(c => c.Id == client)) return Reply.Ok(id, new JsonObject { ["paired"] = true });
        if (_pairing || main.Dialog != null) return Busy(id);

        _pairing = true;
        try
        {
            string name = BrowserName(browser);
            main.Platform.BringToFront();
            bool allow = await main.ConfirmAsync($"Connect {name} to Lockbox?",
                $"{name} is asking to fill and save passwords with Lockbox.\n\n" +
                $"Only allow this if you just clicked Connect in the Lockbox extension and it shows this code:\n\n" +
                $"{BrowserBridge.PairingCode(client)}\n\n" +
                "You can disconnect it any time in Settings.",
                "Allow", cancel: "Don't allow");
            if (!allow) return Reply.Error(id, "denied", "Lockbox didn't allow the connection.");

            main.Settings.BrowserClients.Add(new BrowserClient(client, name, DateTimeOffset.UtcNow));
            main.Settings.Save();
            return Reply.Ok(id, new JsonObject { ["paired"] = true });
        }
        finally { _pairing = false; }
    }

    public static string BrowserName(string browser) => browser switch
    {
        "chrome" => "Google Chrome",
        "edge" => "Microsoft Edge",
        "brave" => "Brave",
        "firefox" => "Firefox",
        "opera" => "Opera",
        "vivaldi" => "Vivaldi",
        _ => "Your browser",
    };

    // ───────────── finding logins ─────────────

    private static JsonObject Logins(JsonNode? id, VaultViewModel vault, string url)
    {
        var page = WebSite.Parse(url);
        if (page == null) return Reply.Ok(id, new JsonObject { ["site"] = null, ["matches"] = new JsonArray() });

        var matches = ForPage(vault, url)
            .Select(m => (JsonNode)Summary(m.Entry, m.Match == WebSite.Match.SameHost))
            .ToArray();
        return Reply.Ok(id, new JsonObject
        {
            ["site"] = page.Site,
            ["host"] = page.Host,
            ["secure"] = page.IsSecure,
            ["matches"] = new JsonArray(matches),
        });
    }

    private static IEnumerable<(Entry Entry, WebSite.Match Match)> ForPage(VaultViewModel vault, string url) =>
        vault.Vault.Entries
            .Where(e => e.Type == ItemType.Login && (e.Password.Length > 0 || e.Username.Length > 0))
            .Select(e => (Entry: e, Match: WebSite.Compare(e.Url, url)))
            .Where(x => x.Match != WebSite.Match.None)
            .OrderByDescending(x => x.Match)
            .ThenBy(x => x.Entry.Title, StringComparer.CurrentCultureIgnoreCase);

    private static JsonObject Search(JsonNode? id, VaultViewModel vault, string query)
    {
        string q = query.Trim();
        bool Has(string s) => s.Contains(q, StringComparison.OrdinalIgnoreCase);
        var all = vault.Vault.Entries
            .Where(e => e.Type == ItemType.Login && (e.Password.Length > 0 || e.Username.Length > 0))
            .Where(e => q.Length == 0 || Has(e.Title) || Has(e.Username) || Has(e.Url))
            .OrderBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return Reply.Ok(id, new JsonObject
        {
            ["total"] = all.Count,
            ["results"] = new JsonArray([.. all.Take(MaxSearchResults).Select(e => (JsonNode)Summary(e, exact: false))]),
        });
    }

    private static JsonObject Summary(Entry e, bool exact) => new()
    {
        ["id"] = e.Id.ToString(),
        ["title"] = e.Title,
        ["username"] = e.Username,
        ["site"] = WebSite.SiteOf(e.Url),
        ["protected"] = e.Reprompt,
        ["exact"] = exact,
    };

    // ───────────── filling ─────────────

    private async Task<JsonObject> FillAsync(JsonNode? id, VaultViewModel vault, JsonObject req)
    {
        string url = Str(req, "url");
        var page = WebSite.Parse(url);
        if (page == null) return Reply.Error(id, "bad-page", "Lockbox only fills logins on web pages.");
        if (FindLogin(vault, req) is not { } entry) return Reply.Error(id, "not-found", "That login isn't in your vault anymore.");

        if (WebSite.Compare(entry.Url, url) == WebSite.Match.None && !Bool(req, "anySite"))
        {
            var err = Reply.Error(id, "other-site", entry.Url.Trim().Length == 0
                ? "This login has no website saved."
                : $"This login is saved for {WebSite.SiteOf(entry.Url)}, not {page.Site}.");
            err["savedSite"] = WebSite.SiteOf(entry.Url);
            return err;
        }

        if (entry.Reprompt && main.Dialog != null) return Busy(id);
        if (entry.Reprompt && !await UnsealAsync(vault, entry)) return Reply.Error(id, "denied", "The master password wasn't entered, so nothing was filled.");
        entry = vault.Vault.Entries.First(e => e.Id == entry.Id);

        if (Bool(req, "rememberSite") && entry.Url.Trim().Length == 0)
        {
            var withSite = entry.Clone();
            withSite.Url = page.Origin;
            if (await vault.SaveFromBrowserAsync(withSite, isNew: false) is { } saved) entry = saved;
        }

        Activity.Touch();
        vault.Status = $"Filled {entry.Title} in your browser.";
        return Reply.Ok(id, new JsonObject
        {
            ["username"] = entry.Username,
            ["password"] = entry.Password,
            ["title"] = entry.Title,
            ["site"] = page.Site,
        });
    }

    // ───────────── saving ─────────────

    private static JsonObject CheckSave(JsonNode? id, VaultViewModel vault, JsonObject req)
    {
        string url = Str(req, "url");
        string username = Str(req, "username").Trim();
        string password = Str(req, "password");
        var none = Reply.Ok(id, new JsonObject { ["action"] = "none" });
        var page = WebSite.Parse(url);
        if (page == null || password.Length == 0 || password.Length > MaxPasswordLength) return none;

        var forSite = ForPage(vault, url).Select(m => m.Entry).ToList();
        if (forSite.Any(e => SameUser(e.Username, username) && e.Password == password)) return none;

        var sameUser = forSite.Where(e => SameUser(e.Username, username)).ToList();
        if (username.Length == 0 && forSite.Count == 1) sameUser = forSite;     // a form with only a password box
        if (sameUser.Count == 1)
        {
            var e = sameUser[0];
            // Typing an old password (say, a failed sign-in) shouldn't offer to go back to it.
            if (e.PasswordHistory.Any(h => h.Password == password)) return none;
            return Reply.Ok(id, new JsonObject
            {
                ["action"] = "update",
                ["entry"] = e.Id.ToString(),
                ["title"] = e.Title,
                ["username"] = e.Username,
                ["site"] = page.Site,
            });
        }
        return Reply.Ok(id, new JsonObject { ["action"] = "new", ["title"] = page.Site, ["username"] = username, ["site"] = page.Site });
    }

    private async Task<JsonObject> SaveAsync(JsonNode? id, VaultViewModel vault, JsonObject req)
    {
        string url = Str(req, "url");
        string username = Str(req, "username").Trim();
        string password = Str(req, "password");
        string title = Str(req, "title").Trim();
        var page = WebSite.Parse(url);
        if (page == null) return Reply.Error(id, "bad-page", "Lockbox only saves logins from web pages.");
        if (password.Length == 0 || password.Length > MaxPasswordLength || username.Length > MaxUsernameLength || title.Length > 200)
            return Reply.Error(id, "bad-request", "That login can't be saved.");

        if (req["entry"] != null)
        {
            if (FindLogin(vault, req) is not { } entry) return Reply.Error(id, "not-found", "That login isn't in your vault anymore.");
            if (WebSite.Compare(entry.Url, url) == WebSite.Match.None)
                return Reply.Error(id, "other-site", $"That login is saved for {WebSite.SiteOf(entry.Url)}, so it can't be changed from {page.Site}.");
            if (entry.Reprompt && main.Dialog != null) return Busy(id);
            if (entry.Reprompt && !await UnsealAsync(vault, entry)) return Reply.Error(id, "denied", "The master password wasn't entered, so nothing was changed.");

            var updated = vault.Vault.Entries.First(e => e.Id == entry.Id).Clone();
            updated.Password = password;
            if (username.Length > 0) updated.Username = username;
            var saved = await vault.SaveFromBrowserAsync(updated, isNew: false);
            return saved == null
                ? Reply.Error(id, "busy", "That login is open in Lockbox with unsaved changes. Save or close it first.")
                : Reply.Ok(id, new JsonObject { ["action"] = "updated", ["entry"] = saved.Id.ToString(), ["title"] = saved.Title });
        }

        var created = await vault.SaveFromBrowserAsync(new Entry
        {
            Type = ItemType.Login,
            Title = title.Length > 0 ? title : page.Site,
            Url = page.Origin,
            Username = username,
            Password = password,
        }, isNew: true);
        return created == null
            ? Reply.Error(id, "save-failed", "Lockbox couldn't save the vault.")
            : Reply.Ok(id, new JsonObject { ["action"] = "created", ["entry"] = created.Id.ToString(), ["title"] = created.Title });
    }

    // ───────────── helpers ─────────────

    private async Task<bool> UnsealAsync(VaultViewModel vault, Entry entry)
    {
        main.Platform.BringToFront();
        return await vault.EnsureUnsealedAsync(entry, "Your browser is asking for this login. Enter your master password to allow it.");
    }

    private static Entry? FindLogin(VaultViewModel vault, JsonObject req) =>
        Guid.TryParse(Str(req, "entry"), out var entryId)
            ? vault.Vault.Entries.FirstOrDefault(e => e.Id == entryId && e.Type == ItemType.Login)
            : null;

    private static bool SameUser(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static JsonObject Busy(JsonNode? id) =>
        Reply.Error(id, "busy", "Lockbox is waiting for you in its window. Finish that first, then try again.");

    private static string Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    private static bool Bool(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
}
