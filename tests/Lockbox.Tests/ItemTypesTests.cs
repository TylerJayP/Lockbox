using System.Text;
using Lockbox.Core;
using Lockbox.Core.Crypto;
using Lockbox.Core.Export;
using Lockbox.Core.Model;
using Lockbox.Core.Tools;

namespace Lockbox.Tests;

public sealed class ItemTypesTests : IDisposable
{
    private const string Pw = "orbit-velvet-cactus-mango-ripple-anchor";
    private readonly string _dir = Directory.CreateTempSubdirectory("lockbox-types-").FullName;
    private readonly byte[] _key = KeyFile.Generate();
    private string VaultPath => Path.Combine(_dir, "t.lbx");

    public void Dispose() => Directory.Delete(_dir, true);

    private Vault NewVault() => Vault.Create(VaultPath, Pw, _key, VaultTests.Fast);
    private Vault Reopen() => Vault.Open(VaultPath, Pw, _key);

    [Fact]
    public void Cards_wifi_notes_and_custom_fields_round_trip()
    {
        using (var v = NewVault())
        {
            v.AddEntry(new Entry
            {
                Type = ItemType.Card, Title = "Chase Visa", Reprompt = true,
                Card = new CardData { CardholderName = "Tyler Perkins", Number = "4111 1111 1111 1111", Expiry = "08/29", SecurityCode = "123", Pin = "4821" },
            });
            v.AddEntry(new Entry
            {
                Type = ItemType.WiFi, Title = "Home Wi-Fi", Password = "router-pass-123",
                Wifi = new WifiData { NetworkName = "Perkins 5G", Security = WifiSecurity.Wpa3, HiddenNetwork = true },
            });
            v.AddEntry(new Entry
            {
                Type = ItemType.SecureNote, Title = "Locker combo", Notes = "12-34-56",
                Fields = [new CustomField("Locker number", "88", false), new CustomField("Spare PIN", "0000", true)],
            });
            v.Save();
        }

        using var r = Reopen();
        var card = r.Entries.Single(e => e.Type == ItemType.Card);
        Assert.True(card.Reprompt);
        Assert.Equal(new CardData { CardholderName = "Tyler Perkins", Number = "4111 1111 1111 1111", Expiry = "08/29", SecurityCode = "123", Pin = "4821" }, card.Card);

        var wifi = r.Entries.Single(e => e.Type == ItemType.WiFi);
        Assert.Equal("Perkins 5G", wifi.Wifi!.NetworkName);
        Assert.Equal(WifiSecurity.Wpa3, wifi.Wifi.Security);
        Assert.True(wifi.Wifi.HiddenNetwork);

        var note = r.Entries.Single(e => e.Type == ItemType.SecureNote);
        Assert.Equal([new CustomField("Locker number", "88", false), new CustomField("Spare PIN", "0000", true)], note.Fields);
    }

    [Fact]
    public void Content_equality_sees_card_and_field_changes()
    {
        var a = new Entry { Type = ItemType.Card, Card = new CardData { Number = "1" }, Fields = [new CustomField("x", "1", false)] };
        var b = a.Clone();
        Assert.True(a.ContentEquals(b));
        b.Card = b.Card! with { Number = "2" };
        Assert.False(a.ContentEquals(b));
        var c = a.Clone();
        c.Fields = [new CustomField("x", "1", true)];
        Assert.False(a.ContentEquals(c));
    }

