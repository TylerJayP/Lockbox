using System.Text.Json;

namespace Lockbox.App.Services;

/// <summary>
/// Non-secret, per-computer preferences (which vault was opened last). Stored in
/// %AppData%\Lockbox\settings.json. Never contains passwords or keys.
/// </summary>
public sealed class AppSettings
{
    public string? LastVaultPath { get; set; }

    /// <summary>Only saved when the user ticks "Remember key file location on this computer".</summary>
    public string? LastKeyFilePath { get; set; }
    public bool RememberKeyFileLocation { get; set; }

    /// <summary>Black out the window in screenshots, screen recordings and screen sharing (Windows 10 2004+).</summary>
    public bool HideFromScreenCapture { get; set; } = true;

    /// <summary>Ask GitHub for a newer version when Lockbox starts. The only time Lockbox goes online.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Auto-type (Windows): Ctrl+Alt+A types the matching login into the active window. Off until turned on.</summary>
    public bool AutoTypeEnabled { get; set; }

    public const int MaxRecentVaults = 5;

    /// <summary>Vaults opened on this computer, newest first. A key file location is only kept when you asked for it.</summary>
    public List<RecentVault> RecentVaults { get; set; } = [];

    /// <summary>Recent vaults, starting the list from the last-opened vault for settings saved by older versions.</summary>
    public IReadOnlyList<RecentVault> GetRecentVaults()
    {
        if (RecentVaults.Count == 0 && !string.IsNullOrEmpty(LastVaultPath))
            RecentVaults.Add(new RecentVault(LastVaultPath, RememberKeyFileLocation ? LastKeyFilePath : null));
        return RecentVaults;
    }

    /// <summary>Records a vault that was just opened or created, and saves.</summary>
    public void RememberVault(string vaultPath, string? keyFilePath, bool rememberKeyFile)
    {
        RememberKeyFileLocation = rememberKeyFile && keyFilePath != null;
        LastVaultPath = vaultPath;
        LastKeyFilePath = RememberKeyFileLocation ? keyFilePath : null;
        RecentVaults.RemoveAll(r => SamePath(r.Path, vaultPath));
        RecentVaults.Insert(0, new RecentVault(vaultPath, LastKeyFilePath));
        if (RecentVaults.Count > MaxRecentVaults) RecentVaults.RemoveRange(MaxRecentVaults, RecentVaults.Count - MaxRecentVaults);
        Save();
    }

    /// <summary>After a key file change: point a remembered key file location at the new file.</summary>
    public void UpdateRememberedKeyFile(string vaultPath, string newKeyFilePath)
    {
        int i = RecentVaults.FindIndex(r => SamePath(r.Path, vaultPath));
        if (i >= 0 && RecentVaults[i].KeyFilePath != null) RecentVaults[i] = RecentVaults[i] with { KeyFilePath = newKeyFilePath };
        if (LastVaultPath != null && SamePath(LastVaultPath, vaultPath) && RememberKeyFileLocation) LastKeyFilePath = newKeyFilePath;
        Save();
    }

    public void ForgetVault(string vaultPath)
    {
        RecentVaults.RemoveAll(r => SamePath(r.Path, vaultPath));
        if (LastVaultPath != null && SamePath(LastVaultPath, vaultPath))
        {
            LastVaultPath = RecentVaults.FirstOrDefault()?.Path;
            LastKeyFilePath = RecentVaults.FirstOrDefault()?.KeyFilePath;
            RememberKeyFileLocation = LastKeyFilePath != null;
        }
        Save();
    }

    private static bool SamePath(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }

    /// <summary>
    /// Newest save of each vault this computer has seen (by vault id). Used to warn when an older copy
    /// replaces a newer one, e.g. a sync tool restoring an old file. Holds no secrets.
    /// </summary>
    public Dictionary<Guid, KnownRevision> KnownVaults { get; set; } = [];

    public KnownRevision? Known(Guid vaultId) => KnownVaults.TryGetValue(vaultId, out var k) ? k : null;

    public void RecordRevision(Guid vaultId, long revision, DateTimeOffset savedUtc)
    {
        if (vaultId == Guid.Empty) return;
        if (KnownVaults.TryGetValue(vaultId, out var k) && k.Revision >= revision) return;
        KnownVaults[vaultId] = new KnownRevision(revision, savedUtc);
        Save();
    }

    private static string FilePath => Path.Combine(
        Environment.GetEnvironmentVariable("LOCKBOX_SETTINGS_DIR") is { Length: > 0 } dir ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lockbox"),
        "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize(File.ReadAllText(FilePath), AppSettingsJson.Default.AppSettings) ?? new AppSettings();
        }
        catch { /* corrupt settings are not worth crashing over */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            if (!RememberKeyFileLocation) LastKeyFilePath = null;
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, AppSettingsJson.Default.AppSettings));
        }
        catch { }
    }
}

public sealed record KnownRevision(long Revision, DateTimeOffset SavedUtc);

public sealed record RecentVault(string Path, string? KeyFilePath);

[System.Text.Json.Serialization.JsonSerializable(typeof(AppSettings))]
[System.Text.Json.Serialization.JsonSourceGenerationOptions(WriteIndented = true)]
internal sealed partial class AppSettingsJson : System.Text.Json.Serialization.JsonSerializerContext;
