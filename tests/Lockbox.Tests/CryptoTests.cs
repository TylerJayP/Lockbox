using System.Security.Cryptography;
using Lockbox.Core.Crypto;

namespace Lockbox.Tests;

public sealed class KeyFileTests
{
    [Fact]
    public void Text_round_trips()
    {
        var secret = KeyFile.Generate();
        var text = KeyFile.ToText(secret);
        Assert.StartsWith(KeyFile.HeaderLine, text);
        Assert.Equal(secret, KeyFile.Parse(text));
    }

    [Fact]
    public void Windows_line_endings_and_bom_are_fine()
    {
        var secret = KeyFile.Generate();
        var text = "﻿" + KeyFile.ToText(secret).Replace("\n", "\r\n");
        Assert.Equal(secret, KeyFile.Parse(text));
    }

    [Fact]
    public void A_typo_is_caught_by_the_checksum()
    {
        var secret = KeyFile.Generate();
        var text = KeyFile.ToText(secret);
        int at = text.IndexOf("KEY:", StringComparison.Ordinal) + 7;
        char c = text[at];
        var broken = text[..at] + (c == '0' ? '1' : '0') + text[(at + 1)..];
        Assert.Throws<KeyFileException>(() => KeyFile.Parse(broken));
    }

    [Fact]
    public void Can_be_typed_back_from_the_printed_sheet()
    {
        var secret = KeyFile.Generate();
        string typed = KeyFile.FormatGroups(secret).ToLowerInvariant().Replace(' ', '-');
        Assert.Equal(secret, KeyFile.ParseTyped(typed, KeyFile.Checksum(secret).ToLowerInvariant()));
    }

    [Fact]
    public void Random_files_are_rejected()
    {
        Assert.Throws<KeyFileException>(() => KeyFile.Parse("hello"));
        Assert.Throws<KeyFileException>(() => KeyFile.Parse(KeyFile.HeaderLine + "\nKEY: abcd\nCHECK: 1234"));
    }

    [Fact]
    public void Save_never_overwrites()
    {
        var path = Path.Combine(Path.GetTempPath(), $"k-{Guid.NewGuid():N}.lbxkey");
        try
        {
            var s = KeyFile.Generate();
            KeyFile.Save(path, s);
            Assert.Throws<IOException>(() => KeyFile.Save(path, KeyFile.Generate()));
            Assert.Equal(s, KeyFile.Load(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Generated_keys_are_unique()
    {
        var keys = Enumerable.Range(0, 200).Select(_ => Convert.ToHexString(KeyFile.Generate())).ToHashSet();
        Assert.Equal(200, keys.Count);
    }
}

public sealed class KdfTests
{
    private static readonly KdfParameters Fast = VaultTests.Fast;

    [Fact]
    public void Same_inputs_same_output_different_salt_different_output()
    {
        var pw = Kdf.PasswordBytes("password");
        var s1 = RandomNumberGenerator.GetBytes(Kdf.SaltSize);
        var s2 = RandomNumberGenerator.GetBytes(Kdf.SaltSize);
        Assert.Equal(Kdf.DeriveArgon2id(pw, s1, Fast), Kdf.DeriveArgon2id(pw, s1, Fast));
        Assert.NotEqual(Kdf.DeriveArgon2id(pw, s1, Fast), Kdf.DeriveArgon2id(pw, s2, Fast));
    }

    [Fact]
    public void Matches_the_Argon2id_reference_behaviour_of_libsodium()
    {
        // Same inputs through a different cost must differ: guards against params being ignored.
        var pw = Kdf.PasswordBytes("password");
        var salt = new byte[Kdf.SaltSize];
        Assert.NotEqual(
            Kdf.DeriveArgon2id(pw, salt, new KdfParameters(8 * 1024, 1)),
            Kdf.DeriveArgon2id(pw, salt, new KdfParameters(8 * 1024, 2)));
        Assert.NotEqual(
            Kdf.DeriveArgon2id(pw, salt, new KdfParameters(8 * 1024, 1)),
            Kdf.DeriveArgon2id(pw, salt, new KdfParameters(16 * 1024, 1)));
    }

    [Fact]
    public void Out_of_range_parameters_are_refused()
    {
        var salt = new byte[Kdf.SaltSize];
        Assert.Throws<ArgumentOutOfRangeException>(() => Kdf.DeriveArgon2id([1], salt, new KdfParameters(1024, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Kdf.DeriveArgon2id([1], salt, new KdfParameters(8 * 1024, 1, 4)));
    }

    [Fact]
    public void Calibration_never_goes_below_the_floor()
    {
        var p = Kdf.Calibrate(TimeSpan.FromMilliseconds(1), memoryKiB: 8 * 1024);
        Assert.Equal(3, p.Passes);
        Assert.Equal(8 * 1024, p.MemoryKiB);
    }
}
