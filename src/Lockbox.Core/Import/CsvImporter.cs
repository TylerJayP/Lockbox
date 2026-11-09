using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Lockbox.Core.Model;

namespace Lockbox.Core.Import;

/// <summary>What was found in a CSV export, and which app it seems to come from (for the summary line).</summary>
public sealed record CsvImportResult(string Source, List<ImportCandidate> Candidates);

/// <summary>
/// Reads password exports in CSV form by recognizing their header row: Chrome, Edge, Brave and other
/// Chromium browsers, Firefox, Safari / Apple Passwords, Bitwarden, 1Password, LastPass, KeePassXC,
/// Dashlane, Proton Pass, and Lockbox's own export (which round-trips cards, Wi-Fi, fields and tags).
/// Unknown layouts work too, as long as the header has a password column and at least two other known ones.
/// </summary>
public static partial class CsvImporter
{
    private enum Col { Title, Url, Username, Email, Password, Notes, Totp, Folder, Tags, Type, Fields, Reprompt, FormAction }

    private static readonly Dictionary<string, Col> Columns = BuildColumns();

    private static Dictionary<string, Col> BuildColumns()
    {
        var d = new Dictionary<string, Col>();
        void Add(Col c, params string[] names) { foreach (var n in names) d[Key(n)] = c; }
        Add(Col.Title, "name", "title", "item name", "account name", "service");
        Add(Col.Url, "url", "login_uri", "website", "uri", "login url", "web site", "urls");
        Add(Col.Username, "username", "login_username", "user name", "login", "login name", "user");
        Add(Col.Email, "email", "email address", "e-mail");
        Add(Col.Password, "password", "login_password", "pass");
        Add(Col.Notes, "notes", "note", "extra", "comments", "comment");
        Add(Col.Totp, "totp", "login_totp", "otpauth", "otp", "otp secret", "otpsecret", "otp url", "one-time password", "authenticator key");
        Add(Col.Folder, "folder", "group", "grouping", "category");
        Add(Col.Tags, "tags", "labels");
        Add(Col.Type, "type");
        Add(Col.Fields, "fields");
        Add(Col.Reprompt, "reprompt");
        Add(Col.FormAction, "formActionOrigin");
        return d;
    }

