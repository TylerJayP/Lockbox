using Lockbox.Core.Import;
using Lockbox.Core.Model;

namespace Lockbox.Tests;

public sealed class ImporterTests
{
    [Fact]
    public void Labeled_blocks_with_backup_codes()
    {
        const string text = """
            Netflix
            Email: tyler@example.com
            Password: hunter2!

            Google
            username: tyler.p
            password: G00gle$ecret
            2FA: authenticator app
            Backup codes:
            1234 5678
            2345 6789

            3456 7890
            """;
        var r = TextImporter.Parse(text);
        Assert.Equal(2, r.Count);

        Assert.Equal("Netflix", r[0].Title);
        Assert.Equal("tyler@example.com", r[0].Username);
        Assert.Equal("hunter2!", r[0].Password);
        Assert.False(r[0].TwoFactorEnabled);
        Assert.Empty(r[0].Warnings);

        Assert.Equal("Google", r[1].Title);
        Assert.Equal("tyler.p", r[1].Username);
        Assert.Equal("G00gle$ecret", r[1].Password);
        Assert.True(r[1].TwoFactorEnabled);
        Assert.Equal(TwoFactorMethod.AuthenticatorApp, r[1].TwoFactorMethod);
        Assert.Equal("1234 5678\n2345 6789\n3456 7890", r[1].BackupCodes);
    }

    [Fact]
    public void One_line_records_with_various_separators()
    {
        const string text = """
            Amazon - tyler@example.com - Amaz0n!pass
            Steam | tylerjp | St3amPass
            discord: tyler@example.com / D1sc0rd!!
            Xbox, tylerjp@outlook.com, Xb0x-Pass
            """;
        var r = TextImporter.Parse(text);
        Assert.Equal(4, r.Count);
        Assert.Equal(("Amazon", "tyler@example.com", "Amaz0n!pass"), (r[0].Title, r[0].Username, r[0].Password));
        Assert.Equal(("Steam", "tylerjp", "St3amPass"), (r[1].Title, r[1].Username, r[1].Password));
        Assert.Equal(("discord", "tyler@example.com", "D1sc0rd!!"), (r[2].Title, r[2].Username, r[2].Password));
        Assert.Equal(("Xbox", "tylerjp@outlook.com", "Xb0x-Pass"), (r[3].Title, r[3].Username, r[3].Password));
    }

    [Fact]
    public void Unlabeled_run_on_entries_are_split_and_flagged()
    {
        const string text = """
            Spotify
            tyler@example.com
            Sp0tify#1
            Hulu
            tyler@example.com
            Hulu2024!
            """;
        var r = TextImporter.Parse(text);
        Assert.Equal(2, r.Count);
        Assert.Equal(("Spotify", "tyler@example.com", "Sp0tify#1"), (r[0].Title, r[0].Username, r[0].Password));
        Assert.Equal(("Hulu", "tyler@example.com", "Hulu2024!"), (r[1].Title, r[1].Username, r[1].Password));
        Assert.Contains(r[0].Warnings, w => w.Contains("Guessed the password"));
    }

    [Fact]
    public void Url_first_blocks_and_letter_only_codes()
    {
        const string text = """
            https://github.com
            user: TylerJayP
            pass: gh-pass-123
            recovery codes: abcd-efgh ijkl-mnop
            qrst-uvwx
            Hulu
            user: someone
            pass: x
            """;
        var r = TextImporter.Parse(text);
        Assert.Equal(2, r.Count);
        Assert.Equal("Github", r[0].Title);
        Assert.Equal("https://github.com", r[0].Url);
        Assert.Equal("abcd-efgh ijkl-mnop\nqrst-uvwx", r[0].BackupCodes);
        Assert.Equal("Hulu", r[1].Title);
    }

    [Fact]
    public void Unlabeled_run_of_codes_after_a_login_is_detected_with_a_warning()
    {
        const string text = """
            Epic Games
            email: tyler@example.com
            password: Ep1c!Games
            11111111
            22222222
            33333333
            44444444
            """;
        var r = Assert.Single(TextImporter.Parse(text));
        Assert.Equal("11111111\n22222222\n33333333\n44444444", r.BackupCodes);
        Assert.Contains(r.Warnings, w => w.Contains("backup codes"));
        Assert.True(r.TwoFactorEnabled);
    }

    [Fact]
    public void Numbered_codes_and_notes_and_pins()
    {
        const string text = """
            Bank
            login: tperkins
            password: B4nk!ng-Secure
            PIN: 4821
            security question: first car? mazdaspeed3
            Backup codes:
            1. 9182-7364
            2. 5647-3829
            """;
        var r = Assert.Single(TextImporter.Parse(text));
        Assert.Equal("tperkins", r.Username);
        Assert.Contains("PIN: 4821", r.Notes);
        Assert.Contains("security question: first car? mazdaspeed3", r.Notes);
        Assert.Equal("9182-7364\n5647-3829", r.BackupCodes);
    }

    [Fact]
    public void Two_passwords_in_a_row_become_two_entries()
    {
        const string text = """
            password: one
            password: two
            """;
        var r = TextImporter.Parse(text);
        Assert.Equal(2, r.Count);
        Assert.All(r, c => Assert.Contains(c.Warnings, w => w.Contains("No name")));
    }

    [Fact]
    public void Decorations_and_blank_noise_are_ignored()
    {
        const string text = """
            ===== GAMES =====
            -------------

            Steam | tylerjp | St3amPass

            ~~~~~~~~
            """;
        var r = Assert.Single(TextImporter.Parse(text));
        Assert.Equal("Steam", r.Title);
    }

    [Fact]
    public void Candidate_converts_to_entry()
    {
        var c = TextImporter.Parse("GitHub\nuser: a\npass: b\n2fa: yes via sms").Single();
        var e = c.ToEntry();
        Assert.Equal("GitHub", e.Title);
        Assert.True(e.TwoFactorEnabled);
        Assert.Equal(TwoFactorMethod.Sms, e.TwoFactorMethod);
        Assert.Contains("pass: b", c.SourceText);
    }

    [Fact]
    public void Empty_input_gives_nothing()
    {
        Assert.Empty(TextImporter.Parse(""));
        Assert.Empty(TextImporter.Parse("\n\n  \n"));
    }
}
