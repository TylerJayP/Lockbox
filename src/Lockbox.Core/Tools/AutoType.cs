using System.Text;
using System.Text.RegularExpressions;
using Lockbox.Core.Import;
using Lockbox.Core.Model;

namespace Lockbox.Core.Tools;

/// <summary>One thing for auto-type to do: type some text, press a key, or wait.</summary>
public sealed record AutoTypeStep(AutoTypeStep.Kinds Kind, string Text = "", int DelayMs = 0)
{
    public enum Kinds { Text, Tab, Enter, Delay }

    public static AutoTypeStep Type(string text) => new(Kinds.Text, text);
    public static readonly AutoTypeStep Tab = new(Kinds.Tab);
    public static readonly AutoTypeStep Enter = new(Kinds.Enter);
    public static AutoTypeStep Wait(int ms) => new(Kinds.Delay, DelayMs: ms);

    public override string ToString() => Kind switch
    {
        Kinds.Text => "text",
        Kinds.Delay => $"wait {DelayMs} ms",
        _ => Kind.ToString(),
    };
}

/// <summary>
/// What auto-type types for a login. By default: username, Tab, password, Enter (or just password,
/// Enter when there's no username). A custom field named "Auto-type" overrides it, using
/// {USERNAME}, {PASSWORD}, {TAB}, {ENTER} and {DELAY 1000}; anything else is typed as written.
/// Useful for two-step sign-ins: {USERNAME}{ENTER}{DELAY 1500}{PASSWORD}{ENTER}
/// </summary>
public static partial class AutoTypeSequence
{
    public const string FieldName = "Auto-type";
    public const string Default = "{USERNAME}{TAB}{PASSWORD}{ENTER}";
    public const int MaxDelayMs = 10_000;

    public static string SequenceFor(Entry entry) =>
        entry.Fields.FirstOrDefault(f => f.Name.Trim().Equals(FieldName, StringComparison.OrdinalIgnoreCase))?.Value is { Length: > 0 } custom
            ? custom
            : entry.Username.Length > 0 ? Default : "{PASSWORD}{ENTER}";

    public static IReadOnlyList<AutoTypeStep> For(Entry entry) => Parse(SequenceFor(entry), entry.Username, entry.Password);

    public static IReadOnlyList<AutoTypeStep> Parse(string sequence, string username, string password)
    {
        var steps = new List<AutoTypeStep>();
        var text = new StringBuilder();
        void Flush() { if (text.Length > 0) { steps.Add(AutoTypeStep.Type(text.ToString())); text.Clear(); } }

        int i = 0;
        while (i < sequence.Length)
        {
            var m = TokenRx().Match(sequence, i);
            if (!m.Success || m.Index != i)
            {
                text.Append(sequence[i++]);
                continue;
            }
            string name = m.Groups[1].Value.ToUpperInvariant();
            string arg = m.Groups[2].Value;
            switch (name)
            {
                case "USERNAME": text.Append(username); break;
                case "PASSWORD": text.Append(password); break;
                case "TAB": Flush(); steps.Add(AutoTypeStep.Tab); break;
                case "ENTER": Flush(); steps.Add(AutoTypeStep.Enter); break;
                case "DELAY" when int.TryParse(arg, out int ms):
                    Flush(); steps.Add(AutoTypeStep.Wait(Math.Clamp(ms, 0, MaxDelayMs))); break;
                case "{" or "}": text.Append(name); break;   // {{} and {}} type a literal brace
                default: text.Append(m.Value); break;        // unknown: type it as written
            }
            i += m.Length;
        }
        Flush();
        return steps;
    }

    [GeneratedRegex(@"\{([A-Za-z]+|\{|\})(?:[ =](\d+))?\}")] private static partial Regex TokenRx();
}

/// <summary>Finds the logins that belong to a window, by its title ("Sign in to GitHub · GitHub — Mozilla Firefox").</summary>
public static partial class AutoTypeMatcher
{
    public sealed record Match(Entry Entry, string MatchedOn, int Score);

    /// <summary>Best matches first. Only logins with a password are considered.</summary>
    public static IReadOnlyList<Match> Find(IEnumerable<Entry> entries, string windowTitle)
    {
        string title = CleanWindowTitle(windowTitle).ToLowerInvariant();
        if (title.Length == 0) return [];

        var found = new List<Match>();
        foreach (var e in entries)
        {
            if (e.Type != ItemType.Login || e.Password.Length == 0) continue;
            Match? best = null;
            foreach (var (token, score) in Tokens(e))
                if (ContainsWord(title, token) && (best == null || score > best.Score))
                    best = new Match(e, token, score);
            if (best != null) found.Add(best);
        }
        return [.. found.OrderByDescending(m => m.Score).ThenByDescending(m => m.MatchedOn.Length).ThenBy(m => m.Entry.Title, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>Drops the browser's name and tab counts from a window title, so "Google Chrome" doesn't match a login called Google.</summary>
    public static string CleanWindowTitle(string title)
    {
        title = title.Replace("​", "").Trim();
        title = MorePagesRx().Replace(title, "");
        title = BrowserSuffixRx().Replace(title, "").Trim();
        return title;
    }

    private static IEnumerable<(string Token, int Score)> Tokens(Entry e)
    {
        string site = CsvImporter.SiteKey(e.Url);
        if (site.Length > 0)
        {
            yield return (site, 30);                                   // "github.com"
            string name = site.Split('.')[0];
            if (name.Length >= 3) yield return (name, 20);            // "github"
        }
        string t = e.Title.Trim().ToLowerInvariant();
        if (t.Length >= 3) yield return (t, 10);
    }

    private static bool ContainsWord(string haystack, string token)
    {
        int at = 0;
        while ((at = haystack.IndexOf(token, at, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            bool startOk = at == 0 || !char.IsLetterOrDigit(haystack[at - 1]);
            int end = at + token.Length;
            bool endOk = end == haystack.Length || !char.IsLetterOrDigit(haystack[end]);
            if (startOk && endOk) return true;
            at++;
        }
        return false;
    }

    [GeneratedRegex(@"\s+and \d+ more pages?\b", RegexOptions.IgnoreCase)] private static partial Regex MorePagesRx();

    // "… - Google Chrome", "… — Mozilla Firefox", and Edge's "… - Personal - Microsoft Edge" (its usual profile names only,
    // so a real part of the page title like "Google Accounts" is never thrown away).
    [GeneratedRegex(@"\s[-—–]\s(?:(?:Personal|Work|School|Default|Profile \d+)\s[-—–]\s(?=Microsoft))?(?:Google Chrome|Chromium|Mozilla Firefox|Firefox|Microsoft\s?Edge|Brave|Opera|Vivaldi|Safari|Arc|Waterfox|LibreWolf)$", RegexOptions.IgnoreCase)]
    private static partial Regex BrowserSuffixRx();
}
