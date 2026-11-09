using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Lockbox.Core.Tools;

public sealed record PasswordOptions(
    int Length = 20,
    bool Lowercase = true,
    bool Uppercase = true,
    bool Digits = true,
    bool Symbols = true,
    bool ExcludeAmbiguous = false);

/// <summary>Everything here uses the OS CSPRNG (<see cref="RandomNumberGenerator"/>), never System.Random.</summary>
public static class PasswordGenerator
{
    public const string LowerChars = "abcdefghijklmnopqrstuvwxyz";
    public const string UpperChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    public const string DigitChars = "0123456789";
    public const string SymbolChars = "!@#$%^&*()-_=+[]{};:,.?/~";
    public const string AmbiguousChars = "Il1O0o|";

    public const int MinLength = 8;
    public const int MaxLength = 128;

    private static readonly Lazy<string[]> Words = new(LoadWords);

    /// <summary>The EFF large word list (as shipped by KeePassXC), used for passphrases.</summary>
    public static IReadOnlyList<string> WordList => Words.Value;

    public static string Generate(PasswordOptions o)
    {
        int length = Math.Clamp(o.Length, MinLength, MaxLength);
        var sets = CharacterSets(o);
        if (sets.Count == 0) throw new ArgumentException("Pick at least one kind of character.");

        string all = string.Concat(sets);
        var chars = new char[length];

        // One character from each chosen set so site rules like "needs a digit" are met...
        for (int i = 0; i < sets.Count && i < length; i++)
            chars[i] = sets[i][RandomNumberGenerator.GetInt32(sets[i].Length)];
        // ...the rest uniformly from all of them...
        for (int i = sets.Count; i < length; i++)
            chars[i] = all[RandomNumberGenerator.GetInt32(all.Length)];
        // ...then shuffle so the guaranteed characters aren't always at the front.
        RandomNumberGenerator.Shuffle(chars.AsSpan());

        return new string(chars);
    }

    public static double EntropyBits(PasswordOptions o)
    {
        int pool = CharacterSets(o).Sum(s => s.Length);
        return pool == 0 ? 0 : Math.Clamp(o.Length, MinLength, MaxLength) * Math.Log2(pool);
    }

    /// <summary>Diceware-style passphrase, e.g. "orbit-velvet-cactus-mango-ripple-anchor". Six words ≈ 77 bits.</summary>
    public static string GeneratePassphrase(int words = 6, string separator = "-", bool capitalize = false, bool addDigit = false)
    {
        words = Math.Clamp(words, 4, 16);
        var list = WordList;
        var picked = new string[words];
        for (int i = 0; i < words; i++)
        {
            string w = list[RandomNumberGenerator.GetInt32(list.Count)];
            picked[i] = capitalize ? char.ToUpperInvariant(w[0]) + w[1..] : w;
        }
        if (addDigit)
        {
            int at = RandomNumberGenerator.GetInt32(words);
            picked[at] += RandomNumberGenerator.GetInt32(10).ToString();
        }
        return string.Join(separator, picked);
    }

    public static double PassphraseEntropyBits(int words) => words * Math.Log2(WordList.Count);

    private static List<string> CharacterSets(PasswordOptions o)
    {
        var sets = new List<string>();
        if (o.Lowercase) sets.Add(LowerChars);
        if (o.Uppercase) sets.Add(UpperChars);
        if (o.Digits) sets.Add(DigitChars);
        if (o.Symbols) sets.Add(SymbolChars);
        if (o.ExcludeAmbiguous)
            sets = sets.Select(s => new string(s.Where(c => !AmbiguousChars.Contains(c)).ToArray())).Where(s => s.Length > 0).ToList();
        return sets;
    }

    private static string[] LoadWords()
    {
        var asm = typeof(PasswordGenerator).Assembly;
        string name = asm.GetManifestResourceNames().Single(n => n.EndsWith("eff_large.wordlist", StringComparison.Ordinal));
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }
}