    [Fact]
    public void Attachments_are_encrypted_saved_and_removable()
    {
        Guid entryId, attId;
        var data = Encoding.UTF8.GetBytes("ATTACHMENT-CANARY recovery-kit.pdf contents");
        using (var v = NewVault())
        {
            entryId = v.AddEntry(new Entry { Title = "GitHub" }).Id;
            var att = v.AddAttachment(entryId, @"C:\Users\tyler\Downloads\github-recovery-codes.txt", data);
            attId = att.Id;
            Assert.Equal("github-recovery-codes.txt", att.FileName);
            Assert.Equal(data.Length, att.Size);
            v.Save();
        }
        Assert.DoesNotContain("ATTACHMENT-CANARY", Encoding.Latin1.GetString(File.ReadAllBytes(VaultPath)));

        using (var r = Reopen())
        {
            var att = Assert.Single(r.Entries.Single().Attachments);
            Assert.Equal(data, r.GetAttachment(att.Id));
            r.RenameAttachment(entryId, att.Id, "codes.txt");
            Assert.Equal("codes.txt", r.Entries.Single().Attachments[0].FileName);
            r.RemoveAttachment(entryId, att.Id);
            Assert.Empty(r.Entries.Single().Attachments);
            Assert.Throws<KeyNotFoundException>(() => r.GetAttachment(attId));
            Assert.Equal(0, r.TotalAttachmentBytes);
        }
    }

    [Fact]
    public void Attachment_size_limits_are_enforced()
    {
        using var v = NewVault();
        var id = v.AddEntry(new Entry { Title = "Big" }).Id;
        var tooBig = new byte[Vault.MaxAttachmentBytes + 1];
        Assert.Throws<ArgumentException>(() => v.AddAttachment(id, "big.bin", tooBig));
    }

    [Fact]
    public void Deleting_an_item_deletes_its_attachments()
    {
        using var v = NewVault();
        var id = v.AddEntry(new Entry { Title = "X" }).Id;
        var att = v.AddAttachment(id, "a.txt", [1, 2, 3]);
        v.DeleteEntry(id);
        Assert.Equal(0, v.TotalAttachmentBytes);
        Assert.Throws<KeyNotFoundException>(() => v.GetAttachment(att.Id));
    }

    [Fact]
    public void Callers_cannot_inject_attachment_refs_through_UpdateEntry()
    {
        using var v = NewVault();
        var id = v.AddEntry(new Entry { Title = "X" }).Id;
        var edit = v.Entries[0].Clone();
        edit.Attachments = [new AttachmentRef(Guid.NewGuid(), "fake.txt", 1, DateTimeOffset.UtcNow, "")];
        edit.Notes = "changed";
        Assert.Empty(v.UpdateEntry(edit).Attachments);

        v.AddAttachment(id, "real.txt", [1]);
        var edit2 = v.Entries[0].Clone();
        edit2.Attachments = [];
        Assert.Single(v.UpdateEntry(edit2).Attachments);
    }

