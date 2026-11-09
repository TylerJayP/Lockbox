namespace Lockbox.Core.Crypto;

/// <summary>
/// Argon2id cost settings. Stored in the vault header so the vault can be opened
/// on any machine, and tuned at creation time so unlocking takes about a second.
/// </summary>
public sealed record KdfParameters(long MemoryKiB, long Passes, int Parallelism = 1)
{
    /// <summary>Lowest memory cost accepted when reading a file (8 MiB). Real vaults use far more.</summary>
    public const long MinMemoryKiB = 8 * 1024;

    /// <summary>Highest memory cost accepted when reading a file (4 GiB), so a corrupted or hostile header can't exhaust RAM.</summary>
    public const long MaxMemoryKiB = 4L * 1024 * 1024;

    public const long MinPasses = 1;
    public const long MaxPasses = 64;

    /// <summary>256 MiB and 3 passes: a sensible floor for new vaults before calibration.</summary>
    public static KdfParameters Default { get; } = new(256 * 1024, 3);

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsWithinLimits =>
        MemoryKiB is >= MinMemoryKiB and <= MaxMemoryKiB &&
        Passes is >= MinPasses and <= MaxPasses &&
        Parallelism == 1; // libsodium's Argon2id only supports a single lane

    public override string ToString() => $"Argon2id, {MemoryKiB / 1024} MiB, {Passes} passes";
}
