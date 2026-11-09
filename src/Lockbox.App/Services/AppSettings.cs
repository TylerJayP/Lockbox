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
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lockbox", "settings.json");

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

[System.Text.Json.Serialization.JsonSerializable(typeof(AppSettings))]
[System.Text.Json.Serialization.JsonSourceGenerationOptions(WriteIndented = true)]
internal sealed partial class AppSettingsJson : System.Text.Json.Serialization.JsonSerializerContext;
