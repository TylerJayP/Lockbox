using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lockbox.Core.Crypto;
using NSec.Cryptography;

namespace Lockbox.Core.Updates;

/// <summary>
/// What a release claims to be: the exact package file and its SHA-256. Signed with the developer's
/// offline Ed25519 key; the app installs an update only if the downloaded package matches a manifest
/// with a valid signature. A hijacked GitHub account can upload files but can't produce that signature.
/// </summary>
public sealed record UpdateManifest(
    [property: JsonPropertyName("product")] string Product,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("package")] string PackageFile,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("released")] DateTimeOffset ReleasedUtc)
{
    public const string ProductName = "Lockbox";
    public const string ManifestFileName = "lockbox-update.json";
    public const string SignatureFileName = "lockbox-update.sig";
}

public sealed class UpdateVerificationException(string message) : Exception(message);

[JsonSerializable(typeof(UpdateManifest))]
[JsonSerializable(typeof(SigningKeyFile))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal sealed partial class UpdateJson : JsonSerializerContext;

/// <summary>Passphrase-encrypted Ed25519 private key, as stored on the developer's machine (never in the repo).</summary>
internal sealed class SigningKeyFile
{
    public string Format { get; set; } = UpdateSigning.KeyFileFormat;
    public string PublicKey { get; set; } = "";
    public KdfParameters Kdf { get; set; } = KdfParameters.Default;
    public byte[] Salt { get; set; } = [];
    public byte[] Nonce { get; set; } = [];
    public byte[] Ciphertext { get; set; } = [];
}

public static class UpdateSigning
{
    public const string KeyFileFormat = "lockbox-signing-key-v1";
    public const string KeyFileExtension = ".lbxsign";

    private static readonly SignatureAlgorithm Ed25519 = SignatureAlgorithm.Ed25519;
    private static readonly byte[] Context = "Lockbox update manifest v1\n"u8.ToArray();
    private static readonly byte[] KeyPurpose = "Lockbox v1 release signing key"u8.ToArray();

    // ───────────── manifests ─────────────

    public static byte[] SerializeManifest(UpdateManifest m) => JsonSerializer.SerializeToUtf8Bytes(m, UpdateJson.Default.UpdateManifest);

    public static UpdateManifest CreateManifest(string version, string packagePath) => new(
        UpdateManifest.ProductName, version, Path.GetFileName(packagePath), Sha256File(packagePath), new FileInfo(packagePath).Length, DateTimeOffset.UtcNow);

    public static string Sha256File(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    /// <summary>Returns a base64 Ed25519 signature over the context string + manifest bytes.</summary>
    public static string Sign(ReadOnlySpan<byte> manifestJson, ReadOnlySpan<byte> privateKey)
    {
        using var key = Key.Import(Ed25519, privateKey, KeyBlobFormat.RawPrivateKey);
        return Convert.ToBase64String(Ed25519.Sign(key, [.. Context, .. manifestJson]));
    }

    /// <summary>
    /// Checks the signature with the public key baked into the app, then parses the manifest.
    /// Throws <see cref="UpdateVerificationException"/> on any problem; never returns an unverified manifest.
    /// </summary>
    public static UpdateManifest VerifyManifest(ReadOnlySpan<byte> manifestJson, string signatureBase64, string publicKeyBase64)
    {
        if (string.IsNullOrWhiteSpace(publicKeyBase64)) throw new UpdateVerificationException("No update signing key is configured in this build.");

        PublicKey publicKey;
        byte[] sig;
        try
        {
            publicKey = PublicKey.Import(Ed25519, Convert.FromBase64String(publicKeyBase64.Trim()), KeyBlobFormat.RawPublicKey);
            sig = Convert.FromBase64String(signatureBase64.Trim());
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            throw new UpdateVerificationException("The update signature is malformed.");
        }

        if (sig.Length != Ed25519.SignatureSize || !Ed25519.Verify(publicKey, [.. Context, .. manifestJson], sig))
            throw new UpdateVerificationException("The update's signature is not valid. It was not published with the developer's signing key.");

        UpdateManifest? m;
        try { m = JsonSerializer.Deserialize(manifestJson, UpdateJson.Default.UpdateManifest); }
        catch (JsonException) { m = null; }
        if (m == null || m.Product != UpdateManifest.ProductName || m.Sha256.Length != 64)
            throw new UpdateVerificationException("The update manifest is not for Lockbox or is incomplete.");
        return m;
    }

    /// <summary>Confirms a downloaded package is byte-for-byte the one the signed manifest describes.</summary>
    public static void VerifyPackage(string packagePath, UpdateManifest m, string expectedVersion)
    {
        if (!string.Equals(m.Version, expectedVersion, StringComparison.OrdinalIgnoreCase))
            throw new UpdateVerificationException($"The signed manifest is for version {m.Version}, but the update offered is {expectedVersion}.");
        if (!string.Equals(Path.GetFileName(packagePath), m.PackageFile, StringComparison.OrdinalIgnoreCase))
            throw new UpdateVerificationException("The downloaded package isn't the one the signed manifest describes.");
        var info = new FileInfo(packagePath);
        if (!info.Exists) throw new UpdateVerificationException("The downloaded package is missing.");
        if (info.Length != m.Size || !CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(Sha256File(packagePath)), Convert.FromHexString(m.Sha256)))
            throw new UpdateVerificationException("The downloaded package doesn't match its signed checksum. It may have been tampered with.");
    }

    // ───────────── keys ─────────────

    /// <summary>Creates a key pair. Returns the raw 32-byte private key (caller must protect and zero it) and the base64 public key.</summary>
    public static (byte[] PrivateKey, string PublicKeyBase64) GenerateKeyPair()
    {
        using var key = Key.Create(Ed25519, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        return (key.Export(KeyBlobFormat.RawPrivateKey), Convert.ToBase64String(key.PublicKey.Export(KeyBlobFormat.RawPublicKey)));
    }

    public static string PublicKeyOf(ReadOnlySpan<byte> privateKey)
    {
        using var key = Key.Import(Ed25519, privateKey, KeyBlobFormat.RawPrivateKey);
        return Convert.ToBase64String(key.PublicKey.Export(KeyBlobFormat.RawPublicKey));
    }

    /// <summary>Encrypts the private key with a passphrase (Argon2id + XChaCha20-Poly1305), as JSON text.</summary>
    public static string ProtectPrivateKey(ReadOnlySpan<byte> privateKey, string passphrase, KdfParameters kdf)
    {
        var salt = RandomNumberGenerator.GetBytes(Kdf.SaltSize);
        var nonce = VaultCrypto.NewNonce();
        string pub = PublicKeyOf(privateKey);
        using var k = VaultCrypto.DerivePassphraseKey(passphrase, salt, kdf, KeyPurpose);
        var file = new SigningKeyFile
        {
            PublicKey = pub, Kdf = kdf, Salt = salt, Nonce = nonce,
            Ciphertext = VaultCrypto.Seal(k, nonce, KeyAad(pub), privateKey),
        };
        return JsonSerializer.Serialize(file, UpdateJson.Default.SigningKeyFile);
    }

    /// <summary>Decrypts a signing key file. Throws <see cref="WrongCredentialsException"/> on a wrong passphrase.</summary>
    public static byte[] UnprotectPrivateKey(string fileText, string passphrase)
    {
        var file = JsonSerializer.Deserialize(fileText, UpdateJson.Default.SigningKeyFile);
        if (file == null || file.Format != KeyFileFormat) throw new VaultFormatException("This isn't a Lockbox signing key file.");
        if (!file.Kdf.IsWithinLimits) throw new VaultFormatException("The signing key file has out-of-range settings.");
        using var k = VaultCrypto.DerivePassphraseKey(passphrase, file.Salt, file.Kdf, KeyPurpose);
        var priv = VaultCrypto.Open(k, file.Nonce, KeyAad(file.PublicKey), file.Ciphertext) ?? throw new WrongCredentialsException();
        if (PublicKeyOf(priv) != file.PublicKey) { CryptographicOperations.ZeroMemory(priv); throw new VaultFormatException("The signing key file is inconsistent."); }
        return priv;
    }

    private static byte[] KeyAad(string publicKeyBase64) => [.. Encoding.UTF8.GetBytes(KeyFileFormat + "\n" + publicKeyBase64)];
}
