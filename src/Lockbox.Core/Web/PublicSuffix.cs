using System.Globalization;
using System.Net;

namespace Lockbox.Core.Web;

/// <summary>
/// The Public Suffix List (publicsuffix.org), as browsers use it to decide where a "site" begins.
/// "mail.google.com" and "accounts.google.com" are the same site (google.com), but
/// "alice.github.io" and "evil.github.io" are different sites, because anyone can get a github.io name.
/// The list is embedded (Resources/public_suffix_list.dat, MPL 2.0), so this never goes online.
/// </summary>
public static class PublicSuffix
{
    private sealed class Rules
    {
        public readonly HashSet<string> Exact = new(StringComparer.Ordinal);
        public readonly HashSet<string> Wildcard = new(StringComparer.Ordinal);   // "*.ck" stored as "ck"
        public readonly HashSet<string> Exception = new(StringComparer.Ordinal);  // "!www.ck" stored as "www.ck"
    }

    private static readonly Lazy<Rules> _rules = new(Load);
    private static readonly IdnMapping Idn = new();

    private static Rules Load()
    {
        var r = new Rules();
        using var stream = typeof(PublicSuffix).Assembly.GetManifestResourceStream("Lockbox.Core.Resources.public_suffix_list.dat")
                           ?? throw new InvalidOperationException("Public suffix list resource missing.");
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line.StartsWith("//")) continue;
            string rule = ToAscii(line.Trim());
            if (rule.StartsWith('!')) r.Exception.Add(rule[1..]);
            else if (rule.StartsWith("*.")) r.Wildcard.Add(rule[2..]);
            else r.Exact.Add(rule);
        }
        return r;
    }

    private static string ToAscii(string s)
    {
        try { return Idn.GetAscii(s).ToLowerInvariant(); }
        catch (ArgumentException) { return s.ToLowerInvariant(); }
    }

    /// <summary>Normalizes a host name: lower case, punycode, no trailing dot. Empty if it isn't a usable name.</summary>
    public static string NormalizeHost(string host)
    {
        host = host.Trim().TrimEnd('.');
        return host.Length == 0 ? "" : ToAscii(host);
    }

    public static bool IsIpAddress(string host) =>
        IPAddress.TryParse(host.Trim('[', ']'), out _);

    /// <summary>The public suffix of a host, e.g. "co.uk" for "www.bbc.co.uk", "github.io" for "alice.github.io".</summary>
    public static string SuffixOf(string host)
    {
        host = NormalizeHost(host);
        if (host.Length == 0 || IsIpAddress(host)) return "";
        var labels = host.Split('.');
        int n = labels.Length;
        var r = _rules.Value;

        int best = 1;              // the implicit "*" rule: any single label is a suffix
        int? exception = null;
        for (int i = 0; i < n; i++)
        {
            int count = n - i;
            string candidate = string.Join('.', labels, i, count);
            if (r.Exception.Contains(candidate)) exception = Math.Max(exception ?? 0, count - 1);
            if (r.Exact.Contains(candidate)) best = Math.Max(best, count);
            if (i + 1 < n && r.Wildcard.Contains(string.Join('.', labels, i + 1, count - 1))) best = Math.Max(best, count);
        }
        int suffixLabels = exception ?? best;
        return string.Join('.', labels, n - suffixLabels, suffixLabels);
    }

    /// <summary>
    /// The site a host belongs to: the public suffix plus one more label ("bbc.co.uk", "alice.github.io").
    /// Null for IP addresses, single-label names like "localhost", and hosts that are a public suffix themselves.
    /// </summary>
    public static string? RegistrableDomain(string host)
    {
        host = NormalizeHost(host);
        if (host.Length == 0 || IsIpAddress(host) || !host.Contains('.')) return null;
        string suffix = SuffixOf(host);
        if (suffix.Length == 0 || host == suffix) return null;
        string rest = host[..^(suffix.Length + 1)];
        string label = rest[(rest.LastIndexOf('.') + 1)..];
        return label + "." + suffix;
    }
}