    private static string Key(string header) => new(header.Trim().Trim('"').ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    public const string TotpFieldName = "Authenticator key (TOTP)";

    /// <summary>True if the text starts with a header row this importer understands.</summary>
    public static bool LooksLikeCsv(string text) => Detect(text) != null;

    /// <summary>Reads a CSV export, or returns null if the text doesn't look like one (use <see cref="TextImporter"/> instead).</summary>
    public static CsvImportResult? TryParse(string text)
    {
        if (Detect(text) is not { } layout) return null;
        var rows = ReadRows(text, layout.Delimiter);
        if (rows.Count == 0) return null;

        var header = rows[0];
        var results = new List<ImportCandidate>();
        var seen = new Dictionary<(string, string, string), int>();

        for (int r = 1; r < rows.Count; r++)
        {
            var cells = rows[r];
            if (cells.All(c => c.Trim().Length == 0)) continue;

            string Get(Col c) => layout.Map.TryGetValue(c, out int i) && i < cells.Count ? cells[i].Trim() : "";
            var cand = BuildCandidate(Get, layout.Source);
            if (cand == null) continue;

            var src = new StringBuilder($"Row {r + 1}");
            for (int i = 0; i < header.Count && i < cells.Count; i++)
                if (cells[i].Trim().Length > 0) src.Append('\n').Append(header[i].Trim()).Append(": ").Append(cells[i].Trim());
            cand.SourceText = src.ToString();

            // Browsers often export the same login twice; keep the first and untick the rest.
            if (cand.Type == ItemType.Login && cand.Password.Length > 0)
            {
                var dupKey = (SiteKey(cand.Url) is { Length: > 0 } site ? site : cand.Title.ToLowerInvariant(), cand.Username.ToLowerInvariant(), cand.Password);
                if (seen.TryGetValue(dupKey, out int firstRow))
                {
                    cand.SuggestSkip = true;
                    cand.Warnings.Add($"Same site, username and password as row {firstRow}. Unticked.");
                }
                else seen[dupKey] = r + 1;
            }
            results.Add(cand);
        }
        return new CsvImportResult(layout.Source, results);
    }

    private static ImportCandidate? BuildCandidate(Func<Col, string> get, string source)
    {
        var c = new ImportCandidate();
        string type = get(Col.Type).ToLowerInvariant();
        string url = get(Col.Url);
        if (url.Length == 0) url = get(Col.FormAction);

        bool isNote = type is "note" or "securenote" or "secure note" || url.Equals("http://sn", StringComparison.OrdinalIgnoreCase);
        c.Type = isNote ? ItemType.SecureNote : ItemType.Login;
        c.Title = get(Col.Title);
        c.Notes = get(Col.Notes);
        c.Reprompt = get(Col.Reprompt) is "1" or "true" or "TRUE";

        if (!isNote)
        {
            c.Url = url;
            c.Username = get(Col.Username) is { Length: > 0 } u ? u : get(Col.Email);
            c.Password = get(Col.Password);
        }

        if (get(Col.Totp) is { Length: > 0 } totp)
        {
            c.TwoFactorEnabled = true;
            c.TwoFactorMethod = TwoFactorMethod.AuthenticatorApp;
            c.Fields.Add(new CustomField(TotpFieldName, totp, Hidden: true));
        }

        AddTag(c, CleanFolder(get(Col.Folder)));
        foreach (var t in get(Col.Tags).Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) AddTag(c, t);

        ReadFieldLines(c, get(Col.Fields));
        if (isNote && c.Notes.StartsWith("NoteType:", StringComparison.OrdinalIgnoreCase)) ReadLastPassNote(c);
        SplitBackupCodes(c);

        if (c.Type == ItemType.Login)
        {
            if (c.Title.Length == 0) c.Title = TitleFromUrl(c.Url);
            if (c.Url.StartsWith("android://", StringComparison.OrdinalIgnoreCase))
            {
                c.Warnings.Add("Login for an Android app (from Google Password Manager).");
                if (c.Title.StartsWith("android://", StringComparison.OrdinalIgnoreCase)) c.Title = TitleFromUrl(c.Url);
            }
            if (c.Username.Length == 0 && c.Password.Length == 0 && c.Notes.Length == 0 && c.Fields.Count == 0) return null;
            if (c.Password.Length == 0) c.Warnings.Add("No password in the export.");
        }
        if (c.Title.Length == 0) c.Title = c.Username.Length > 0 ? c.Username : "Untitled";
        if (c.Type == ItemType.WiFi && c.Wifi != null && c.Title == "Untitled") c.Title = c.Wifi.NetworkName;
        return c;
    }

    /// <summary>Bitwarden's "fields" column ("Name: Value" per line), including the extras Lockbox's own export writes there.</summary>
    private static void ReadFieldLines(ImportCandidate c, string fields)
    {
        if (fields.Length == 0) return;
        var card = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        WifiSecurity? wifi = null;
        bool hiddenNetwork = false;

        foreach (var raw in fields.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            int colon = line.IndexOf(": ", StringComparison.Ordinal);
            string name = colon > 0 ? line[..colon].Trim() : "Field";
            string value = colon > 0 ? line[(colon + 2)..].Trim() : line;

            switch (name.ToLowerInvariant())
            {
                case "2fa":
                    c.TwoFactorEnabled = true;
                    c.TwoFactorMethod = ParseMethod(value);
                    continue;
                case "tags":
                    foreach (var t in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) AddTag(c, t);
                    continue;
                case "wi-fi security":
                    wifi = ParseWifi(value);
                    continue;
                case "hidden network":
                    hiddenNetwork = value.StartsWith('y');
                    continue;
                case "attachments (not exported)":
                    c.Warnings.Add($"Attachments weren't in the export ({value}). Add them again by hand.");
                    continue;
                case "cardholder" or "card number" or "expiry" or "security code" or "pin" when c.Type == ItemType.SecureNote:
                    card[name] = value;
                    continue;
            }
            c.Fields.Add(new CustomField(name, value, LooksSecret(name)));
        }

        if (card.ContainsKey("card number"))
        {
            c.Type = ItemType.Card;
            c.Card = new CardData
            {
                CardholderName = card.GetValueOrDefault("cardholder", ""), Number = card.GetValueOrDefault("card number", ""),
                Expiry = card.GetValueOrDefault("expiry", ""), SecurityCode = card.GetValueOrDefault("security code", ""),
                Pin = card.GetValueOrDefault("pin", ""),
            };
        }
        else foreach (var (n, v) in card) c.Fields.Add(new CustomField(n, v, LooksSecret(n)));

        if (wifi is { } sec && c.Type == ItemType.Login)
        {
            c.Type = ItemType.WiFi;
            c.Wifi = new WifiData { NetworkName = c.Username, Security = sec, HiddenNetwork = hiddenNetwork };
            c.Username = "";
            c.Url = "";
        }
    }

    /// <summary>LastPass keeps typed items as secure notes with "Key:Value" lines; turn its payment cards into real cards.</summary>
    private static void ReadLastPassNote(ImportCandidate c)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in c.Notes.Split('\n'))
        {
            int colon = line.IndexOf(':');
            if (colon > 0) values[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        if (!values.TryGetValue("NoteType", out var kind) || !kind.Equals("Credit Card", StringComparison.OrdinalIgnoreCase)) return;
        c.Type = ItemType.Card;
        c.Card = new CardData
        {
            CardholderName = values.GetValueOrDefault("Name on Card", ""), Number = values.GetValueOrDefault("Number", ""),
            Expiry = LastPassExpiry(values.GetValueOrDefault("Expiration Date", "")), SecurityCode = values.GetValueOrDefault("Security Code", ""),
        };
        c.Notes = values.GetValueOrDefault("Notes", "");
    }

    // "January,2027" → "01/27"
    private static string LastPassExpiry(string s)
    {
        var parts = s.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length == 2 && DateTime.TryParseExact(parts[0], "MMMM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var m)
            && parts[1].Length == 4)
            return $"{m.Month:00}/{parts[1][2..]}";
        return s;
    }

    /// <summary>Lockbox's export appends "Backup codes:" and the codes to the notes; give them back to the backup codes box.</summary>
    private static void SplitBackupCodes(ImportCandidate c)
    {
        var m = BackupCodesRx().Match(c.Notes);
        if (!m.Success) return;
        c.BackupCodes = m.Groups[1].Value.Trim();
        c.Notes = c.Notes[..m.Index].Trim();
        c.TwoFactorEnabled = true;
    }

    private static void AddTag(ImportCandidate c, string tag)
    {
        if (tag.Length > 0 && !c.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) c.Tags.Add(tag);
    }

    private static string CleanFolder(string folder)
    {
        folder = folder.Trim();
        if (folder.StartsWith("Root/", StringComparison.OrdinalIgnoreCase)) folder = folder[5..];   // KeePassXC
        return folder is "Root" or "(none)" or "No Folder" ? "" : folder;
    }

    private static bool LooksSecret(string name) => SecretNameRx().IsMatch(name);

    private static TwoFactorMethod ParseMethod(string s) => s.ToLowerInvariant() switch
    {
        var x when x.Contains("app") || x.Contains("totp") || x.Contains("authenticator") => TwoFactorMethod.AuthenticatorApp,
        var x when x.Contains("sms") || x.Contains("text") => TwoFactorMethod.Sms,
        var x when x.Contains("email") => TwoFactorMethod.Email,
        var x when x.Contains("key") || x.Contains("passkey") => TwoFactorMethod.HardwareKey,
        "on" or "" => TwoFactorMethod.None,
        _ => TwoFactorMethod.Other,
    };

    private static WifiSecurity ParseWifi(string s) => s.ToUpperInvariant() switch
    {
        var x when x.StartsWith("WPA3") => WifiSecurity.Wpa3,
        var x when x.StartsWith("WPA/") => WifiSecurity.WpaWpa2,
        var x when x.StartsWith("WEP") => WifiSecurity.Wep,
        var x when x.StartsWith("OPEN") => WifiSecurity.Open,
        _ => WifiSecurity.Wpa2,
    };

    // ───────────────────────── names from web addresses ─────────────────────────

    /// <summary>
    /// The part of a web address that identifies the site, e.g. "accounts.google.com" → "google.com",
    /// "www.bbc.co.uk" → "bbc.co.uk". Used for titles and for spotting logins you already have.
    /// </summary>
    public static string SiteKey(string url)
    {
        string host = Host(url);
        if (host.Length == 0 || IPv4Rx().IsMatch(host) || !host.Contains('.')) return host;
        var labels = host.Split('.');
        int keep = labels.Length >= 3 && labels[^1].Length == 2 && SecondLevel.Contains(labels[^2]) ? 3 : 2;
        return string.Join('.', labels[^Math.Min(keep, labels.Length)..]);
    }

    private static readonly HashSet<string> SecondLevel = ["co", "com", "org", "net", "ac", "gov", "edu", "ne", "or"];

    private static string Host(string url)
    {
        url = url.Trim();
        if (url.Length == 0) return "";
        if (url.StartsWith("android://", StringComparison.OrdinalIgnoreCase))
        {
            int at = url.IndexOf('@');
            return at > 0 ? url[(at + 1)..].TrimEnd('/').ToLowerInvariant() : "";
        }
        if (!url.Contains("://")) url = "https://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return "";
        string host = u.Host.ToLowerInvariant();
        return host.StartsWith("www.") ? host[4..] : host;
    }

    private static string TitleFromUrl(string url)
    {
        if (url.StartsWith("android://", StringComparison.OrdinalIgnoreCase))
        {
            // "com.spotify.music" → "Spotify (Android app)"
            var name = Host(url).Split('.').FirstOrDefault(p => p.Length > 2 && p is not ("com" or "org" or "net" or "android" or "app" or "apps" or "mobile"));
            return name == null ? "Android app" : char.ToUpperInvariant(name[0]) + name[1..] + " (Android app)";
        }
        return SiteKey(url);
    }

    // ───────────────────────── reading the file ─────────────────────────

    private sealed record Layout(char Delimiter, Dictionary<Col, int> Map, string Source);

    private static Layout? Detect(string text)
    {
        string first = FirstLine(text);
        if (first.Length == 0) return null;

        Layout? best = null;
        foreach (char delim in new[] { ',', ';', '\t' })
        {
            var header = ReadRows(first, delim).FirstOrDefault();
            if (header == null || header.Count < 2) continue;
            var map = new Dictionary<Col, int>();
            for (int i = 0; i < header.Count; i++)
                if (Columns.TryGetValue(Key(header[i]), out var col)) map.TryAdd(col, i);
            // A password column plus two more we know: enough to be sure this is an export, not a note that starts "name, password".
            bool usable = map.ContainsKey(Col.Password) && map.Count >= 3
                          && (map.ContainsKey(Col.Url) || map.ContainsKey(Col.Title) || map.ContainsKey(Col.Username));
            if (usable && (best == null || map.Count > best.Map.Count))
                best = new Layout(delim, map, NameSource(header.Select(Key).ToHashSet()));
        }
        return best;
    }

    private static string NameSource(HashSet<string> h)
    {
        if (h.Contains("loginuri") && h.Contains("fields")) return "Bitwarden or Lockbox";
        if (h.Contains("formactionorigin") || h.Contains("httprealm")) return "Firefox";
        if (h.Contains("grouping") && h.Contains("extra")) return "LastPass";
        if (h.Contains("username2")) return "Dashlane";
        if (h.Contains("vault") && h.Contains("createtime")) return "Proton Pass";
        if (h.Contains("group") && h.Contains("lastmodified")) return "KeePassXC";
        if (h.Contains("otpauth") && h.Contains("archived")) return "1Password";
        if (h.Contains("otpauth") && h.Contains("title")) return "Safari / Apple Passwords";
        if (h.SetEquals(["name", "url", "username", "password"]) || h.SetEquals(["name", "url", "username", "password", "note"]))
            return "Chrome, Edge or Brave";
        return "CSV";
    }

    private static string FirstLine(string text)
    {
        text = text.TrimStart('﻿', ' ', '\r', '\n', '\t');
        int end = text.IndexOfAny(['\r', '\n']);
        return end < 0 ? text : text[..end];
    }

    /// <summary>RFC 4180: quoted cells, "" for a quote, and line breaks inside quotes.</summary>
    internal static List<List<string>> ReadRows(string text, char delimiter)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        bool quoted = false, any = false;
        int i = text.Length > 0 && text[0] == '﻿' ? 1 : 0;

        for (; i < text.Length; i++)
        {
            char ch = text[i];
            if (quoted)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else quoted = false;
                }
                else if (ch == '\r') { if (i + 1 >= text.Length || text[i + 1] != '\n') cell.Append('\n'); }
                else cell.Append(ch);
                continue;
            }

            if (ch == '"' && cell.Length == 0) { quoted = true; any = true; }
            else if (ch == delimiter) { row.Add(cell.ToString()); cell.Clear(); any = true; }
            else if (ch is '\r' or '\n')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                if (any || cell.Length > 0) { row.Add(cell.ToString()); rows.Add(row); }
                row = []; cell.Clear(); any = false;
            }
            else { cell.Append(ch); any = true; }
        }
        if (any || cell.Length > 0) { row.Add(cell.ToString()); rows.Add(row); }
        return rows;
    }

    [GeneratedRegex(@"(?:^|\n)\s*Backup codes:\s*\n([\s\S]+)$", RegexOptions.IgnoreCase)] private static partial Regex BackupCodesRx();
    [GeneratedRegex(@"pass|pin|secret|key|code|answer|token|cvv|cvc", RegexOptions.IgnoreCase)] private static partial Regex SecretNameRx();
    [GeneratedRegex(@"^\d{1,3}(\.\d{1,3}){3}$")] private static partial Regex IPv4Rx();
}
