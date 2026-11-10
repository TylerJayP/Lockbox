namespace Lockbox.Core.Web;

/// <summary>Which saved logins belong to a web page. Used by the browser extension, auto-type and imports.</summary>
public static class WebSite
{
    public enum Match { None, SameSite, SameHost }

    /// <summary>A web address split into the parts that matter for matching.</summary>
    public sealed record Address(string Scheme, string Host, int Port, string Site)
    {
        public bool IsSecure => Scheme == "https";
        public string Origin => Port == DefaultPort(Scheme) ? $"{Scheme}://{Host}" : $"{Scheme}://{Host}:{Port}";
    }

    /// <summary>
    /// Reads an http(s) address. Addresses saved without a scheme ("github.com") are taken as https.
    /// Returns null for anything else (android://, file://, chrome://, garbage).
    /// </summary>
    public static Address? Parse(string? url)
    {
        url = (url ?? "").Trim();
        if (url.Length == 0) return null;
        if (!url.Contains("://")) url = "https://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return null;
        string scheme = u.Scheme.ToLowerInvariant();
        if (scheme is not ("http" or "https")) return null;
        string host = PublicSuffix.NormalizeHost(u.IdnHost);
        if (host.Length == 0) return null;
        string site = PublicSuffix.RegistrableDomain(host) ?? host;
        return new Address(scheme, host, u.Port, site);
    }

    /// <summary>The site an address belongs to ("accounts.google.com" → "google.com"), or "" if it isn't a web address.</summary>
    public static string SiteOf(string? url) => Parse(url)?.Site ?? "";

    /// <summary>
    /// Whether a login saved for <paramref name="savedUrl"/> belongs on the page at <paramref name="pageUrl"/>.
    /// Same site is enough for normal domains. IP addresses, "localhost" and other names without a
    /// public suffix must match exactly, including the port. A port in the saved address must always match.
    /// </summary>
    public static Match Compare(string? savedUrl, string? pageUrl)
    {
        var saved = Parse(savedUrl);
        var page = Parse(pageUrl);
        if (saved == null || page == null) return Match.None;

        bool savedHasPort = HasExplicitPort(savedUrl!);
        if (savedHasPort && saved.Port != page.Port) return Match.None;

        if (saved.Host == page.Host) return Match.SameHost;
        bool exactOnly = PublicSuffix.RegistrableDomain(saved.Host) == null;
        if (exactOnly) return Match.None;
        return saved.Site == page.Site ? Match.SameSite : Match.None;
    }

    private static bool HasExplicitPort(string url)
    {
        if (!url.Contains("://")) url = "https://" + url.Trim();
        return Uri.TryCreate(url, UriKind.Absolute, out var u) && !u.IsDefaultPort;
    }

    private static int DefaultPort(string scheme) => scheme == "http" ? 80 : 443;
}
