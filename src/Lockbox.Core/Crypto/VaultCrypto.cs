using System.Security.Cryptography;
using NSec.Cryptography;

namespace Lockbox.Core.Crypto;

/// <summary>
/// Key derivation and authenticated encryption.
///
///   stretched = Argon2id(NFC(password), salt, params)                 — slow, memory-hard
///   vaultKey  = HKDF-SHA256(stretched || keyFileSecret, salt, info)   — both factors required
///   file      = header || XChaCha20-Poly1305(vaultKey, nonce, aad: header, payload)
///
/// The key lives in libsodium-managed memory (NSec <see cref="Key"/>) and can't be exported.
/// Every intermediate byte array is zeroed as soon as it's no longer needed.
/// </summary>
internal static class VaultCrypto
{
    public static readonly AeadAlgorithm Aead = AeadAlgorithm.XChaCha20Poly1305;
    public const int NonceSize = 24;
    public const int TagSize = 16;

    private static readonly byte[] VaultInfo = "Lockbox v1 vault key"u8.ToArray();
    private static readonly byte[] RecoveryInfo = "Lockbox v1 recovery key"u8.ToArray();

    public static Key DeriveVaultKey(string password, ReadOnlySpan<byte> keyFileSecret, ReadOnlySpan<byte> salt, KdfParameters kdf)
    {
        if (keyFileSecret.Length != KeyFile.SecretSize)
            throw new ArgumentException("Key file secret must be 32 bytes.", nameof(keyFileSecret));

        byte[] pw = Kdf.PasswordBytes(password);
        byte[]? stretched = null;
        byte[] ikm = new byte[Kdf.OutputSize + KeyFile.SecretSize];
        byte[] okm = new byte[32];
        try
        {
            stretched = Kdf.DeriveArgon2id(pw, salt, kdf);
            stretched.CopyTo(ikm, 0);
            keyFileSecret.CopyTo(ikm.AsSpan(Kdf.OutputSize));
            HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, okm, salt, VaultInfo);
            return Key.Import(Aead, okm, KeyBlobFormat.RawSymmetricKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pw);
            if (stretched != null) CryptographicOperations.ZeroMemory(stretched);
            CryptographicOperations.ZeroMemory(ikm);
            CryptographicOperations.ZeroMemory(okm);
        }
    }

    /// <summary>Key for the sealed backup-codes section: its own passphrase, salt and Argon2id run.</summary>
    public static Key DeriveRecoveryKey(string passphrase, ReadOnlySpan<byte> salt, KdfParameters kdf)
    {
        byte[] pw = Kdf.PasswordBytes(passphrase);
        byte[]? stretched = null;
        byte[] okm = new byte[32];
        try
        {
            stretched = Kdf.DeriveArgon2id(pw, salt, kdf);
            HKDF.DeriveKey(HashAlgorithmName.SHA256, stretched, okm, salt, RecoveryInfo);
            return Key.Import(Aead, okm, KeyBlobFormat.RawSymmetricKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pw);
            if (stretched != null) CryptographicOperations.ZeroMemory(stretched);
            CryptographicOperations.ZeroMemory(okm);
        }
    }

    public static byte[] NewNonce() => RandomNumberGenerator.GetBytes(NonceSize);

    public static byte[] Seal(Key key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> associatedData, ReadOnlySpan<byte> plaintext) =>
        Aead.Encrypt(key, nonce, associatedData, plaintext);

    /// <summary>Returns null if the key is wrong or anything (header, nonce, ciphertext) was altered.</summary>
    public static byte[]? Open(Key key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> associatedData, ReadOnlySpan<byte> ciphertext)
    {
        if (ciphertext.Length < TagSize) return null;
        var plaintext = new byte[ciphertext.Length - TagSize];
        if (Aead.Decrypt(key, nonce, associatedData, ciphertext, plaintext)) return plaintext;
        CryptographicOperations.ZeroMemory(plaintext);
        return null;
    }
}
