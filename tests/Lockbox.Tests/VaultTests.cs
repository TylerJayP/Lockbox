using System.Text;
using Lockbox.Core;
using Lockbox.Core.Crypto;
using Lockbox.Core.Model;

namespace Lockbox.Tests;

public sealed class VaultTests : IDisposable
{
    // Tiny KDF cost so tests run fast. Real vaults are calibrated to ~1 s with 256 MiB.
    internal static readonly KdfParameters Fast = new(KdfParameters.MinMemoryKiB, 1);
    private const string Pw = "orbit-velvet-cactus-mango-ripple-anchor";

    private readonly string _dir = Directory.CreateTempSubdirectory("lockbox-test-").FullName;
    private readonly byte[] _keyFile = KeyFile.Generate();
    private string VaultPath => Path.Combine(_dir, "test.lbx");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static Entry Sample(string title = "GitHub", string password = "s3cret-Pa55-unique!") => new()
    {
        Title = title, Url = "https://github.com", Username = "TylerJayP", Password = password,
        TwoFactorEnabled = true, TwoFactorMethod = TwoFactorMethod.AuthenticatorApp, Notes = "note", Tags = ["dev"],
    };

    [Fact]
    public void Create_save_and_reopen_round_trips_entries()
    {
        using (var v = Vault.Create(VaultPath, Pw, _keyFile, Fast))
        {
            v.AddEntry(Sample());
            v.UpdateSettings(s => s.AutoLockMinutes = 9);
            v.Save();
        }

        using var reopened = Vault.Open(VaultPath, Pw, _keyFile);
        var e = Assert.Single(reopened.Entries);
        Assert.Equal("GitHub", e.Title);
        Assert.Equal("TylerJayP", e.Username);
        Assert.Equal("s3cret-Pa55-unique!", e.Password);
        Assert.True(e.TwoFactorEnabled);
        Assert.Equal(TwoFactorMethod.AuthenticatorApp, e.TwoFactorMethod);
        Assert.Equal(["dev"], e.Tags);
        Assert.Equal(9, reopened.Settings.AutoLockMinutes);
        Assert.False(reopened.HasUnsavedChanges);
    }

    [Fact]
    public void Wrong_password_is_rejected()
    {
        using (var v = Vault.Create(VaultPath, Pw, _keyFile, Fast)) { }
        Assert.Throws<WrongCredentialsException>(() => Vault.Open(VaultPath, Pw + "x", _keyFile));
    }

    [Fact]
    public void Wrong_key_file_is_rejected()
    {
        using (var v = Vault.Create(VaultPath, Pw, _keyFile, Fast)) { }
        Assert.Throws<WrongCredentialsException>(() => Vault.Open(VaultPath, Pw, KeyFile.Generate()));
    }

    [Theory]
    [InlineData(100)]  // ciphertext byte
    [InlineData(-1)]   // last byte (auth tag)
    [InlineData(25)]   // salt byte in the header
    [InlineData(40)]   // nonce byte in the header
    public void Any_tampering_is_detected(int offset)
    {
        using (var v = Vault.Create(VaultPath, Pw, _keyFile, Fast)) { v.AddEntry(Sample()); v.Save(); }
        var bytes = File.ReadAllBytes(VaultPath);
        bytes[offset < 0 ? bytes.Length + offset : offset] ^= 0x01;
        File.WriteAllBytes(VaultPath, bytes);
        Assert.Throws<WrongCredentialsException>(() => Vault.Open(VaultPath, Pw, _keyFile));
    }

    [Fact]
    public void Tampered_kdf_settings_are_rejected()
    {
        using (var v = Vault.Create(VaultPath, Pw, _keyFile, Fast)) { }
        var bytes = File.ReadAllBytes(VaultPath);
        bytes[15] = 2; // passes 1 → 2: different key, and the header is authenticated anyway
        File.WriteAllBytes(VaultPath, bytes);
        Assert.Throws<WrongCredentialsException>(() => Vault.Open(VaultPath, Pw, _keyFile));
    }

    [Fact]
    public void Absurd_kdf_settings_in_header_are_refused_before_hashing()
    {
        using (var v = Vault.Create(VaultPath, Pw, _keyFile, Fast)) { }
        var bytes = File.ReadAllBytes(VaultPath);
        bytes[14] = 0xFF; // memory → ~4 TiB
        File.WriteAllBytes(VaultPath, bytes);
        Assert.Throws<VaultFormatException>(() => Vault.Open(VaultPath, Pw, _keyFile));
    }

    [Fact]
    public void Non_vault_file_gives_clear_error()
    {
        File.WriteAllText(VaultPath, "netflix: me@example.com / hunter2");
        Assert.Throws<VaultFormatException>(() => Vault.Open(VaultPath, Pw, _keyFile));
    }

