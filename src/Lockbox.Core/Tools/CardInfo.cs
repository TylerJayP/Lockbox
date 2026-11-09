namespace Lockbox.Core.Tools;

/// <summary>Display helpers for payment cards. Nothing here is a security control.</summary>
public static class CardInfo
{
    public static string Digits(string number) => new(number.Where(char.IsAsciiDigit).ToArray());

    public static string Brand(string number)
    {
        string d = Digits(number);
        if (d.Length == 0) return "Card";
        if (d[0] == '4') return "Visa";
        if (d.Length >= 2 && (d[..2] is "34" or "37")) return "American Express";
        if (d.Length >= 2 && int.TryParse(d[..2], out int two) && two is >= 51 and <= 55) return "Mastercard";
        if (d.Length >= 4 && int.TryParse(d[..4], out int four) && four is >= 2221 and <= 2720) return "Mastercard";
        if (d.StartsWith("6011") || d.StartsWith("65") || (d.Length >= 3 && int.TryParse(d[..3], out int three) && three is >= 644 and <= 649)) return "Discover";
        return "Card";
    }

    public static string Last4(string number)
    {
        string d = Digits(number);
        return d.Length >= 4 ? d[^4..] : d;
    }

    /// <summary>"Visa •••• 4417", or just the brand when there's no number.</summary>
    public static string Summary(string number)
    {
        string last = Last4(number);
        return last.Length == 0 ? Brand(number) : $"{Brand(number)} •••• {last}";
    }

    /// <summary>Luhn checksum: catches most typos in a card number.</summary>
    public static bool LooksValid(string number)
    {
        string d = Digits(number);
        if (d.Length is < 12 or > 19) return false;
        int sum = 0;
        bool dbl = false;
        for (int i = d.Length - 1; i >= 0; i--)
        {
            int n = d[i] - '0';
            if (dbl) { n *= 2; if (n > 9) n -= 9; }
            sum += n;
            dbl = !dbl;
        }
        return sum % 10 == 0;
    }

    /// <summary>Groups digits the way they're printed (Amex 4-6-5, others in fours).</summary>
    public static string Format(string number)
    {
        string d = Digits(number);
        if (d.Length == 0) return number;
        int[] groups = Brand(d) == "American Express" ? [4, 6, 5] : [4, 4, 4, 4, 4];
        var parts = new List<string>();
        int i = 0;
        foreach (int g in groups)
        {
            if (i >= d.Length) break;
            parts.Add(d.Substring(i, Math.Min(g, d.Length - i)));
            i += g;
        }
        if (i < d.Length) parts.Add(d[i..]);
        return string.Join(" ", parts);
    }
}

/// <summary>Builds the standard "WIFI:" text that phone cameras turn into a join-network prompt.</summary>
public static class WifiQr
{
    public static string Payload(string networkName, string password, Model.WifiSecurity security, bool hidden)
    {
        string type = security switch
        {
            Model.WifiSecurity.Open => "nopass",
            Model.WifiSecurity.Wep => "WEP",
            _ => "WPA",
        };
        var sb = new System.Text.StringBuilder();
        sb.Append("WIFI:T:").Append(type).Append(";S:").Append(Escape(networkName)).Append(';');
        if (security != Model.WifiSecurity.Open) sb.Append("P:").Append(Escape(password)).Append(';');
        if (hidden) sb.Append("H:true;");
        sb.Append(';');
        return sb.ToString();
    }

    /// <summary>Backslash-escapes the characters the format reserves.</summary>
    public static string Escape(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (c is '\\' or ';' or ',' or ':' or '"') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }
}
