using System.Text;
using Lockbox.Core;
using Lockbox.Core.Crypto;
using Lockbox.Core.Export;
using Lockbox.Core.Model;

namespace Lockbox.Tests;

public sealed class ExportTests
{
    /// <summary>Strict RFC 4180 reader, so the test doesn't trust the writer's own assumptions.</summary>
    private static List<string[]> ParseCsv(string text)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var cell = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') { row.Add(cell.ToString()); cell.Clear(); rows.Add([.. row]); row.Clear(); i++; }
            else cell.Append(c);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add([.. row]); }
        return rows;
    }

    [Fact]
    public void Bitwarden_csv_round_trips_awkward_values()
    {
        var tricky = new Entry
        {
            Title = "Bank, \"Main\" account",
            Url = "https://bank.example.com/login?a=1,b=2",
            Username = "tyler@example.com",
            Password = "p\"a,s\ns'w=rd😀",
            Notes = "line one\r\nline two, with comma",
            TwoFactorEnabled = true,
            TwoFactorMethod = TwoFactorMethod.Sms,
            Tags = ["banking", "important"],
        };
        var plain = new Entry { Title = "Amazon", Username = "me", Password = "x" };

        var csv = CsvExport.ToBitwardenCsv([tricky, plain]);
        var rows = ParseCsv(csv);

        Assert.Equal(3, rows.Count);
        Assert.Equal(CsvExport.BitwardenHeader, rows[0]);
        Assert.All(rows, r => Assert.Equal(11, r.Length));

        var a = rows[1]; // sorted by title: Amazon first
        Assert.Equal("Amazon", a[3]);
        Assert.Equal("login", a[2]);

        var b = rows[2];
        Assert.Equal("banking", b[0]);                       // folder = first tag
        Assert.Equal("Bank, \"Main\" account", b[3]);
        Assert.Equal("line one\nline two, with comma", b[4]);
        Assert.Equal("2FA: Text message (SMS)\nTags: banking, important", b[5]);
        Assert.Equal("https://bank.example.com/login?a=1,b=2", b[7]);
        Assert.Equal("tyler@example.com", b[8]);
        Assert.Equal("p\"a,s\ns'w=rd😀", b[9]);
    }

    [Fact]
    public void Backup_codes_are_only_included_when_asked()
    {
        var e = new Entry { Title = "GitHub", Notes = "personal", HasBackupCodes = true };
        string without = CsvExport.ToBitwardenCsv([e]);
        string with = CsvExport.ToBitwardenCsv([e], _ => "1111 2222\n3333 4444");

        Assert.DoesNotContain("1111", without);
        var notes = ParseCsv(with)[1][4];
        Assert.Equal("personal\n\nBackup codes:\n1111 2222\n3333 4444", notes);
    }

    [Fact]
    public void Encrypted_copy_opens_with_same_credentials_and_includes_unsaved_changes()
    {
        var dir = Directory.CreateTempSubdirectory("lockbox-export-").FullName;
        try
        {
            var key = KeyFile.Generate();
            string path = Path.Combine(dir, "v.lbx"), copy = Path.Combine(dir, "usb", "v-copy.lbx");
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);

            using (var v = Vault.Create(path, "orbit-velvet-cactus-mango-ripple-anchor", key, VaultTests.Fast))
            {
                v.AddEntry(new Entry { Title = "Unsaved yet" });
                v.SaveEncryptedCopy(copy);
                Assert.False(v.HasUnsavedChanges);
                Assert.Throws<IOException>(() => v.SaveEncryptedCopy(path));
            }

            using var c = Vault.Open(copy, "orbit-velvet-cactus-mango-ripple-anchor", key);
            Assert.Equal("Unsaved yet", Assert.Single(c.Entries).Title);
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(copy)!, "*.tmp"));
        }
        finally { Directory.Delete(dir, true); }
    }
}
