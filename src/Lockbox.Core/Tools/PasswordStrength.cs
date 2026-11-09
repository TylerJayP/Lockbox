using Lockbox.Core.Model;

namespace Lockbox.Core.Tools;

public enum StrengthLevel { VeryWeak = 0, Weak = 1, Fair = 2, Strong = 3, VeryStrong = 4 }

public sealed record StrengthResult(StrengthLevel Level, double GuessesLog10, string Feedback, string CrackTime)
{
    public string Label => Level switch
    {
        StrengthLevel.VeryWeak => "Very weak",
        StrengthLevel.Weak => "Weak",
        StrengthLevel.Fair => "Fair",
        StrengthLevel.Strong => "Strong",
        _ => "Very strong",
    };
}

/// <summary>
/// Strength estimates come from zxcvbn (Dropbox's algorithm), which catches dictionary words,
/// keyboard patterns, dates and "Password123!"-style substitutions that naive length × charset math misses.
/// </summary>
public static class PasswordStrength
{
    /// <summary>
    /// Bar for the master password and recovery passphrase. Score 4 means zxcvbn estimates
    /// at least 10^10 guesses; on top of that every guess costs a full Argon2id run.
    /// </summary>
    public const StrengthLevel MasterMinimum = StrengthLevel.VeryStrong;
    public const int MasterMinLength = 12;

    public static StrengthResult Evaluate(string password, IEnumerable<string>? context = null)
    {
        if (string.IsNullOrEmpty(password))
            return new StrengthResult(StrengthLevel.VeryWeak, 0, "", "instantly");

        // zxcvbn gets slow and pointless on very long inputs; anything this long is strong anyway.
        string sample = password.Length > 100 ? password[..100] : password;
        var r = Zxcvbn.Core.EvaluatePassword(sample, context?.Where(s => !string.IsNullOrWhiteSpace(s)));

        static string Sentence(string s) { s = s.Trim(); return s.EndsWith('.') || s.EndsWith('!') || s.EndsWith('?') ? s : s + "."; }
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(r.Feedback.Warning)) parts.Add(Sentence(r.Feedback.Warning));
        parts.AddRange(r.Feedback.Suggestions.Where(s => !string.IsNullOrWhiteSpace(s)).Select(Sentence));

        return new StrengthResult((StrengthLevel)Math.Clamp(r.Score, 0, 4), r.GuessesLog10,
            string.Join(" ", parts), r.CrackTimeDisplay.OfflineSlowHashing1e4PerSecond);
    }

    /// <summary>Returns an error message, or null if the master password / passphrase is acceptable.</summary>
    public static string? ValidateMasterPassword(string password, IEnumerable<string>? context = null)
    {
        if (password.Length < MasterMinLength) return $"Use at least {MasterMinLength} characters. A generated passphrase is easiest.";
        var s = Evaluate(password, context);
        if (s.Level < MasterMinimum)
            return "Too guessable for a master password. " + (s.Feedback.Length > 0 ? s.Feedback : "Add more words or use a generated passphrase.");
        return null;
    }
}

[Flags]
public enum EntryIssue
{
    None = 0,
    Weak = 1,
    Reused = 2,
    Empty = 4,
    /// <summary>Password hasn't changed in over two years.</summary>
    Old = 8,
    /// <summary>A login with a password but no two-factor authentication.</summary>
    NoTwoFactor = 16,
}

public static class VaultAudit
{
    public static readonly TimeSpan OldAfter = TimeSpan.FromDays(730);

    /// <summary>The issues that put an item in the sidebar's "needs attention" list.</summary>
    public const EntryIssue AttentionMask = EntryIssue.Weak | EntryIssue.Reused | EntryIssue.Empty;

    /// <summary>Flags weak (zxcvbn score ≤ 2), reused, empty and old passwords, and logins without 2FA.</summary>
    public static Dictionary<Guid, EntryIssue> Analyze(IEnumerable<Entry> entries, DateTimeOffset? now = null)
    {
        var list = entries.ToList();
        var at = now ?? DateTimeOffset.UtcNow;
        var result = list.ToDictionary(e => e.Id, _ => EntryIssue.None);
        var checkable = list.Where(e => e.HasCheckablePassword).ToList();

        foreach (var group in checkable.Where(e => e.Password.Length > 0).GroupBy(e => e.Password, StringComparer.Ordinal))
            if (group.Count() > 1)
                foreach (var e in group) result[e.Id] |= EntryIssue.Reused;

        var cache = new Dictionary<string, StrengthLevel>(StringComparer.Ordinal);
        foreach (var e in checkable)
        {
            if (e.Password.Length == 0) { result[e.Id] |= EntryIssue.Empty; continue; }
            if (!cache.TryGetValue(e.Password, out var level))
                cache[e.Password] = level = PasswordStrength.Evaluate(e.Password, [e.Username, e.Title]).Level;
            if (level <= StrengthLevel.Fair) result[e.Id] |= EntryIssue.Weak;
            if (at - e.PasswordChangedUtc > OldAfter) result[e.Id] |= EntryIssue.Old;
            if (e.Type == ItemType.Login && !e.TwoFactorEnabled) result[e.Id] |= EntryIssue.NoTwoFactor;
        }
        return result;
    }
}
