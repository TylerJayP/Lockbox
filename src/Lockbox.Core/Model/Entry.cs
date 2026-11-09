namespace Lockbox.Core.Model;

public enum TwoFactorMethod
{
    None,
    AuthenticatorApp,
    Sms,
    Email,
    HardwareKey,
    Other,
}

/// <summary>Kind of vault item. Login must stay 0 so vaults saved before item types existed load as logins.</summary>
public enum ItemType
{
    Login = 0,
    SecureNote = 1,
    Card = 2,
    WiFi = 3,
}

public enum WifiSecurity
{
    Wpa2 = 0,
    Wpa3 = 1,
    WpaWpa2 = 2,
    Wep = 3,
    Open = 4,
}

/// <summary>Payment card details (only used when <see cref="Entry.Type"/> is <see cref="ItemType.Card"/>).</summary>
public sealed record CardData
{
    public string CardholderName { get; init; } = "";
    public string Number { get; init; } = "";
    /// <summary>As printed on the card, e.g. "08/29".</summary>
    public string Expiry { get; init; } = "";
    public string SecurityCode { get; init; } = "";
    public string Pin { get; init; } = "";
}

/// <summary>Wi-Fi network details. The network password is <see cref="Entry.Password"/>, so strength checks and history apply.</summary>
public sealed record WifiData
{
    public string NetworkName { get; init; } = "";
    public WifiSecurity Security { get; init; } = WifiSecurity.Wpa2;
    public bool HiddenNetwork { get; init; }
}

/// <summary>Any extra labelled value, e.g. "Account number" or "Security question". Hidden fields are masked like passwords.</summary>
public sealed record CustomField(string Name, string Value, bool Hidden);

/// <summary>Points at an encrypted file stored inside the vault. The bytes live in the vault's attachment store.</summary>
public sealed record AttachmentRef(Guid Id, string FileName, long Size, DateTimeOffset AddedUtc, string Sha256);

/// <summary>One vault item. Backup codes are NOT stored here; they live in the sealed recovery section.</summary>
public sealed class Entry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ItemType Type { get; set; } = ItemType.Login;
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";

    /// <summary>The "2FA is on for this account" checkmark.</summary>
    public bool TwoFactorEnabled { get; set; }
    public TwoFactorMethod TwoFactorMethod { get; set; }

    /// <summary>True when the sealed section holds backup codes for this entry (so the UI can show a badge while sealed).</summary>
    public bool HasBackupCodes { get; set; }

    public CardData? Card { get; set; }
    public WifiData? Wifi { get; set; }
    public List<CustomField> Fields { get; set; } = [];

    /// <summary>"Ask for the master password before showing or copying this item."</summary>
    public bool Reprompt { get; set; }

    public string Notes { get; set; } = "";
    public List<string> Tags { get; set; } = [];

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ModifiedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset PasswordChangedUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Previous passwords, newest first. Managed by <see cref="Vault.UpdateEntry"/>; edits to it from callers are ignored.</summary>
    public List<PasswordHistoryItem> PasswordHistory { get; set; } = [];

    /// <summary>Encrypted files attached to this item. Managed by the vault's attachment methods; edits from callers are ignored.</summary>
    public List<AttachmentRef> Attachments { get; set; } = [];

    /// <summary>Whether this kind of item has a password that strength/reuse checks apply to.</summary>
    public bool HasCheckablePassword => Type == ItemType.Login || (Type == ItemType.WiFi && Wifi?.Security != WifiSecurity.Open);

    public Entry Clone() => new()
    {
        Id = Id,
        Type = Type,
        Title = Title,
        Url = Url,
        Username = Username,
        Password = Password,
        TwoFactorEnabled = TwoFactorEnabled,
        TwoFactorMethod = TwoFactorMethod,
        HasBackupCodes = HasBackupCodes,
        Card = Card,
        Wifi = Wifi,
        Fields = [.. Fields],
        Reprompt = Reprompt,
        Notes = Notes,
        Tags = [.. Tags],
        CreatedUtc = CreatedUtc,
        ModifiedUtc = ModifiedUtc,
        PasswordChangedUtc = PasswordChangedUtc,
        PasswordHistory = [.. PasswordHistory],
        Attachments = [.. Attachments],
    };

    public bool ContentEquals(Entry o) =>
        Type == o.Type && Title == o.Title && Url == o.Url && Username == o.Username && Password == o.Password &&
        TwoFactorEnabled == o.TwoFactorEnabled && TwoFactorMethod == o.TwoFactorMethod &&
        Equals(Card, o.Card) && Equals(Wifi, o.Wifi) && Fields.SequenceEqual(o.Fields) && Reprompt == o.Reprompt &&
        Notes == o.Notes && Tags.SequenceEqual(o.Tags);
}

/// <summary>A password this login used before, and when it was replaced.</summary>
public sealed record PasswordHistoryItem(string Password, DateTimeOffset ReplacedUtc);

/// <summary>Per-vault behaviour settings (stored inside the encrypted vault, so they travel with it).</summary>
public sealed class VaultSettings
{
    public int AutoLockMinutes { get; set; } = 5;
    public int ClipboardClearSeconds { get; set; } = 20;
    public bool LockOnMinimize { get; set; } = true;

    /// <summary>How many old passwords to keep per login (0 turns history off).</summary>
    public int PasswordHistoryLimit { get; set; } = 10;
}
