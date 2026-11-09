using System.Security.Cryptography;
using System.Text;

namespace Lockbox.Core.Crypto;

/// <summary>
/// The "something you have" half of the vault key: 32 random bytes, saved as a small
/// text file that can also be printed and typed back in from paper.
/// </summary>
public static class KeyFile
{
    public const int SecretSize = 32;
    public const string HeaderLine = "LOCKBOX KEY FILE v1";
    public const string DefaultExtension = ".lbxkey";

    public static byte[] Generate() => RandomNumberGenerator.GetBytes(SecretSize);

    /// <summary>First 4 bytes of SHA-256 over the secret, so a mistyped key is caught before trying it.</summary>
    public static string Checksum(ReadOnlySpan<byte> secret)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(secret, hash);
        return Convert.ToHexString(hash[..4]);
    }

    /// <summary>The key as 16 groups of 4 hex characters, 4 groups per line (for the emergency sheet).</summary>
    public static string FormatGroups(ReadOnlySpan<byte> secret, string lineIndent = "")
    {
        var hex = Convert.ToHexString(secret);
        var sb = new StringBuilder();
        for (int i = 0; i < hex.Length; i += 4)
        {
            int group = i / 4;
            if (group % 4 == 0) { if (group > 0) sb.Append('\n'); sb.Append(group == 0 ? "" : lineIndent); }
            else sb.Append(' ');
            sb.Append(hex, i, 4);
        }
        return sb.ToString();
    }

    public static string ToText(ReadOnlySpan<byte> secret)
    {
        if (secret.Length != SecretSize) throw new ArgumentException("Key file secret must be 32 bytes.", nameof(secret));
        return
            $"{HeaderLine}\n" +
            "This file is half of the key to a Lockbox vault. Keep it somewhere other than the vault file.\n" +
            "Anyone who has this file AND the master password can open the vault.\n" +
            "\n" +
            $"KEY:   {FormatGroups(secret, "       ")}\n" +
            $"CHECK: {Checksum(secret)}\n";
    }

    public static byte[] Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.TrimStart('﻿').TrimStart().StartsWith(HeaderLine, StringComparison.Ordinal))
            throw new KeyFileException("This isn't a Lockbox key file.");

        int keyAt = text.IndexOf("KEY:", StringComparison.Ordinal);
        int checkAt = text.IndexOf("CHECK:", StringComparison.Ordinal);
        if (keyAt < 0 || checkAt < 0 || checkAt < keyAt)
            throw new KeyFileException("The key file is damaged (missing KEY or CHECK).");

        string keyPart = text[(keyAt + 4)..checkAt];
        string checkPart = text[(checkAt + 6)..];
        int newline = checkPart.IndexOf('\n');
        if (newline >= 0) checkPart = checkPart[..newline];
        return ParseTyped(keyPart, checkPart);
    }

    /// <summary>Parse a key typed in from the printed emergency sheet. Spaces, dashes and case don't matter.</summary>
    public static byte[] ParseTyped(string keyHex, string check)
    {
        string hex = new string(keyHex.Where(Uri.IsHexDigit).ToArray());
        string chk = new string(check.Where(Uri.IsHexDigit).ToArray());
        if (hex.Length != SecretSize * 2)
            throw new KeyFileException($"The key should be {SecretSize * 2} hex characters; found {hex.Length}.");

        var secret = Convert.FromHexString(hex);
        if (!string.Equals(Checksum(secret), chk, StringComparison.OrdinalIgnoreCase))
        {
            CryptographicOperations.ZeroMemory(secret);
            throw new KeyFileException("The key doesn't match its CHECK value. Look for a typo or a damaged file.");
        }
        return secret;
    }

    public static byte[] Load(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new KeyFileException("Key file not found.");
        if (info.Length > 16 * 1024) throw new KeyFileException("This isn't a Lockbox key file (too large).");
        return Parse(File.ReadAllText(path, Encoding.UTF8));
    }

    public static void Save(string path, ReadOnlySpan<byte> secret)
    {
        if (File.Exists(path)) throw new IOException("A file already exists there. Pick a new name so an existing key isn't overwritten.");
        File.WriteAllText(path, ToText(secret), new UTF8Encoding(false));
    }
}

public sealed class KeyFileException(string message) : Exception(message);