    [Fact]
    public void Revision_goes_up_on_every_save_and_id_is_stable()
    {
        Guid id;
        using (var v = NewVault())
        {
            id = v.VaultId;
            Assert.NotEqual(Guid.Empty, id);
            Assert.Equal(1, v.Revision);
            v.Save();
            v.Save();
            Assert.Equal(3, v.Revision);
        }
        using var r = Reopen();
        Assert.Equal(id, r.VaultId);
        Assert.Equal(3, r.Revision);
        Assert.True(r.LastSavedUtc > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public void Audit_flags_old_passwords_missing_2fa_and_ignores_non_password_items()
    {
        var login = new Entry { Type = ItemType.Login, Title = "Shop", Password = "Xk9#mQ2$vL7!pR4@zT" };
        var withTfa = new Entry { Type = ItemType.Login, Title = "Bank", Password = "Zq8!nW3#bH6@tY1$kP", TwoFactorEnabled = true };
        var openWifi = new Entry { Type = ItemType.WiFi, Title = "Cafe", Wifi = new WifiData { Security = WifiSecurity.Open } };
        var card = new Entry { Type = ItemType.Card, Title = "Visa", Card = new CardData { Pin = "1234" } };
        var note = new Entry { Type = ItemType.SecureNote, Title = "Note" };
        var wifi = new Entry { Type = ItemType.WiFi, Title = "Home", Password = "Xk9#mQ2$vL7!pR4@zT", Wifi = new WifiData() };

        var later = DateTimeOffset.UtcNow.AddYears(3);
        var r = VaultAudit.Analyze([login, withTfa, openWifi, card, note, wifi], later);

        Assert.Equal(EntryIssue.Old | EntryIssue.NoTwoFactor | EntryIssue.Reused, r[login.Id]);
        Assert.Equal(EntryIssue.Old, r[withTfa.Id]);
        Assert.Equal(EntryIssue.None, r[openWifi.Id]);   // no password expected
        Assert.Equal(EntryIssue.None, r[card.Id]);       // PINs aren't audited
        Assert.Equal(EntryIssue.None, r[note.Id]);
        Assert.Equal(EntryIssue.Old | EntryIssue.Reused, r[wifi.Id]); // reused with the login; 2FA doesn't apply to Wi-Fi
        Assert.Equal(EntryIssue.None, VaultAudit.Analyze([withTfa])[withTfa.Id]); // not old today
    }

    [Theory]
    [InlineData("4111 1111 1111 1111", "Visa", true)]
    [InlineData("5555-5555-5555-4444", "Mastercard", true)]
    [InlineData("2223003122003222", "Mastercard", true)]
    [InlineData("378282246310005", "American Express", true)]
    [InlineData("6011111111111117", "Discover", true)]
    [InlineData("4111 1111 1111 1112", "Visa", false)]
    public void Card_brand_and_checksum(string number, string brand, bool valid)
    {
        Assert.Equal(brand, CardInfo.Brand(number));
        Assert.Equal(valid, CardInfo.LooksValid(number));
    }

    [Fact]
    public void Card_formatting()
    {
        Assert.Equal("4111 1111 1111 1111", CardInfo.Format("4111111111111111"));
        Assert.Equal("3782 822463 10005", CardInfo.Format("378282246310005"));
        Assert.Equal("Visa •••• 1111", CardInfo.Summary("4111-1111-1111-1111"));
    }

    [Fact]
    public void Wifi_qr_payload_escapes_special_characters()
    {
        Assert.Equal("WIFI:T:WPA;S:My\\;Net;P:pa\\:ss\\,\\\\w\\\"d;H:true;;",
            WifiQr.Payload("My;Net", "pa:ss,\\w\"d", WifiSecurity.Wpa2, hidden: true));
        Assert.Equal("WIFI:T:nopass;S:Cafe;;", WifiQr.Payload("Cafe", "ignored", WifiSecurity.Open, false));
        Assert.Equal("WIFI:T:WEP;S:Old;P:abc;;", WifiQr.Payload("Old", "abc", WifiSecurity.Wep, false));
    }

    [Fact]
    public void Csv_export_includes_every_item_type()
    {
        var items = new[]
        {
            new Entry { Type = ItemType.Card, Title = "Visa", Reprompt = true, Card = new CardData { CardholderName = "T P", Number = "4111111111111111", Expiry = "08/29", SecurityCode = "123" } },
            new Entry { Type = ItemType.WiFi, Title = "Home", Password = "wifipass", Wifi = new WifiData { NetworkName = "Perkins 5G", Security = WifiSecurity.Wpa3 } },
            new Entry { Type = ItemType.SecureNote, Title = "Note", Notes = "secret", Fields = [new CustomField("Locker", "88", true)] },
        };
        string csv = CsvExport.ToBitwardenCsv(items);
        Assert.Contains("\"note\",\"Visa\"", csv);
        Assert.Contains("Card number: 4111111111111111", csv);
        Assert.Contains("\"login\",\"Home\"", csv);
        Assert.Contains("\"Perkins 5G\",\"wifipass\"", csv);
        Assert.Contains("Wi-Fi security: WPA3", csv);
        Assert.Contains("\"note\",\"Note\",\"secret\",\"Locker: 88\"", csv);
        Assert.Contains("\"1\"", csv); // reprompt carried over for the card
    }
}