    [Fact]
    public void Secrets_never_appear_in_the_file_in_plaintext()
    {
        using (var v = Vault.Create(VaultPath, Pw, _keyFile, Fast))
        {
            v.AddEntry(Sample(password: "PLAINTEXT-CANARY-12345"));
            v.SetupRecovery("recovery passphrase canary", Fast);
            v.SetBackupCodes(v.Entries[0].Id, "CODE-CANARY-9999");
            v.Save();
        }
        string raw = Encoding.Latin1.GetString(File.ReadAllBytes(VaultPath));
        Assert.DoesNotContain("PLAINTEXT-CANARY", raw);
        Assert.DoesNotContain("CODE-CANARY", raw);
        Assert.DoesNotContain("GitHub", raw);
        Assert.DoesNotContain("TylerJayP", raw);
    }

    [Fact]
    public void File_size_is_padded_so_it_does_not_reveal_small_changes()
    {
        using var v = Vault.Create(VaultPath, Pw, _keyFile, Fast);
        long empty = new FileInfo(VaultPath).Length;
        v.AddEntry(Sample());
        v.Save();
        Assert.Equal(empty, new FileInfo(VaultPath).Length);
    }

    [Fact]
    public void Each_save_uses_a_fresh_nonce()
    {
        using var v = Vault.Create(VaultPath, Pw, _keyFile, Fast);
        var a = File.ReadAllBytes(VaultPath);
        v.Save();
        var b = File.ReadAllBytes(VaultPath);
        Assert.False(a.AsSpan(37, 24).SequenceEqual(b.AsSpan(37, 24)));
    }

