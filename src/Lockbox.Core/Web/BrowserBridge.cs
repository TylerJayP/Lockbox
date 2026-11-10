using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Lockbox.Core.Web;

/// <summary>
/// Names and rules shared by Lockbox, its browser helper (Lockbox.BrowserHost) and the tests.
///
/// How the pieces talk:
///   extension ⇄ (native messaging, stdin/stdout) ⇄ Lockbox.BrowserHost ⇄ (local pipe, this user only) ⇄ Lockbox
/// The browser only starts the helper for the extension IDs listed below, and Lockbox only answers
/// a browser after you approve it once (the pairing code shows in both places).
/// </summary>
public static class BrowserBridge
{
    /// <summary>The native messaging host name the extension connects to.</summary>
    public const string HostName = "com.tylerjayp.lockbox";

    /// <summary>The Firefox add-on ID (set in the extension's Firefox manifest).</summary>
    public const string FirefoxExtensionId = "lockbox@tylerjayp.github.io";

    /// <summary>
    /// Chrome, Edge and Brave extension IDs Lockbox works with. The first is the development build
    /// (fixed by the "key" in extension/manifest.json). After publishing on the Chrome Web Store, add the
    /// store's ID here too (see docs/BROWSER.md).
    /// </summary>
    public static readonly IReadOnlyList<string> ChromeExtensionIds =
    [
        "pnjncldjklkgjghfknegebmndmhmchmj",
    ];

    public static IEnumerable<string> ChromeOrigins => ChromeExtensionIds.Select(id => $"chrome-extension://{id}/");

    /// <summary>True for the callers the helper reports: one of our Chrome origins, or our Firefox add-on ID.</summary>
    public static bool IsTrustedCaller(string? caller) =>
        caller != null && (caller == FirefoxExtensionId || ChromeOrigins.Contains(caller));

    /// <summary>
    /// How browsers start a native host: Chrome, Edge and Brave pass the extension's origin,
    /// Firefox passes the host manifest's path and the add-on ID.
    /// </summary>
    public static string? CallerFromArgs(IEnumerable<string> args) =>
        args.FirstOrDefault(a => a.StartsWith("chrome-extension://", StringComparison.Ordinal))
        ?? args.FirstOrDefault(a => a == FirefoxExtensionId);

    /// <summary>The local pipe Lockbox listens on: one per Windows user, and only that user can open it.</summary>
    public static string PipeName =>
        Environment.GetEnvironmentVariable("LOCKBOX_BROWSER_PIPE") is { Length: > 0 } custom ? custom : "Lockbox.Browser." + UserTag();

    private static string UserTag()
    {
        string who = OperatingSystem.IsWindows() ? WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName : Environment.UserName;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(who)))[..16].ToLowerInvariant();
    }

    /// <summary>
    /// The six-digit code shown both in the extension and in Lockbox when a browser asks to connect,
    /// so you can tell it's really your browser asking. Computed the same way in extension/lib/pairing.js.
    /// </summary>
    public static string PairingCode(string clientId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(clientId));
        uint n = BinaryPrimitives.ReadUInt32BigEndian(hash) % 1_000_000;
        return $"{n / 1000:000} {n % 1000:000}";
    }

    /// <summary>Client IDs are random UUIDs the extension makes once per browser profile.</summary>
    public static bool IsValidClientId(string? id) =>
        id is { Length: >= 16 and <= 64 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
}
