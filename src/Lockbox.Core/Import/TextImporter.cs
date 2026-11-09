using System.Text;
using System.Text.RegularExpressions;
using Lockbox.Core.Model;

namespace Lockbox.Core.Import;

/// <summary>A proposed entry from the import. Everything is editable on the review screen before it's saved.</summary>
public sealed class ImportCandidate
{
    public ItemType Type { get; set; } = ItemType.Login;
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public bool TwoFactorEnabled { get; set; }
    public TwoFactorMethod TwoFactorMethod { get; set; }
    public string BackupCodes { get; set; } = "";
    public string Notes { get; set; } = "";
    public List<string> Tags { get; } = [];
    public List<CustomField> Fields { get; } = [];
    public CardData? Card { get; set; }
    public WifiData? Wifi { get; set; }
    public bool Reprompt { get; set; }

    /// <summary>Start unticked on the review screen (e.g. a duplicate row in the same export).</summary>
    public bool SuggestSkip { get; set; }

    /// <summary>The original lines this candidate came from, shown on the review screen.</summary>
    public string SourceText { get; set; } = "";
    public List<string> Warnings { get; } = [];

    internal readonly StringBuilder Source = new();
    internal readonly HashSet<string> CodeShapes = [];
    internal bool HasCredentials => Username.Length > 0 || Password.Length > 0;
    internal bool IsTitleOnly => Title.Length > 0 && Url.Length == 0 && !HasCredentials && BackupCodes.Length == 0 && Notes.Length == 0;
    internal bool IsEmpty => Title.Length == 0 && Url.Length == 0 && !HasCredentials && BackupCodes.Length == 0 && Notes.Length == 0;

    public Entry ToEntry()
    {
        bool twoFactor = Type == ItemType.Login && (TwoFactorEnabled || BackupCodes.Trim().Length > 0);
        return new Entry
        {
            Type = Type,
            Title = Title.Trim(),
            Url = Type == ItemType.Login ? Url.Trim() : "",
            Username = Type == ItemType.Login ? Username.Trim() : "",
            Password = Type is ItemType.Login or ItemType.WiFi ? Password : "",
            TwoFactorEnabled = twoFactor,
            TwoFactorMethod = !twoFactor ? TwoFactorMethod.None
                : TwoFactorMethod == TwoFactorMethod.None ? TwoFactorMethod.AuthenticatorApp : TwoFactorMethod,
            Card = Type == ItemType.Card ? Card : null,
            Wifi = Type == ItemType.WiFi ? Wifi : null,
            Fields = [.. Fields],
            Tags = [.. Tags],
            Reprompt = Reprompt,
            Notes = Notes.Trim(),
        };
    }

