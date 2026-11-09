using Lockbox.Core.Model;
using Lockbox.Core.Tools;

namespace Lockbox.Tests;

public sealed class GeneratorTests
{
    [Theory]
    [InlineData(8)]
    [InlineData(20)]
    [InlineData(64)]
    public void Has_requested_length_and_one_of_each_class(int length)
    {
        for (int i = 0; i < 200; i++)
        {
            var p = PasswordGenerator.Generate(new PasswordOptions(length));
            Assert.Equal(length, p.Length);
            Assert.Contains(p, char.IsLower);
            Assert.Contains(p, char.IsUpper);
            Assert.Contains(p, char.IsDigit);
            Assert.Contains(p, c => PasswordGenerator.SymbolChars.Contains(c));
        }
    }

    [Fact]
    public void Respects_options()
    {
        for (int i = 0; i < 200; i++)
        {
            var p = PasswordGenerator.Generate(new PasswordOptions(30, Symbols: false, ExcludeAmbiguous: true));
            Assert.DoesNotContain(p, c => PasswordGenerator.SymbolChars.Contains(c));
            Assert.DoesNotContain(p, c => PasswordGenerator.AmbiguousChars.Contains(c));
            Assert.All(p, c => Assert.True(char.IsAsciiLetterOrDigit(c)));
        }
    }

    [Fact]
    public void Digits_only_is_allowed_and_nothing_selected_is_an_error()
    {
        var pin = PasswordGenerator.Generate(new PasswordOptions(10, false, false, true, false));
        Assert.All(pin, c => Assert.True(char.IsAsciiDigit(c)));
        Assert.Throws<ArgumentException>(() => PasswordGenerator.Generate(new PasswordOptions(10, false, false, false, false)));
    }

    [Fact]
    public void Characters_are_roughly_uniform()
    {
        var counts = new Dictionary<char, int>();
        var opts = new PasswordOptions(100, true, false, false, false);
        for (int i = 0; i < 400; i++)
            foreach (var c in PasswordGenerator.Generate(opts)) counts[c] = counts.GetValueOrDefault(c) + 1;
        // 40,000 chars over 26 letters ≈ 1538 each; allow a wide margin.
        Assert.Equal(26, counts.Count);
        Assert.All(counts.Values, n => Assert.InRange(n, 1300, 1800));
    }

    [Fact]
    public void Passphrases_use_the_word_list()
    {
        Assert.Equal(7772, PasswordGenerator.WordList.Count);
        var words = PasswordGenerator.GeneratePassphrase(6).Split('-');
        Assert.Equal(6, words.Length);
        Assert.All(words, w => Assert.Contains(w, PasswordGenerator.WordList));
        Assert.InRange(PasswordGenerator.PassphraseEntropyBits(6), 77, 78);
    }

    [Fact]
    public void Generated_passphrase_passes_master_password_rules()
    {
        for (int i = 0; i < 20; i++)
            Assert.Null(PasswordStrength.ValidateMasterPassword(PasswordGenerator.GeneratePassphrase(6)));
    }
}

public sealed class StrengthTests
{
    [Theory]
    [InlineData("Password123!")]
    [InlineData("tyler1995")]
    [InlineData("qwertyuiop12")]
    [InlineData("iloveyou2024!")]
    public void Common_patterns_are_rejected_as_master_passwords(string pw)
    {
        Assert.NotNull(PasswordStrength.ValidateMasterPassword(pw));
        Assert.True(PasswordStrength.Evaluate(pw).Level <= StrengthLevel.Fair);
    }

    [Fact]
    public void Personal_info_counts_against_a_password()
    {
        var plain = PasswordStrength.Evaluate("TylerPerkins88");
        var withContext = PasswordStrength.Evaluate("TylerPerkins88", ["tyler", "perkins"]);
        Assert.True(withContext.GuessesLog10 < plain.GuessesLog10);
    }

    [Fact]
    public void Audit_flags_reused_weak_and_empty()
    {
        var a = new Entry { Title = "A", Password = "Xk9#mQ2$vL7!pR4@zT" };
        var b = new Entry { Title = "B", Password = "Xk9#mQ2$vL7!pR4@zT" };
        var c = new Entry { Title = "C", Password = "password1" };
        var d = new Entry { Title = "D", Password = "" };
        var r = VaultAudit.Analyze([a, b, c, d]).ToDictionary(kv => kv.Key, kv => kv.Value & VaultAudit.AttentionMask);
        Assert.Equal(EntryIssue.Reused, r[a.Id]);
        Assert.Equal(EntryIssue.Reused, r[b.Id]);
        Assert.Equal(EntryIssue.Weak, r[c.Id]);
        Assert.Equal(EntryIssue.Empty, r[d.Id]);
    }
}
