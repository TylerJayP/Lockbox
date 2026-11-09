using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using NSec.Cryptography;

namespace Lockbox.Core.Crypto;

/// <summary>Argon2id password stretching (via libsodium) plus calibration.</summary>
public static class Kdf
{
    public const int SaltSize = 16;
    public const int OutputSize = 32;

    /// <summary>
    /// Passwords are normalized to Unicode NFC before hashing so the same password typed on
    /// Windows, macOS or Linux always produces the same bytes.
    /// </summary>
    public static byte[] PasswordBytes(string password) =>
        Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormC));

    public static byte[] DeriveArgon2id(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> salt, KdfParameters p)
    {
        if (salt.Length != SaltSize) throw new ArgumentException($"Salt must be {SaltSize} bytes.", nameof(salt));
        if (!p.IsWithinLimits) throw new ArgumentOutOfRangeException(nameof(p), "Argon2id parameters are out of range.");

        var algorithm = PasswordBasedKeyDerivationAlgorithm.Argon2id(new Argon2Parameters
        {
            DegreeOfParallelism = p.Parallelism,
            MemorySize = p.MemoryKiB,
            NumberOfPasses = p.Passes,
        });
        return algorithm.DeriveBytes(secret, salt, OutputSize);
    }

    /// <summary>
    /// Picks the number of passes so one derivation takes roughly <paramref name="target"/> on this machine.
    /// Memory stays fixed (default 256 MiB) because memory is what makes GPU cracking expensive.
    /// </summary>
    public static KdfParameters Calibrate(TimeSpan target, long memoryKiB = 256 * 1024, long minPasses = 3)
    {
        const long probePasses = 2;
        var probe = new KdfParameters(memoryKiB, probePasses);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var secret = RandomNumberGenerator.GetBytes(32);

        var sw = Stopwatch.StartNew();
        var output = DeriveArgon2id(secret, salt, probe);
        sw.Stop();
        CryptographicOperations.ZeroMemory(output);

        double perPassMs = Math.Max(1.0, sw.Elapsed.TotalMilliseconds / probePasses);
        long passes = (long)Math.Ceiling(target.TotalMilliseconds / perPassMs);
        passes = Math.Clamp(passes, minPasses, 20);
        return new KdfParameters(memoryKiB, passes);
    }
}
