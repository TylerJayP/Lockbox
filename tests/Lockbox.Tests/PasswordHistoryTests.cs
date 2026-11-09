using Lockbox.Core;
using Lockbox.Core.Crypto;
using Lockbox.Core.Export;
using Lockbox.Core.Model;

namespace Lockbox.Tests;

public sealed class PasswordHistoryTests : IDisposable
{
    private const string Pw = "orbit-velvet-cactus-mango-ripple-anchor";
    private readonly string _dir = Directory.CreateTempSubdirectory("lockbox-history-").FullName;
    private readonly byte[] _key = KeyFile.Generate();
    private string VaultPath => Path.Combine(_dir, "h.lbx");

    public void Dispose() => Directory.Delete(_dir, true);

    private static Entry ChangePassword(Vault v, Guid id, string newPassword)
    {
        var e = v.Entries.First(x => x.Id == id).Clone();
        e.Password = newPassword;
        return v.UpdateEntry(e);
    }

    [Fact]
    public void Changing_a_password_records_the_old_one_newest_first()
    {
        using var v = Vault.Create(VaultPath, Pw, _key, VaultTests.Fast);
        var id = v.AddEntry(new Entry { Title = "Netflix", Password = "first" }).Id;
        Assert.Empty(v.Entries[0].PasswordHistory);

        ChangePassword(v, id, "second");
        var after = ChangePassword(v, id, "third");

        Assert.Equal("third", after.Password);
        Assert.Equal(["second", "first"], after.PasswordHistory.Select(h => h.Password));
        Assert.True(after.PasswordHistory[0].ReplacedUtc >= after.PasswordHistory[1].ReplacedUtc);
    }

    [Fact]
    public void Other_edits_and_empty_passwords_add_nothing()
    {
        using var v = Vault.Create(VaultPath, Pw, _key, VaultTests.Fast);
        var id = v.AddEntry(new Entry { Title = "New login" }).Id;

        ChangePassword(v, id, "set-for-the-first-time");     // old one was empty
        var e = v.Entries[0].Clone();
        e.Notes = "just a note";
        var after = v.UpdateEntry(e);

        Assert.Empty(after.PasswordHistory);
    }

    [Fact]
    public void Restoring_an_old_password_does_not_duplicate_it()
    {
        using var v = Vault.Create(VaultPath, Pw, _key, VaultTests.Fast);
        var id = v.AddEntry(new Entry { Title = "Steam", Password = "A" }).Id;
        ChangePassword(v, id, "B");
        var after = ChangePassword(v, id, "A"); // put A back

        Assert.Equal("A", after.Password);
        Assert.Equal(["B"], after.PasswordHistory.Select(h => h.Password));
    }

    [Fact]
    public void History_is_capped_at_the_limit()
    {
        using var v = Vault.Create(VaultPath, Pw, _key, VaultTests.Fast);
        var id = v.AddEntry(new Entry { Title = "Rotating", Password = "p0" }).Id;
        for (int i = 1; i <= 15; i++) ChangePassword(v, id, $"p{i}");

        var h = v.Entries[0].PasswordHistory;
        Assert.Equal(10, h.Count);
        Assert.Equal("p14", h[0].Password);
        Assert.Equal("p5", h[^1].Password);
    }

    [Fact]
    public void Lowering_the_limit_trims_and_zero_turns_history_off()
    {
        using var v = Vault.Create(VaultPath, Pw, _key, VaultTests.Fast);
        var id = v.AddEntry(new Entry { Title = "X", Password = "p0" }).Id;
        for (int i = 1; i <= 5; i++) ChangePassword(v, id, $"p{i}");

        v.UpdateSettings(s => s.PasswordHistoryLimit = 2);
        Assert.Equal(["p4", "p3"], v.Entries[0].PasswordHistory.Select(h => h.Password));

        v.UpdateSettings(s => s.PasswordHistoryLimit = 0);
        Assert.Empty(v.Entries[0].PasswordHistory);
        ChangePassword(v, id, "p6");
        Assert.Empty(v.Entries[0].PasswordHistory);
    }

