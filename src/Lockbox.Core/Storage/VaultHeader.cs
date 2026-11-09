using System.Buffers.Binary;
using Lockbox.Core.Crypto;

namespace Lockbox.Core.Storage;

/// <summary>
/// Fixed 61-byte plaintext header. All of it is fed to the cipher as associated data,
/// so changing any byte (KDF settings, salt, nonce) makes the vault refuse to open.
///
///  0  magic "LBXVAULT"        8
///  8  format version (u16)     2
/// 10  KDF id (1 = Argon2id)    1
/// 11  memory KiB (u32)         4
/// 15  passes (u32)             4
/// 19  parallelism (u8)         1
/// 20  salt                    16
/// 36  cipher id (1 = XChaCha20-Poly1305) 1
/// 37  nonce                   24
/// 61  ciphertext + 16-byte tag ...
/// </summary>
internal sealed record VaultHeader(KdfParameters Kdf, byte[] Salt, byte[] Nonce)
{
    public static ReadOnlySpan<byte> Magic => "LBXVAULT"u8;
    public const ushort FormatVersion = 1;
    public const byte KdfArgon2id = 1;
    public const byte CipherXChaCha20Poly1305 = 1;
    public const int Size = 61;

    public byte[] ToBytes()
    {
        var b = new byte[Size];
        var s = b.AsSpan();
        Magic.CopyTo(s);
        BinaryPrimitives.WriteUInt16LittleEndian(s[8..], FormatVersion);
        s[10] = KdfArgon2id;
        BinaryPrimitives.WriteUInt32LittleEndian(s[11..], checked((uint)Kdf.MemoryKiB));
        BinaryPrimitives.WriteUInt32LittleEndian(s[15..], checked((uint)Kdf.Passes));
        s[19] = checked((byte)Kdf.Parallelism);
        Salt.CopyTo(s[20..]);
        s[36] = CipherXChaCha20Poly1305;
        Nonce.CopyTo(s[37..]);
        return b;
    }

    public static VaultHeader Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < Size + VaultCrypto.TagSize || !data[..8].SequenceEqual(Magic))
            throw new VaultFormatException("This file isn't a Lockbox vault.");

        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]);
        if (version != FormatVersion)
            throw new VaultFormatException($"This vault uses format version {version}, which this version of Lockbox can't read. Update Lockbox.");
        if (data[10] != KdfArgon2id || data[36] != CipherXChaCha20Poly1305)
            throw new VaultFormatException("This vault uses an unknown key-derivation or cipher setting.");

        var kdf = new KdfParameters(
            BinaryPrimitives.ReadUInt32LittleEndian(data[11..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[15..]),
            data[19]);
        if (!kdf.IsWithinLimits)
            throw new VaultFormatException("The vault header has out-of-range security settings (the file may be damaged).");

        return new VaultHeader(kdf, data.Slice(20, Lockbox.Core.Crypto.Kdf.SaltSize).ToArray(), data.Slice(37, VaultCrypto.NonceSize).ToArray());
    }
}
