using System.Text.Json;
using System.Text.Json.Nodes;
using Lockbox.Core.Web;
using Microsoft.Win32;

namespace Lockbox.App.Services;

/// <summary>
/// Tells installed browsers where the Lockbox helper is (native messaging host registration).
/// On Windows that's one registry value per browser under HKEY_CURRENT_USER pointing at a small JSON file;
/// nothing needs administrator rights. Done when the browser extension is turned on, refreshed at each
/// start (so the path survives updates), and removed when it's turned off or Lockbox is uninstalled.
/// </summary>
public static class BrowserHostRegistration
{
    private static readonly string[] ChromiumRegistryKeys =
    [
        @"Software\Google\Chrome", @"Software\Chromium", @"Software\Microsoft\Edge",
        @"Software\BraveSoftware\Brave-Browser", @"Software\Vivaldi",
    ];
    private const string FirefoxRegistryKey = @"Software\Mozilla";

    private static readonly string[] ChromiumLinuxDirs =
    [
        ".config/google-chrome", ".config/chromium", ".config/BraveSoftware/Brave-Browser", ".config/microsoft-edge", ".config/vivaldi",
    ];

    /// <summary>The helper browsers start. It ships next to Lockbox.exe.</summary>
    public static string HelperPath =>
        Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Lockbox.BrowserHost.exe" : "Lockbox.BrowserHost");

    public static bool HelperExists => File.Exists(HelperPath);

    private static string? CustomDir => Environment.GetEnvironmentVariable("LOCKBOX_BROWSER_HOST_DIR") is { Length: > 0 } d ? d : null;
    private static string ManifestDir => CustomDir ?? Path.Combine(AppSettings.Directory, "browser");
    private static string ChromeManifestPath => Path.Combine(ManifestDir, $"{BrowserBridge.HostName}.chrome.json");
    private static string FirefoxManifestPath => Path.Combine(ManifestDir, $"{BrowserBridge.HostName}.firefox.json");

    /// <summary>Registers the helper with every supported browser. Returns null on success, or what went wrong.</summary>
    public static string? Register()
    {
        if (!HelperExists)
            return "The browser helper (Lockbox.BrowserHost) is missing from Lockbox's folder. Reinstalling Lockbox fixes this.";
        try
        {
            Directory.CreateDirectory(ManifestDir);
            File.WriteAllText(ChromeManifestPath, Manifest("allowed_origins", new JsonArray([.. BrowserBridge.ChromeOrigins.Select(o => (JsonNode)o)])));
            File.WriteAllText(FirefoxManifestPath, Manifest("allowed_extensions", new JsonArray(BrowserBridge.FirefoxExtensionId)));
            if (CustomDir != null) return null;   // tests: files only, leave the real browsers alone

            if (OperatingSystem.IsWindows())
            {
                foreach (var key in ChromiumRegistryKeys) SetRegistry(key, ChromeManifestPath);
                SetRegistry(FirefoxRegistryKey, FirefoxManifestPath);
            }
            else if (OperatingSystem.IsLinux())
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                foreach (var dir in ChromiumLinuxDirs.Select(d => Path.Combine(home, d)).Where(Directory.Exists))
                    CopyInto(Path.Combine(dir, "NativeMessagingHosts"), ChromeManifestPath);
                if (Directory.Exists(Path.Combine(home, ".mozilla")))
                    CopyInto(Path.Combine(home, ".mozilla", "native-messaging-hosts"), FirefoxManifestPath);
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return "Couldn't register Lockbox with your browsers: " + ex.Message;
        }
    }

    /// <summary>Removes the registration from every browser. Never throws.</summary>
    public static void Unregister()
    {
        try
        {
            if (CustomDir == null && OperatingSystem.IsWindows())
            {
                foreach (var key in ChromiumRegistryKeys.Append(FirefoxRegistryKey))
                    Registry.CurrentUser.DeleteSubKeyTree($@"{key}\NativeMessagingHosts\{BrowserBridge.HostName}", throwOnMissingSubKey: false);
            }
            else if (CustomDir == null && OperatingSystem.IsLinux())
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                foreach (var dir in ChromiumLinuxDirs)
                    File.Delete(Path.Combine(home, dir, "NativeMessagingHosts", BrowserBridge.HostName + ".json"));
                File.Delete(Path.Combine(home, ".mozilla", "native-messaging-hosts", BrowserBridge.HostName + ".json"));
            }
            File.Delete(ChromeManifestPath);
            File.Delete(FirefoxManifestPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
    }

    private static string Manifest(string allowKey, JsonArray allowed) => new JsonObject
    {
        ["name"] = BrowserBridge.HostName,
        ["description"] = "Lockbox password manager",
        ["path"] = HelperPath,
        ["type"] = "stdio",
        [allowKey] = allowed,
    }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void SetRegistry(string browserKey, string manifestPath)
    {
        using var k = Registry.CurrentUser.CreateSubKey($@"{browserKey}\NativeMessagingHosts\{BrowserBridge.HostName}");
        k.SetValue("", manifestPath);
    }

    private static void CopyInto(string dir, string manifestPath)
    {
        Directory.CreateDirectory(dir);
        File.Copy(manifestPath, Path.Combine(dir, BrowserBridge.HostName + ".json"), overwrite: true);
    }
}