    [Fact]
    public void A_stale_editor_copy_cannot_rewrite_history()
    {
        using var v = Vault.Create(VaultPath, Pw, _key, VaultTests.Fast);
        var id = v.AddEntry(new Entry { Title = "X", Password = "one" }).Id;
        ChangePassword(v, id, "two");

        var tampered = v.Entries[0].Clone();
        tampered.PasswordHistory = [new PasswordHistoryItem("injected", DateTimeOffset.UtcNow)];
        tampered.Notes = "edit";
        var after = v.UpdateEntry(tampered);

        Assert.Equal(["one"], after.PasswordHistory.Select(h => h.Password));
    }

    [Fact]
    public void History_can_be_cleared_and_survives_save_and_reopen()
    {
        Guid keep, clear;
        using (var v = Vault.Create(VaultPath, Pw, _key, VaultTests.Fast))
        {
            keep = v.AddEntry(new Entry { Title = "Keep", Password = "k1" }).Id;
            clear = v.AddEntry(new Entry { Title = "Clear", Password = "c1" }).Id;
            ChangePassword(v, keep, "k2");
            ChangePassword(v, clear, "c2");
            v.ClearPasswordHistory(clear);
            v.Save();
        }
        using var r = Vault.Open(VaultPath, Pw, _key);
        Assert.Equal(["k1"], r.Entries.First(e => e.Id == keep).PasswordHistory.Select(h => h.Password));
        Assert.Empty(r.Entries.First(e => e.Id == clear).PasswordHistory);
    }

    [Fact]
    public void Old_passwords_are_encrypted_like_everything_else()
    {
        using (var v = Vault.Create(VaultPath, Pw, _key, VaultTests.Fast))
        {
            var id = v.AddEntry(new Entry { Title = "X", Password = "OLD-PASSWORD-CANARY" }).Id;
            ChangePassword(v, id, "new");
            v.Save();
        }
        var raw = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(VaultPath));
        Assert.DoesNotContain("OLD-PASSWORD-CANARY", raw);
    }

    [Fact]
    public void Vaults_from_before_history_existed_still_open()
    {
        // Simulate a v0.2 payload: entries without a PasswordHistory property.
        var json = """{"Version":1,"CreatedUtc":"2026-11-09T00:00:00+00:00","Entries":[{"Id":"7d7f6a8e-3f7c-4a3b-9f2e-1a2b3c4d5e6f","Title":"Old","Url":"","Username":"u","Password":"p","TwoFactorEnabled":false,"TwoFactorMethod":"None","HasBackupCodes":false,"Notes":"","Tags":[],"CreatedUtc":"2026-11-09T00:00:00+00:00","ModifiedUtc":"2026-11-09T00:00:00+00:00","PasswordChangedUtc":"2026-11-09T00:00:00+00:00"}],"Recovery":null,"Settings":{"AutoLockMinutes":5,"ClipboardClearSeconds":20,"LockOnMinimize":true}}""";
        var payload = System.Text.Json.JsonSerializer.Deserialize(json, PayloadJson.Default.VaultPayload)!;
        Assert.Empty(payload.Entries[0].PasswordHistory);
        Assert.Equal(10, payload.Settings.PasswordHistoryLimit);
    }

    [Fact]
    public void Csv_export_contains_only_current_passwords()
    {
        using var v = Vault.Create(VaultPath, Pw, _key, VaultTests.Fast);
        var id = v.AddEntry(new Entry { Title = "X", Password = "retired-one" }).Id;
        ChangePassword(v, id, "current-one");
        var csv = CsvExport.ToBitwardenCsv(v.Entries);
        Assert.Contains("current-one", csv);
        Assert.DoesNotContain("retired-one", csv);
    }
}