    [Fact]
    public void Save_backs_up_previous_file_once_per_unlock_and_leaves_no_temp_files()
    {
        using (var v = Vault.Create(VaultPath, Pw, _keyFile, Fast)) { }
        using (var v = Vault.Open(VaultPath, Pw, _keyFile))
        {
            v.AddEntry(Sample("A")); v.Save();
            v.AddEntry(Sample("B")); v.Save();
            Assert.Single(v.ListBackups());

            // The backup is the pre-session file and still opens.
            using var old = Vault.Open(v.ListBackups()[0].FullName, Pw, _keyFile);
            Assert.Empty(old.Entries);
        }
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void Backups_are_capped()
    {
        using (var v = Vault.Create(VaultPath, Pw, _keyFile, Fast)) { }
        for (int i = 0; i < Vault.BackupsToKeep + 5; i++)
        {
            using var v = Vault.Open(VaultPath, Pw, _keyFile);
            v.AddEntry(Sample($"E{i}"));
            v.Save();
        }
        using var last = Vault.Open(VaultPath, Pw, _keyFile);
        Assert.Equal(Vault.BackupsToKeep, last.ListBackups().Count);
        Assert.Equal(Vault.BackupsToKeep, last.DeleteBackups());
        Assert.Empty(last.ListBackups());
    }

    [Fact]
    public void External_change_is_detected_instead_of_silently_overwritten()
    {
        using (var v = Vault.Create(VaultPath, Pw, _keyFile, Fast)) { }
        using var mine = Vault.Open(VaultPath, Pw, _keyFile);
        using (var other = Vault.Open(VaultPath, Pw, _keyFile)) { other.AddEntry(Sample("From other device")); other.Save(); }

        mine.AddEntry(Sample("Mine"));
        Assert.Throws<VaultConflictException>(() => mine.Save());
        mine.Save(overwriteExternalChanges: true);

        using var check = Vault.Open(VaultPath, Pw, _keyFile);
        Assert.Equal("Mine", Assert.Single(check.Entries).Title);
    }

    [Fact]
    public void Backup_codes_stay_sealed_until_their_own_passphrase_is_given()
    {
        Guid id;
        using (var v = Vault.Create(VaultPath, Pw, _keyFile, Fast))
        {
            id = v.AddEntry(Sample()).Id;
            v.SetupRecovery("a different recovery passphrase", Fast);
            v.SetBackupCodes(id, "1234 5678\n2345 6789");
            v.Save();
        }

        using var r = Vault.Open(VaultPath, Pw, _keyFile);
        Assert.True(r.IsRecoveryConfigured);
        Assert.False(r.IsRecoveryUnlocked);
        Assert.True(r.Entries[0].HasBackupCodes);
        Assert.Throws<RecoveryLockedException>(() => r.GetBackupCodes(id));

        Assert.False(r.UnlockRecovery("wrong"));
        Assert.False(r.IsRecoveryUnlocked);
        Assert.True(r.UnlockRecovery("a different recovery passphrase"));
        Assert.Equal("1234 5678\n2345 6789", r.GetBackupCodes(id));

        r.LockRecovery();
        Assert.Throws<RecoveryLockedException>(() => r.GetBackupCodes(id));
    }

    [Fact]
    public void Clearing_codes_removes_the_badge()
    {
        using var v = Vault.Create(VaultPath, Pw, _keyFile, Fast);
        var id = v.AddEntry(Sample()).Id;
        v.SetupRecovery("recovery passphrase", Fast);
        v.SetBackupCodes(id, "1111 2222");
        Assert.True(v.Entries[0].HasBackupCodes);
        v.SetBackupCodes(id, "   ");
        Assert.False(v.Entries[0].HasBackupCodes);
    }

    [Fact]
    public void Codes_for_entries_deleted_while_sealed_are_pruned_on_next_unlock()
    {
        Guid keep, drop;
        using (var v = Vault.Create(VaultPath, Pw, _keyFile, Fast))
        {
            keep = v.AddEntry(Sample("Keep")).Id;
            drop = v.AddEntry(Sample("Drop")).Id;
            v.SetupRecovery("rp", Fast);
            v.SetBackupCodes(keep, "1111");
            v.SetBackupCodes(drop, "2222");
            v.Save();
        }
        using (var v = Vault.Open(VaultPath, Pw, _keyFile))
        {
            v.DeleteEntry(drop); // sealed: codes can't be removed yet
            v.Save();
        }
        using (var v = Vault.Open(VaultPath, Pw, _keyFile))
        {
            Assert.True(v.UnlockRecovery("rp"));
            Assert.True(v.HasUnsavedChanges);
            Assert.Equal("1111", v.GetBackupCodes(keep));
            Assert.Equal("", v.GetBackupCodes(drop));
        }
    }

    [Fact]
    public void Changing_credentials_rekeys_the_vault()
    {
        var newKey = KeyFile.Generate();
        using (var v = Vault.Create(VaultPath, Pw, _keyFile, Fast))
        {
            v.AddEntry(Sample());
            v.Save();
            v.ChangeCredentials("brand-new-master-passphrase-here", newKey, Fast);
        }
        Assert.Throws<WrongCredentialsException>(() => Vault.Open(VaultPath, Pw, _keyFile));
        using var r = Vault.Open(VaultPath, "brand-new-master-passphrase-here", newKey);
        Assert.Single(r.Entries);
    }

    [Fact]
    public void Credentials_can_be_verified_without_reopening()
    {
        using var v = Vault.Create(VaultPath, Pw, _keyFile, Fast);
        Assert.True(v.VerifyCredentials(Pw, _keyFile));
        Assert.False(v.VerifyCredentials(Pw + "!", _keyFile));
        Assert.False(v.VerifyCredentials(Pw, KeyFile.Generate()));

        var newKey = KeyFile.Generate();
        v.ChangeCredentials("brand-new-master-passphrase-here", newKey, Fast);
        Assert.False(v.VerifyCredentials(Pw, _keyFile));
        Assert.True(v.VerifyCredentials("brand-new-master-passphrase-here", newKey));
    }

    [Fact]
    public void Update_tracks_password_change_time_and_keeps_created_time()
    {
        using var v = Vault.Create(VaultPath, Pw, _keyFile, Fast);
        var e = v.AddEntry(Sample());
        var created = e.CreatedUtc;
        var pwTime = e.PasswordChangedUtc;
        Thread.Sleep(15);

        var edit = e.Clone(); edit.Notes = "changed";
        var after1 = v.UpdateEntry(edit);
        Assert.Equal(pwTime, after1.PasswordChangedUtc);
        Assert.Equal(created, after1.CreatedUtc);

        var edit2 = after1.Clone(); edit2.Password = "new-password";
        var after2 = v.UpdateEntry(edit2);
        Assert.True(after2.PasswordChangedUtc > pwTime);
    }

    [Fact]
    public void Locked_vault_cannot_be_used()
    {
        var v = Vault.Create(VaultPath, Pw, _keyFile, Fast);
        v.Dispose();
        Assert.Throws<ObjectDisposedException>(() => v.Save());
    }

    [Fact]
    public void Create_refuses_to_overwrite_an_existing_file()
    {
        File.WriteAllText(VaultPath, "something important");
        Assert.Throws<IOException>(() => Vault.Create(VaultPath, Pw, _keyFile, Fast));
        Assert.Equal("something important", File.ReadAllText(VaultPath));
    }

    [Fact]
    public void Password_is_unicode_normalized()
    {
        string composed = "café-orbit-velvet-cactus";     // é as one code point
        string decomposed = "café-orbit-velvet-cactus";  // e + combining accent (macOS style)
        using (var v = Vault.Create(VaultPath, composed, _keyFile, Fast)) { }
        using var r = Vault.Open(VaultPath, decomposed, _keyFile);
        Assert.NotNull(r);
    }
}