    /// <summary>A short note of what else comes along that the review screen doesn't show as boxes.</summary>
    public string ExtrasSummary
    {
        get
        {
            var parts = new List<string>();
            if (Type == ItemType.Card) parts.Add("payment card" + (Card?.Number.Length >= 4 ? $" ending {Card.Number[^4..]}" : ""));
            if (Type == ItemType.WiFi) parts.Add($"Wi-Fi network \"{Wifi?.NetworkName}\"");
            if (Type == ItemType.SecureNote) parts.Add("secure note");
            if (Fields.Any(f => f.Name == CsvImporter.TotpFieldName)) parts.Add("authenticator key (kept as a hidden field)");
            int other = Fields.Count(f => f.Name != CsvImporter.TotpFieldName);
            if (other > 0) parts.Add(other == 1 ? "1 custom field" : $"{other} custom fields");
            if (Tags.Count > 0) parts.Add("tags: " + string.Join(", ", Tags));
            if (Reprompt) parts.Add("asks for the master password");
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>
/// Best-effort parser for a hand-written passwords .txt file. Understands, in any mix:
///   • labeled blocks      ("Netflix" / "email: …" / "password: …" / "backup codes:" + code lines)
///   • one-line records    ("Amazon - me@x.com - pass", "Steam | user | pass", "Discord: me@x.com / pass")
///   • unlabeled run-ons   ("Spotify" / "me@x.com" / "pass" / "Hulu" / …)
/// Nothing is guessed silently: guesses get a warning so they stand out on the review screen.
/// </summary>
public static partial class TextImporter
{
    private enum Field { Title, Username, Email, Password, Url, TwoFactor, Codes, Notes, Pin }

    private static readonly Dictionary<string, Field> Labels = BuildLabels();

    private static Dictionary<string, Field> BuildLabels()
    {
        var d = new Dictionary<string, Field>(StringComparer.OrdinalIgnoreCase);
        void Add(Field f, params string[] names) { foreach (var n in names) d[Norm(n)] = f; }
        Add(Field.Title, "title", "service", "site name", "app", "account name");
        Add(Field.Username, "user", "username", "user name", "login", "login id", "userid", "user id", "account", "acct", "handle", "id", "gamertag", "screen name", "uname", "un");
        Add(Field.Email, "email", "e-mail", "mail", "email address", "e-mail address");
        Add(Field.Password, "password", "pass", "pw", "pwd", "passwd", "passcode", "passphrase", "pword", "p");
        Add(Field.Pin, "pin", "pin code");
        Add(Field.Url, "url", "site", "website", "web", "link", "domain", "web site", "web address");
        Add(Field.TwoFactor, "2fa", "mfa", "two factor", "two-factor", "2 factor", "totp", "authenticator", "auth app", "auth", "2sv", "two step", "2-step", "2 step verification");
        Add(Field.Codes, "backup", "backup codes", "backup code", "backup keys", "backup key", "recovery", "recovery codes", "recovery code",
            "recovery key", "recovery keys", "codes", "2fa backup", "2fa backup codes", "2fa codes", "emergency codes", "scratch codes", "one time codes", "mfa backup codes");
        Add(Field.Notes, "note", "notes", "comment", "comments", "info", "misc", "security question", "security answer", "question", "answer", "hint");
        return d;
    }

    private static string Norm(string label) => SpaceRx().Replace(NonWordRx().Replace(label.ToLowerInvariant(), " "), " ").Trim();

    public static List<ImportCandidate> Parse(string text)
    {
        var results = new List<ImportCandidate>();
        ImportCandidate? cur = null;
        bool inCodes = false;

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        bool danglingTitle = false;

        void Finish()
        {
            // A lone title with nothing under it (e.g. a section heading) isn't an entry.
            if (cur != null && !cur.IsEmpty && !cur.IsTitleOnly) results.Add(cur);
            cur = null;
            inCodes = false;
        }
        ImportCandidate Start(string sourceLine)
        {
            // The line that starts a new entry was already recorded under the previous one; move it.
            if (cur != null)
            {
                string tail = sourceLine + Environment.NewLine;
                if (cur.Source.ToString().EndsWith(tail, StringComparison.Ordinal)) cur.Source.Length -= tail.Length;
            }
            Finish();
            cur = new ImportCandidate();
            cur.Source.AppendLine(sourceLine);
            return cur;
        }

        for (int i = 0; i < lines.Length; i++)
        {
            string raw = lines[i];
            string line = raw.Trim().TrimStart('﻿');

            // A blank line ends a block, unless we're collecting backup codes and more codes follow.
            if (line.Length == 0)
            {
                if (inCodes && cur != null && NextNonBlank(lines, i) is { } nb && AcceptsAsCode(cur, nb)) continue;
                if (cur != null && cur.HasCredentials) Finish();
                else if (cur != null && cur.IsTitleOnly) danglingTitle = true;
                inCodes = false;
                continue;
            }
            if (IsDecoration(line) || SectionHeaderRx().IsMatch(line))
            {
                if (cur != null && cur.HasCredentials) Finish();
                else if (cur != null && cur.IsTitleOnly) { cur = null; }
                danglingTitle = false;
                continue;
            }

            // A title sitting alone above a blank line keeps its labeled fields ("Netflix" / "" / "email: …"),
            // but an unlabeled line after the gap means the title was really a heading.
            if (danglingTitle)
            {
                danglingTitle = false;
                if (TryLabel(line, out _, out _, out _) != true) cur = null;
            }

            cur?.Source.AppendLine(raw);

            // ── Backup-code continuation lines ──
            if (inCodes && cur != null)
            {
                if (AcceptsAsCode(cur, line) && TryLabel(line, out _, out _, out _) != true)
                {
                    AddCodes(cur, line);
                    continue;
                }
                inCodes = false;
            }

            // ── "label: value" ──
            if (TryLabel(line, out var field, out var value, out var labelText) == true)
            {
                cur ??= Start(raw);
                switch (field)
                {
                    case Field.Title:
                        if (cur.Title.Length > 0 && cur.HasCredentials) { cur = Start(raw); }
                        cur.Title = value;
                        break;
                    case Field.Username:
                    case Field.Email:
                        if (cur.Username.Length > 0 && cur.Password.Length > 0) { cur = Start(raw); cur.Warnings.Add("No name found for this login."); }
                        if (cur.Username.Length == 0) cur.Username = value;
                        else AppendLine(cur, c => c.Notes, (c, v) => c.Notes = v, $"{labelText}: {value}");
                        break;
                    case Field.Password:
                        if (cur.Password.Length > 0) { cur = Start(raw); cur.Warnings.Add("No name found for this login."); }
                        cur.Password = value;
                        break;
                    case Field.Pin:
                        if (cur.Password.Length == 0 && value.Length > 0) { cur.Password = value; cur.Warnings.Add("Used the PIN as the password."); }
                        else AppendLine(cur, c => c.Notes, (c, v) => c.Notes = v, $"{labelText}: {value}");
                        break;
                    case Field.Url:
                        cur.Url = value;
                        if (cur.Title.Length == 0) cur.Title = TitleFromUrl(value);
                        break;
                    case Field.TwoFactor:
                        ApplyTwoFactor(cur, value);
                        break;
                    case Field.Codes:
                        inCodes = true;
                        if (value.Length > 0) AddCodes(cur, value);
                        break;
                    case Field.Notes:
                        AppendLine(cur, c => c.Notes, (c, v) => c.Notes = v, string.Equals(labelText, "notes", StringComparison.OrdinalIgnoreCase) || string.Equals(labelText, "note", StringComparison.OrdinalIgnoreCase) ? value : $"{labelText}: {value}");
                        break;
                }
                continue;
            }

            // ── One-line record: "Amazon - me@x.com - pass", "Discord: me@x.com / pass" ──
            if (TrySplitRecord(line, out var rec))
            {
                cur = Start(raw);
                ApplyRecord(cur, rec);
                continue;
            }

            // ── Bare URL ──
            if (UrlRx().IsMatch(line))
            {
                if (cur == null || cur.Url.Length > 0 || cur.HasCredentials) cur = Start(raw);
                cur.Url = line;
                if (cur.Title.Length == 0) cur.Title = TitleFromUrl(line);
                continue;
            }

            // ── A run of 4+ code-shaped lines right after a login = probably backup codes ──
            if (cur != null && cur.Password.Length > 0 && IsCodeLike(line) && CountCodeRun(lines, i) >= 4)
            {
                inCodes = true;
                AddCodes(cur, line);
                if (!cur.Warnings.Contains(GuessedCodes)) cur.Warnings.Add(GuessedCodes);
                continue;
            }

            // ── Unlabeled line ──
            bool singleToken = !line.Contains(' ');
            if (cur == null || cur.Password.Length > 0)
            {
                cur = Start(raw);
                cur.Title = line;
            }
            else if (cur.Title.Length == 0 && cur.Username.Length == 0 && !EmailRx().IsMatch(line))
            {
                cur.Title = line;
            }
            else if (cur.Username.Length == 0 && singleToken)
            {
                cur.Username = line;
                if (!EmailRx().IsMatch(line)) cur.Warnings.Add("Guessed the username from an unlabeled line.");
            }
            else if (cur.Password.Length == 0 && singleToken && cur.Username.Length > 0)
            {
                cur.Password = line;
                cur.Warnings.Add("Guessed the password from an unlabeled line.");
            }
            else
            {
                AppendLine(cur, c => c.Notes, (c, v) => c.Notes = v, line);
            }
        }
        Finish();

        foreach (var c in results) Finalize(c, results.IndexOf(c) + 1);
        return results;
    }

    private const string GuessedCodes = "Treated a run of code-like lines as backup codes.";

    private static void Finalize(ImportCandidate c, int n)
    {
        c.SourceText = c.Source.ToString().TrimEnd();
        c.Title = c.Title.Trim().TrimEnd(':').Trim();
        if (c.Title.Length == 0)
        {
            c.Title = c.Url.Length > 0 ? TitleFromUrl(c.Url)
                : EmailRx().IsMatch(c.Username) ? c.Username[(c.Username.IndexOf('@') + 1)..]
                : $"Imported login {n}";
            if (!c.Warnings.Contains("No name found for this login.")) c.Warnings.Add("No name found for this login.");
        }
        if (c.Password.Length == 0 && c.BackupCodes.Length == 0) c.Warnings.Add("No password found.");
        if (c.BackupCodes.Length > 0 && !c.TwoFactorEnabled) { c.TwoFactorEnabled = true; if (c.TwoFactorMethod == TwoFactorMethod.None) c.TwoFactorMethod = TwoFactorMethod.AuthenticatorApp; }
    }

    // ───────────────────────── line classification ─────────────────────────

    /// <summary>true = known label; false = looks like "X: y" but X isn't a known label; null = no label syntax.</summary>
    private static bool? TryLabel(string line, out Field field, out string value, out string labelText)
    {
        field = default; value = ""; labelText = "";
        if (UrlRx().IsMatch(line)) return null;
        var m = LabelRx().Match(line);
        if (!m.Success) return null;
        labelText = m.Groups["label"].Value.Trim();
        value = m.Groups["value"].Value.Trim();
        if (Labels.TryGetValue(Norm(labelText), out field)) return true;
        return false;
    }

    private static bool TrySplitRecord(string line, out string[] fields)
    {
        fields = [];
        // "Title: rest" where Title isn't a known label
        if (TryLabel(line, out _, out var value, out var label) == false && value.Length > 0)
        {
            var rest = SplitFields(value);
            if (rest.Length >= 2 || (rest.Length == 1 && (rest[0].Contains(':') && !UrlRx().IsMatch(rest[0]) || EmailRx().IsMatch(rest[0]))))
            {
                fields = [label, .. rest];
                return true;
            }
            return false;
        }
        if (TryLabel(line, out _, out _, out _) != null) return false;

        var parts = SplitFields(line);
        if (parts.Length is >= 2 and <= 4) { fields = parts; return true; }
        return false;
    }

    private static string[] SplitFields(string s) =>
        FieldSeparatorRx().Split(s).Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();

    private static void ApplyRecord(ImportCandidate c, string[] f)
    {
        // Expand "user:pass" packed into one field.
        var list = new List<string>();
        foreach (var p in f)
        {
            int colon = p.IndexOf(':');
            if (colon > 0 && colon < p.Length - 1 && !UrlRx().IsMatch(p) && !p.Contains(' ') && list.Count > 0) { list.Add(p[..colon]); list.Add(p[(colon + 1)..]); }
            else list.Add(p);
        }

        int i = 0;
        if (UrlRx().IsMatch(list[0])) { c.Url = list[0]; c.Title = TitleFromUrl(list[0]); i = 1; }
        else if (list.Count >= 3 || !LooksLikeLogin(list[0])) { c.Title = list[0]; i = 1; }
        else c.Warnings.Add("No name found for this login.");

        var rest = list.Skip(i).ToList();
        if (rest.Count >= 1) c.Username = rest[0];
        if (rest.Count >= 2) c.Password = rest[1];
        if (rest.Count >= 3) c.Notes = string.Join("\n", rest.Skip(2));
        if (rest.Count == 1 && !EmailRx().IsMatch(rest[0])) { c.Password = rest[0]; c.Username = ""; c.Warnings.Add("Only one value after the name; treated it as the password."); }
    }

    private static bool LooksLikeLogin(string s) => EmailRx().IsMatch(s);

    private static void ApplyTwoFactor(ImportCandidate c, string value)
    {
        string v = value.ToLowerInvariant();
        if (v is "" or "no" or "n" or "none" or "off" or "false" or "disabled" or "0") { c.TwoFactorEnabled = v.Length == 0 ? c.TwoFactorEnabled : false; return; }
        c.TwoFactorEnabled = true;
        c.TwoFactorMethod =
            v.Contains("sms") || v.Contains("text") || v.Contains("phone") ? TwoFactorMethod.Sms :
            v.Contains("email") || v.Contains("e-mail") ? TwoFactorMethod.Email :
            v.Contains("yubi") || v.Contains("key") || v.Contains("fido") || v.Contains("passkey") ? TwoFactorMethod.HardwareKey :
            TwoFactorMethod.AuthenticatorApp;
    }

    /// <summary>Backup-code shaped: 4–40 chars, letters/digits in groups split by spaces or dashes, with at least one digit.</summary>
    private static bool IsCodeLike(string line)
    {
        string s = StripEnumeration(line.Trim());
        return s.Length is >= 4 and <= 60 && CodeRx().IsMatch(s) && s.Any(char.IsDigit) && !EmailRx().IsMatch(s);
    }

    /// <summary>
    /// Inside an explicit "backup codes:" section, accept a line if it's code-shaped and either has a digit
    /// or every token has the same shape as codes already collected (so "Hulu" after "abcd-efgh" isn't swallowed).
    /// </summary>
    private static bool AcceptsAsCode(ImportCandidate c, string line)
    {
        if (IsCodeLike(line)) return true;
        string s = StripEnumeration(line.Trim());
        if (!CodeRx().IsMatch(s) || c.CodeShapes.Count == 0) return false;
        return s.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(t => c.CodeShapes.Contains(Shape(t)));
    }

    private static void AddCodes(ImportCandidate c, string line)
    {
        string s = StripEnumeration(line);
        foreach (var t in s.Split(' ', StringSplitOptions.RemoveEmptyEntries)) c.CodeShapes.Add(Shape(t));
        AppendLine(c, x => x.BackupCodes, (x, v) => x.BackupCodes = v, s);
    }

    private static string Shape(string token) => new(token.Select(ch => char.IsLetterOrDigit(ch) ? 'x' : ch).ToArray());

    private static string StripEnumeration(string s) => EnumRx().Replace(s.Trim(), "");

    private static int CountCodeRun(string[] lines, int start)
    {
        int n = 0;
        for (int j = start; j < lines.Length; j++)
        {
            var t = lines[j].Trim();
            if (t.Length == 0) continue;
            if (!IsCodeLike(t)) break;
            n++;
        }
        return n;
    }

    private static string? NextNonBlank(string[] lines, int i)
    {
        for (int j = i + 1; j < lines.Length; j++) if (lines[j].Trim().Length > 0) return lines[j].Trim();
        return null;
    }

    private static bool IsDecoration(string line) => DecorationRx().IsMatch(line);

    private static string TitleFromUrl(string url)
    {
        string s = url.Trim();
        if (!s.Contains("://")) s = "https://" + s;
        if (Uri.TryCreate(s, UriKind.Absolute, out var u))
        {
            string host = u.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? u.Host[4..] : u.Host;
            var parts = host.Split('.');
            string core = parts.Length >= 2 ? parts[^2] : host;
            return core.Length > 0 ? char.ToUpperInvariant(core[0]) + core[1..] : host;
        }
        return url;
    }

    private static void AppendLine(ImportCandidate c, Func<ImportCandidate, string> get, Action<ImportCandidate, string> set, string line)
    {
        string existing = get(c);
        set(c, existing.Length == 0 ? line : existing + "\n" + line);
    }

    // ───────────────────────── regexes ─────────────────────────

    [GeneratedRegex(@"^(?<label>[A-Za-z0-9][A-Za-z0-9 ._()\-/]{0,30}?)\s*(?::|=|\s-\s)\s*(?<value>.*)$")]
    private static partial Regex LabelRx();

    [GeneratedRegex(@"\s+\|\s+|\s*\|\s*|\t+|\s+-\s+|\s+/\s+|\s*,\s+|\s{3,}")]
    private static partial Regex FieldSeparatorRx();

    [GeneratedRegex(@"^(https?://\S+|www\.\S+|[a-z0-9][a-z0-9-]*(\.[a-z0-9-]+)*\.(com|net|org|io|gg|tv|edu|gov|co|us|app|dev|me|ai|uk|ca|de|xyz|info|biz|live|store|shop)(/\S*)?)$", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRx();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailRx();

    [GeneratedRegex(@"^[A-Za-z0-9]{3,16}([ \-][A-Za-z0-9]{3,16}){0,4}$")]
    private static partial Regex CodeRx();

    [GeneratedRegex(@"^(\d{1,2}[.)]|[-*•])\s+")]
    private static partial Regex EnumRx();

    [GeneratedRegex(@"^[\s\-=_*#~.]{3,}$")]
    private static partial Regex DecorationRx();

    [GeneratedRegex(@"^(#{1,6}\s+|[=*~_\-\[]{3,}\s*)[^=#*~_\[\]]+?\s*[=#*~_\-\]]*$")]
    private static partial Regex SectionHeaderRx();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonWordRx();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpaceRx();
}
