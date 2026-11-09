using System.Text;
using Lockbox.Core.Model;

namespace Lockbox.Core.Export;

/// <summary>
/// Readable (UNENCRYPTED) export in Bitwarden's CSV layout, which Bitwarden imports directly and
/// KeePassXC, 1Password and spreadsheets can map. RFC 4180 quoting; every data cell is quoted.
/// </summary>
public static class CsvExport
{
    public static IReadOnlyList<string> BitwardenHeader { get; } =
        ["folder", "favorite", "type", "name", "notes", "fields", "reprompt", "login_uri", "login_username", "login_password", "login_totp"];

    /// <param name="backupCodes">Returns an entry's backup codes to include in its notes, or null to leave them out.</param>
    public static string ToBitwardenCsv(IEnumerable<Entry> entries, Func<Entry, string?>? backupCodes = null)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(",", BitwardenHeader)).Append("\r\n");

        foreach (var e in entries.OrderBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase))
        {
            // Bitwarden's CSV only knows "login" and "note"; cards become notes with their details as fields.
            var fields = new List<string>();
            string type = "login", uri = e.Url, user = e.Username, pass = e.Password;

            switch (e.Type)
            {
                case ItemType.SecureNote:
                    type = "note"; uri = user = pass = "";
                    break;
                case ItemType.Card when e.Card is { } c:
                    type = "note"; uri = user = pass = "";
                    fields.Add($"Cardholder: {c.CardholderName}");
                    fields.Add($"Card number: {c.Number}");
                    fields.Add($"Expiry: {c.Expiry}");
                    fields.Add($"Security code: {c.SecurityCode}");
                    if (c.Pin.Length > 0) fields.Add($"PIN: {c.Pin}");
                    break;
                case ItemType.WiFi when e.Wifi is { } w:
                    user = w.NetworkName; uri = "";
                    fields.Add($"Wi-Fi security: {Describe(w.Security)}");
                    if (w.HiddenNetwork) fields.Add("Hidden network: yes");
                    break;
            }

            if (e.Type == ItemType.Login && e.TwoFactorEnabled) fields.Add($"2FA: {Describe(e.TwoFactorMethod)}");
            if (e.Tags.Count > 0) fields.Add($"Tags: {string.Join(", ", e.Tags)}");
            foreach (var f in e.Fields) fields.Add($"{OneLine(f.Name)}: {OneLine(f.Value)}");
            if (e.Attachments.Count > 0) fields.Add($"Attachments (not exported): {string.Join(", ", e.Attachments.Select(a => a.FileName))}");

            string notes = e.Notes.Trim();
            if (backupCodes?.Invoke(e) is { } codes && codes.Trim().Length > 0)
                notes = (notes.Length > 0 ? notes + "\n\n" : "") + "Backup codes:\n" + codes.Trim();

            AppendRow(sb,
            [
                e.Tags.FirstOrDefault() ?? "", // folder
                "",                            // favorite
                type,
                e.Title,
                notes,
                string.Join("\n", fields),
                e.Reprompt ? "1" : "0",        // reprompt
                uri,
                user,
                pass,
                "",                            // login_totp (Lockbox doesn't store TOTP secrets)
            ]);
        }
        return sb.ToString();
    }

    private static string OneLine(string s) => s.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');

    public static string Describe(WifiSecurity s) => s switch
    {
        WifiSecurity.Wpa3 => "WPA3",
        WifiSecurity.WpaWpa2 => "WPA/WPA2",
        WifiSecurity.Wep => "WEP",
        WifiSecurity.Open => "Open (no password)",
        _ => "WPA2",
    };

    public static string Describe(TwoFactorMethod m) => m switch
    {
        TwoFactorMethod.AuthenticatorApp => "Authenticator app",
        TwoFactorMethod.Sms => "Text message (SMS)",
        TwoFactorMethod.Email => "Email code",
        TwoFactorMethod.HardwareKey => "Security key / passkey",
        TwoFactorMethod.Other => "Other",
        _ => "On",
    };

    private static void AppendRow(StringBuilder sb, IReadOnlyList<string> cells)
    {
        for (int i = 0; i < cells.Count; i++)
        {
            if (i > 0) sb.Append(',');
            string v = (cells[i] ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
            sb.Append('"').Append(v.Replace("\"", "\"\"")).Append('"');
        }
        sb.Append("\r\n");
    }
}
