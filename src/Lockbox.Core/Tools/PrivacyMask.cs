using System.Globalization;

namespace Lockbox.Core.Tools;

/// <summary>
/// Partly hides usernames for lists that are on screen a lot: "tyler@example.com" becomes
/// "ty•••@example.com". You can still tell your accounts apart, but someone glancing at the
/// screen can't read them.
/// </summary>
public static class PrivacyMask
{
    public const string Dots = "•••";

    public static string Username(string? username)
    {
        string s = (username ?? "").Trim();
        if (s.Length == 0) return "";

        int at = s.LastIndexOf('@');
        if (at > 0 && at < s.Length - 1)
        {
            string local = s[..at];
            int keep = Length(local) <= 3 ? 1 : 2;
            return Take(local, keep) + Dots + s[at..];
        }

        int n = Length(s);
        if (n <= 3) return Take(s, 1) + Dots;
        if (n < 6) return Take(s, 2) + Dots;
        return Take(s, 2) + Dots + TakeLast(s, 2);
    }

    // Count and cut by whole characters, so an emoji or accented letter is never split in half.
    private static int Length(string s) => new StringInfo(s).LengthInTextElements;
    private static string Take(string s, int count) => new StringInfo(s).SubstringByTextElements(0, Math.Min(count, Length(s)));
    private static string TakeLast(string s, int count)
    {
        var info = new StringInfo(s);
        int n = info.LengthInTextElements;
        return info.SubstringByTextElements(Math.Max(0, n - count));
    }
}
