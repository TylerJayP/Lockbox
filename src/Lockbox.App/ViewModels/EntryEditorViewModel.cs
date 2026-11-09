using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Security.Cryptography;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lockbox.App.Services;
using Lockbox.Core;
using Lockbox.Core.Model;
using Lockbox.Core.Tools;

namespace Lockbox.App.ViewModels;

public partial class EntryEditorViewModel : ObservableObject
{
    private readonly VaultViewModel _owner;
    private Entry _original;
    private string _originalCodes = "";
    private bool _loading;

    public EntryEditorViewModel(VaultViewModel owner, Entry entry)
    {
        _owner = owner;
        _original = entry.Clone();
        IsSealed = entry.Reprompt;
        Fields.CollectionChanged += OnFieldsChanged;
        Load(entry);
        if (owner.Vault.IsRecoveryUnlocked) LoadCodes();
        RegeneratePreview();
    }

    public Guid EntryId => _original.Id;
    public ItemType Type => _original.Type;
    public bool IsLogin => Type == ItemType.Login;
    public bool IsNote => Type == ItemType.SecureNote;
    public bool IsCard => Type == ItemType.Card;
    public bool IsWifi => Type == ItemType.WiFi;
    public bool HasPasswordField => IsLogin || IsWifi;
    public bool IsNotNote => !IsNote;
    public string PasswordLabel => IsWifi ? "Wi-Fi password" : "Password";
    public string TypeLabel => Type switch
    {
        ItemType.SecureNote => "Secure note",
        ItemType.Card => "Payment card",
        ItemType.WiFi => "Wi-Fi network",
        _ => "Login",
    };
    public Geometry? TypeIcon => Ui.Icon(Type switch
    {
        ItemType.SecureNote => "IconNote",
        ItemType.Card => "IconCard",
        ItemType.WiFi => "IconWifi",
        _ => "IconKey",
    });

    public static IReadOnlyList<TwoFactorOption> TwoFactorOptions => TwoFactorOption.All;
    public static IReadOnlyList<WifiSecurityOption> WifiSecurityOptions => WifiSecurityOption.All;

    // ── protected items ──
    [ObservableProperty] private bool _isSealed;
    public bool IsOpen => !IsSealed;
    partial void OnIsSealedChanged(bool value) => OnPropertyChanged(nameof(IsOpen));
    public void Unseal() => IsSealed = false;

    // ── common ──
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _notes = "";
    [ObservableProperty] private string _tags = "";
    [ObservableProperty] private bool _reprompt;

    // ── login / Wi-Fi ──
    [ObservableProperty] private string _url = "";
    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private bool _revealPassword;
    [ObservableProperty] private bool _twoFactorEnabled;
    [ObservableProperty] private TwoFactorOption _twoFactorMethod = TwoFactorOption.All[0];

    [ObservableProperty] private string _networkName = "";
    [ObservableProperty] private WifiSecurityOption _wifiSecurity = WifiSecurityOption.All[0];
    [ObservableProperty] private bool _hiddenNetwork;
    [ObservableProperty] private Bitmap? _wifiQr;
    public bool HasWifiQr => WifiQr != null;
    partial void OnWifiQrChanged(Bitmap? value) => OnPropertyChanged(nameof(HasWifiQr));

    // ── card ──
    [ObservableProperty] private string _cardholderName = "";
    [ObservableProperty] private string _cardNumber = "";
    [ObservableProperty] private string _cardExpiry = "";
    [ObservableProperty] private string _cardCode = "";
    [ObservableProperty] private string _cardPin = "";
    [ObservableProperty] private bool _revealCard;
    public string CardHint
    {
        get
        {
            string digits = CardInfo.Digits(CardNumber);
            if (digits.Length == 0) return "";
            string brand = CardInfo.Brand(CardNumber);
            return CardInfo.LooksValid(CardNumber) ? brand : $"{brand} · this number doesn't pass the card checksum, so check it for a typo";
        }
    }

    // ── backup codes ──
    [ObservableProperty] private string _backupCodes = "";
    [ObservableProperty] private bool _codesUnlocked;
    [ObservableProperty] private bool _hasCodes;

    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private bool _focusTitleRequested;

    [ObservableProperty] private double _strengthValue;
    [ObservableProperty] private IBrush _strengthBrush = Brushes.Transparent;
    [ObservableProperty] private string _strengthText = "";
    [ObservableProperty] private string _issueText = "";

    [ObservableProperty] private string _createdText = "";
    [ObservableProperty] private string _modifiedText = "";
    [ObservableProperty] private string _passwordAgeText = "";

    // ── custom fields ──
    public ObservableCollection<CustomFieldRowViewModel> Fields { get; } = [];
    public bool HasFields => Fields.Count > 0;

    private void OnFieldsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null) foreach (CustomFieldRowViewModel r in e.NewItems) r.PropertyChanged += OnFieldRowChanged;
        if (e.OldItems != null) foreach (CustomFieldRowViewModel r in e.OldItems) r.PropertyChanged -= OnFieldRowChanged;
        OnPropertyChanged(nameof(HasFields));
        if (!_loading) RecomputeDirty();
    }

    private void OnFieldRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_loading && e.PropertyName is nameof(CustomFieldRowViewModel.Name) or nameof(CustomFieldRowViewModel.Value) or nameof(CustomFieldRowViewModel.Hidden))
            RecomputeDirty();
    }

    // ── attachments ──
    public ObservableCollection<AttachmentRowViewModel> Attachments { get; } = [];
    public bool HasAttachments => Attachments.Count > 0;

    private void LoadAttachments(Entry e)
    {
        Attachments.Clear();
        foreach (var a in e.Attachments) Attachments.Add(new AttachmentRowViewModel(a));
        OnPropertyChanged(nameof(HasAttachments));
    }

    // ── password history ──
    public ObservableCollection<HistoryRowViewModel> History { get; } = [];
    [ObservableProperty] private bool _historyOpen;
    [ObservableProperty] private bool _historyRevealed;
    public bool HasHistory => History.Count > 0;
    public string HistoryToggleText => HistoryOpen ? "Hide password history" : $"Password history ({History.Count})";

    partial void OnHistoryOpenChanged(bool value) => OnPropertyChanged(nameof(HistoryToggleText));
    partial void OnHistoryRevealedChanged(bool value) { foreach (var row in History) row.Revealed = value; }

    private void LoadHistory(Entry e)
    {
        History.Clear();
        foreach (var h in e.PasswordHistory) History.Add(new HistoryRowViewModel(h) { Revealed = HistoryRevealed });
        if (History.Count == 0) HistoryOpen = false;
        OnPropertyChanged(nameof(HasHistory));
        OnPropertyChanged(nameof(HistoryToggleText));
    }

    // ── generator ──
    [ObservableProperty] private bool _generatorOpen;
    [ObservableProperty] private bool _genPassphrase;
    [ObservableProperty] private int _genLength = 20;
    [ObservableProperty] private int _genWords = 5;
    [ObservableProperty] private bool _genUpper = true;
    [ObservableProperty] private bool _genLower = true;
    [ObservableProperty] private bool _genDigits = true;
    [ObservableProperty] private bool _genSymbols = true;
    [ObservableProperty] private bool _genNoAmbiguous;
    [ObservableProperty] private string _genPreview = "";
    [ObservableProperty] private string _genInfo = "";

    public bool GenRandom
    {
        get => !GenPassphrase;
        set => GenPassphrase = !value;
    }

    public bool HasUrl => Url.Trim().Length > 0;
    public bool CodesLocked => !CodesUnlocked;
    public bool CodesDirty => CodesUnlocked && BackupCodes.Trim().Replace("\r\n", "\n") != _originalCodes;
    public bool HasIssue => IssueText.Length > 0;
    public bool RecoveryConfigured => _owner.Vault.IsRecoveryConfigured;
    public string CodesLockedText => HasCodes
        ? "Backup codes saved — sealed with your recovery passphrase."
        : RecoveryConfigured ? "No backup codes saved. Unlock to add some." : "No backup codes saved yet.";
    public string UnlockCodesLabel => RecoveryConfigured ? "Unlock codes" : "Add backup codes";

    private void Load(Entry e)
    {
        _loading = true;
        Title = e.Title;
        Url = e.Url;
        Username = e.Username;
        Password = e.Password;
        TwoFactorEnabled = e.TwoFactorEnabled;
        TwoFactorMethod = TwoFactorOption.For(e.TwoFactorMethod == Core.Model.TwoFactorMethod.None ? Core.Model.TwoFactorMethod.AuthenticatorApp : e.TwoFactorMethod);
        Notes = e.Notes;
        Tags = string.Join(", ", e.Tags);
        Reprompt = e.Reprompt;
        HasCodes = e.HasBackupCodes;

        var card = e.Card ?? new CardData();
        CardholderName = card.CardholderName;
        CardNumber = card.Number;
        CardExpiry = card.Expiry;
        CardCode = card.SecurityCode;
        CardPin = card.Pin;

        var wifi = e.Wifi ?? new WifiData();
        NetworkName = wifi.NetworkName;
        WifiSecurity = WifiSecurityOption.For(wifi.Security);
        HiddenNetwork = wifi.HiddenNetwork;
        WifiQr = null;

        Fields.Clear();
        foreach (var f in e.Fields) Fields.Add(new CustomFieldRowViewModel(f.Name, f.Value, f.Hidden));

        CreatedText = $"Created {Ui.Ago(e.CreatedUtc)}";
        ModifiedText = $"Edited {Ui.Ago(e.ModifiedUtc)}";
        PasswordAgeText = HasPasswordField ? $"Password changed {Ui.Ago(e.PasswordChangedUtc)}" : "";
        LoadHistory(e);
        LoadAttachments(e);
        _loading = false;
        UpdateStrength();
        UpdateIssue();
        OnPropertyChanged(nameof(CardHint));
        IsDirty = false;
    }

    private void LoadCodes()
    {
        _loading = true;
        BackupCodes = _owner.Vault.GetBackupCodes(EntryId);
        _originalCodes = BackupCodes;
        CodesUnlocked = true;
        _loading = false;
        RefreshCodeTexts();
    }

    public void OnRecoveryLocked()
    {
        _loading = true;
        BackupCodes = "";
        _originalCodes = "";
        CodesUnlocked = false;
        _loading = false;
        RecomputeDirty();
        RefreshCodeTexts();
    }

    private void RefreshCodeTexts()
    {
        OnPropertyChanged(nameof(CodesLocked));
        OnPropertyChanged(nameof(CodesLockedText));
        OnPropertyChanged(nameof(UnlockCodesLabel));
        OnPropertyChanged(nameof(RecoveryConfigured));
    }

    public void BeginNew()
    {
        FocusTitleRequested = true;
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading) return;
        switch (e.PropertyName)
        {
            case nameof(Title) or nameof(Url) or nameof(Username) or nameof(TwoFactorEnabled) or nameof(TwoFactorMethod)
                or nameof(Notes) or nameof(Tags) or nameof(BackupCodes) or nameof(Reprompt)
                or nameof(CardholderName) or nameof(CardExpiry) or nameof(CardCode) or nameof(CardPin):
                RecomputeDirty();
                if (e.PropertyName == nameof(Url)) OnPropertyChanged(nameof(HasUrl));
                break;
            case nameof(CardNumber):
                RecomputeDirty();
                OnPropertyChanged(nameof(CardHint));
                break;
            case nameof(NetworkName) or nameof(WifiSecurity) or nameof(HiddenNetwork):
                RecomputeDirty();
                WifiQr = null; // details changed; the code would be stale
                break;
            case nameof(Password):
                RecomputeDirty();
                UpdateStrength();
                WifiQr = null;
                break;
            case nameof(GenPassphrase):
                OnPropertyChanged(nameof(GenRandom));
                RegeneratePreview();
                break;
            case nameof(GenLength) or nameof(GenWords) or nameof(GenUpper) or nameof(GenLower) or nameof(GenDigits) or nameof(GenSymbols) or nameof(GenNoAmbiguous):
                RegeneratePreview();
                break;
            case nameof(CodesUnlocked):
                OnPropertyChanged(nameof(CodesLocked));
                break;
        }
    }

    private void RecomputeDirty() => IsDirty = !ToEntry().ContentEquals(_original) || CodesDirty;

    private void UpdateStrength()
    {
        if (!HasPasswordField || Password.Length == 0) { StrengthValue = 0; StrengthText = ""; StrengthBrush = Brushes.Transparent; return; }
        var s = PasswordStrength.Evaluate(Password, [Username, Title, NetworkName]);
        StrengthValue = (int)s.Level + 1;
        StrengthBrush = Ui.StrengthBrush(s.Level);
        StrengthText = s.Level <= StrengthLevel.Fair && s.Feedback.Length > 0 ? $"{s.Label} — {s.Feedback}" : s.Label;
    }

    private void UpdateIssue()
    {
        var i = _owner.IssuesFor(EntryId);
        var parts = new List<string>();
        if (i.HasFlag(EntryIssue.Reused)) parts.Add("This password is used for more than one item. If one site leaks, the others are exposed too.");
        if (i.HasFlag(EntryIssue.Old)) parts.Add("It hasn't been changed in over two years.");
        IssueText = string.Join(" ", parts);
        OnPropertyChanged(nameof(HasIssue));
    }

    public Entry ToEntry()
    {
        var e = _original.Clone();
        e.Title = Title.Trim();
        e.Notes = Notes;
        e.Tags = Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        e.Reprompt = Reprompt;
        e.Fields = Fields
            .Where(f => f.Name.Trim().Length > 0 || f.Value.Length > 0)
            .Select(f => new CustomField(f.Name.Trim(), f.Value, f.Hidden)).ToList();

        switch (Type)
        {
            case ItemType.Login:
                e.Url = Url.Trim();
                e.Username = Username.Trim();
                e.Password = Password;
                e.TwoFactorEnabled = TwoFactorEnabled;
                e.TwoFactorMethod = TwoFactorEnabled ? TwoFactorMethod.Value : Core.Model.TwoFactorMethod.None;
                break;
            case ItemType.WiFi:
                e.Password = Password;
                e.Wifi = new WifiData { NetworkName = NetworkName.Trim(), Security = WifiSecurity.Value, HiddenNetwork = HiddenNetwork };
                break;
            case ItemType.Card:
                e.Card = new CardData
                {
                    CardholderName = CardholderName.Trim(), Number = CardNumber.Trim(), Expiry = CardExpiry.Trim(),
                    SecurityCode = CardCode.Trim(), Pin = CardPin.Trim(),
                };
                break;
        }
        return e;
    }

    /// <summary>Pushes edits into the in-memory vault (not to disk). Safe to call when nothing changed.</summary>
    public void ApplyToVault()
    {
        if (!IsDirty) return;
        if (!_owner.Vault.Entries.Any(x => x.Id == EntryId)) return;

        var saved = _owner.Vault.UpdateEntry(ToEntry());
        if (CodesDirty && _owner.Vault.IsRecoveryUnlocked)
        {
            _owner.Vault.SetBackupCodes(EntryId, BackupCodes);
            _originalCodes = BackupCodes.Trim().Replace("\r\n", "\n");
            saved = _owner.Vault.Entries.First(x => x.Id == EntryId);
        }
        _original = saved.Clone();
        LoadHistory(saved);
        HasCodes = saved.HasBackupCodes;
        ModifiedText = $"Edited {Ui.Ago(saved.ModifiedUtc)}";
        PasswordAgeText = HasPasswordField ? $"Password changed {Ui.Ago(saved.PasswordChangedUtc)}" : "";
        RefreshCodeTexts();
        IsDirty = false;
    }

    /// <summary>Picks up changes the vault made directly (attachments), keeping unsaved edits.</summary>
    private void RefreshFromVault()
    {
        var current = _owner.Vault.Entries.FirstOrDefault(x => x.Id == EntryId);
        if (current == null) return;
        _original.Attachments = [.. current.Attachments];
        _original.ModifiedUtc = current.ModifiedUtc;
        LoadAttachments(current);
        ModifiedText = $"Edited {Ui.Ago(current.ModifiedUtc)}";
    }

    // ───────────── commands ─────────────

    [RelayCommand]
    private async Task Unlock()
    {
        if (await _owner.VerifyMasterPasswordAsync($"\"{Title}\" is protected", "Enter your master password to view this item."))
            Unseal();
    }

    [RelayCommand]
    private async Task Save()
    {
        ApplyToVault();
        if (await _owner.PersistAsync()) UpdateIssue();
    }

    [RelayCommand]
    private void Revert()
    {
        Load(_original);
        if (CodesUnlocked) { _loading = true; BackupCodes = _originalCodes; _loading = false; }
        IsDirty = false;
    }

    [RelayCommand]
    private async Task Delete() => await _owner.DeleteEntryAsync(EntryId);

    [RelayCommand]
    private async Task CopyUsername() => await _owner.CopySecretAsync(Username, "Username");

    [RelayCommand]
    private async Task CopyPassword() => await _owner.CopySecretAsync(Password, IsWifi ? "Wi-Fi password" : "Password");

    [RelayCommand]
    private async Task CopyNetworkName() => await _owner.CopySecretAsync(NetworkName, "Network name");

    [RelayCommand]
    private async Task CopyCardNumber() => await _owner.CopySecretAsync(CardInfo.Digits(CardNumber), "Card number");

    [RelayCommand]
    private async Task CopyCardCode() => await _owner.CopySecretAsync(CardCode, "Security code");

    [RelayCommand]
    private async Task CopyCardPin() => await _owner.CopySecretAsync(CardPin, "PIN");

    [RelayCommand]
    private async Task CopyCodes() => await _owner.CopySecretAsync(BackupCodes, "Backup codes");

    [RelayCommand]
    private void ToggleReveal() => RevealPassword = !RevealPassword;

    [RelayCommand]
    private void ToggleRevealCard() => RevealCard = !RevealCard;

    [RelayCommand]
    private void OpenUrl()
    {
        var u = Url.Trim();
        if (u.Length == 0) return;
        if (!u.Contains("://")) u = "https://" + u;
        if (Uri.TryCreate(u, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
            _owner.Platform.OpenUrl(uri.AbsoluteUri);
    }

    [RelayCommand]
    private async Task UnlockCodes()
    {
        if (await _owner.EnsureRecoveryUnlockedAsync()) LoadCodes();
        RefreshCodeTexts();
    }

    [RelayCommand]
    private void LockCodes() => _owner.LockRecoveryNow();

    // ── Wi-Fi QR ──

    [RelayCommand]
    private void ToggleWifiQr()
    {
        if (WifiQr != null) { WifiQr = null; return; }
        if (NetworkName.Trim().Length == 0) { _owner.Status = "Add the network name first."; return; }
        string payload = WifiQr_Payload();
        using var gen = new QRCoder.QRCodeGenerator();
        using var data = gen.CreateQrCode(payload, QRCoder.QRCodeGenerator.ECCLevel.M);
        byte[] png = new QRCoder.PngByteQRCode(data).GetGraphic(8, [0, 0, 0], [255, 255, 255]);
        using var ms = new MemoryStream(png);
        WifiQr = new Bitmap(ms);
    }

    private string WifiQr_Payload() => Core.Tools.WifiQr.Payload(NetworkName.Trim(), Password, WifiSecurity.Value, HiddenNetwork);

    // ── custom fields ──

    [RelayCommand]
    private void AddField() => Fields.Add(new CustomFieldRowViewModel("", "", false) { FocusRequested = true });

    [RelayCommand]
    private void RemoveField(CustomFieldRowViewModel? row)
    {
        if (row != null) Fields.Remove(row);
    }

    [RelayCommand]
    private async Task CopyField(CustomFieldRowViewModel? row)
    {
        if (row != null) await _owner.CopySecretAsync(row.Value, row.Name.Length > 0 ? row.Name : "Field");
    }

    // ── attachments ──

    [RelayCommand]
    private async Task AddAttachment()
    {
        var path = await _owner.Platform.PickOpenFileAsync("Attach a file (it's encrypted inside the vault)", "All files", "*");
        if (path == null) return;
        var info = new FileInfo(path);
        if (info.Length > Vault.MaxAttachmentBytes)
        {
            await _owner.Main.AlertAsync("File too large", $"{info.Name} is {Ui.Size(info.Length)}. Attachments can be at most {Ui.Size(Vault.MaxAttachmentBytes)} each, because the whole vault is rewritten on every save.");
            return;
        }

        byte[] bytes = [];
        try
        {
            bytes = await File.ReadAllBytesAsync(path);
            _owner.Vault.AddAttachment(EntryId, info.Name, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            await _owner.Main.AlertAsync("Couldn't attach the file", ex.Message);
            return;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }

        RefreshFromVault();
        if (!await _owner.PersistAsync()) return;
        _owner.Status = $"{info.Name} attached and encrypted.";

        var choice = await _owner.Main.ShowAsync(new DialogViewModel("Delete the original?",
            $"{info.Name} is now encrypted inside your vault. The original file is still on your disk, unencrypted:\n{path}",
            [new DialogButton("keep", "Keep it"), new DialogButton("delete", "Overwrite and delete it", IsDanger: true)]));
        if (choice != "delete") return;
        try
        {
            await FileShredder.OverwriteAndDeleteAsync(path);
            _owner.Status = $"Original {info.Name} overwritten and deleted.";
        }
        catch (Exception ex) { await _owner.Main.AlertAsync("Couldn't delete the original", ex.Message); }
    }

    [RelayCommand]
    private async Task SaveAttachment(AttachmentRowViewModel? row)
    {
        if (row == null) return;
        string ext = Path.GetExtension(row.FileName).TrimStart('.');
        var path = await _owner.Platform.PickSaveFileAsync("Save a copy (this copy is NOT encrypted)", row.FileName, ext, "File");
        if (path == null) return;
        byte[] bytes = [];
        try
        {
            bytes = _owner.Vault.GetAttachment(row.Id);
            await File.WriteAllBytesAsync(path, bytes);
            _owner.Status = $"Saved {Path.GetFileName(path)}. That copy isn't encrypted; delete it when you're done.";
        }
        catch (Exception ex) { await _owner.Main.AlertAsync("Couldn't save the file", ex.Message); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    [RelayCommand]
    private async Task RemoveAttachment(AttachmentRowViewModel? row)
    {
        if (row == null) return;
        if (!await _owner.Main.ConfirmAsync("Remove attachment?", $"{row.FileName} will be deleted from the vault.", "Remove", danger: true)) return;
        _owner.Vault.RemoveAttachment(EntryId, row.Id);
        RefreshFromVault();
        await _owner.PersistAsync();
    }

    // ── history ──

    [RelayCommand]
    private void ToggleHistory() => HistoryOpen = !HistoryOpen;

    [RelayCommand]
    private void ToggleHistoryReveal() => HistoryRevealed = !HistoryRevealed;

    [RelayCommand]
    private async Task CopyOldPassword(HistoryRowViewModel? row)
    {
        if (row != null) await _owner.CopySecretAsync(row.Password, "Old password");
    }

    [RelayCommand]
    private void RestoreOldPassword(HistoryRowViewModel? row)
    {
        if (row == null) return;
        Password = row.Password;
        RevealPassword = true;
        _owner.Status = "Old password put back. Save to keep it; the current one will move into history.";
    }

    [RelayCommand]
    private async Task ClearHistory()
    {
        if (!await _owner.Main.ConfirmAsync("Clear password history?",
                $"Lockbox will forget the {History.Count} previous password(s) saved for \"{Title}\". The current password isn't affected.",
                "Clear history", danger: true))
            return;
        _owner.Vault.ClearPasswordHistory(EntryId);
        _original.PasswordHistory.Clear();
        LoadHistory(_original);
        await _owner.PersistAsync();
    }

    // ── generator ──

    [RelayCommand]
    private void ToggleGenerator()
    {
        GeneratorOpen = !GeneratorOpen;
        if (GeneratorOpen) RegeneratePreview();
    }

    [RelayCommand]
    private void RegeneratePreview()
    {
        try
        {
            if (GenPassphrase)
            {
                GenPreview = PasswordGenerator.GeneratePassphrase(GenWords, "-", capitalize: true, addDigit: true);
                GenInfo = $"{GenWords} words · about {PasswordGenerator.PassphraseEntropyBits(GenWords):0} bits";
            }
            else
            {
                var opts = new PasswordOptions(GenLength, GenLower, GenUpper, GenDigits, GenSymbols, GenNoAmbiguous);
                GenPreview = PasswordGenerator.Generate(opts);
                GenInfo = $"{GenLength} characters · about {PasswordGenerator.EntropyBits(opts):0} bits";
            }
        }
        catch (ArgumentException ex)
        {
            GenPreview = "";
            GenInfo = ex.Message;
        }
    }

    [RelayCommand]
    private void UseGenerated()
    {
        if (GenPreview.Length == 0) return;
        Password = GenPreview;
        RevealPassword = true;
        GeneratorOpen = false;
    }
}

public sealed record WifiSecurityOption(WifiSecurity Value, string Label)
{
    public static IReadOnlyList<WifiSecurityOption> All { get; } =
    [
        new(Core.Model.WifiSecurity.Wpa2, "WPA2"),
        new(Core.Model.WifiSecurity.Wpa3, "WPA3"),
        new(Core.Model.WifiSecurity.WpaWpa2, "WPA/WPA2"),
        new(Core.Model.WifiSecurity.Wep, "WEP (old)"),
        new(Core.Model.WifiSecurity.Open, "Open (no password)"),
    ];

    public static WifiSecurityOption For(WifiSecurity s) => All.FirstOrDefault(o => o.Value == s) ?? All[0];
    public override string ToString() => Label;
}

public partial class CustomFieldRowViewModel(string name, string value, bool hidden) : ObservableObject
{
    [ObservableProperty] private string _name = name;
    [ObservableProperty] private string _value = value;
    [ObservableProperty] private bool _hidden = hidden;
    [ObservableProperty] private bool _revealed;
    [ObservableProperty] private bool _focusRequested;

    /// <summary>Plain text unless the field is marked hidden and not currently revealed.</summary>
    public bool ShowPlain => !Hidden || Revealed;

    partial void OnHiddenChanged(bool value) => OnPropertyChanged(nameof(ShowPlain));
    partial void OnRevealedChanged(bool value) => OnPropertyChanged(nameof(ShowPlain));

    [RelayCommand] private void ToggleReveal() => Revealed = !Revealed;
}

public sealed class AttachmentRowViewModel(AttachmentRef a)
{
    public Guid Id { get; } = a.Id;
    public string FileName { get; } = a.FileName;
    public string Details { get; } = $"{Ui.Size(a.Size)} · added {a.AddedUtc.ToLocalTime():MMM d, yyyy}";
}

public partial class HistoryRowViewModel(PasswordHistoryItem item) : ObservableObject
{
    private const string Mask = "••••••••••••";

    public string Password { get; } = item.Password;
    public string WhenText { get; } = $"Replaced {Ui.Ago(item.ReplacedUtc)} · {item.ReplacedUtc.ToLocalTime():MMM d, yyyy}";

    [ObservableProperty] private bool _revealed;

    /// <summary>Fixed-length mask so hidden rows don't reveal how long the old password was.</summary>
    public string Display => Revealed ? Password : Mask;

    partial void OnRevealedChanged(bool value) => OnPropertyChanged(nameof(Display));
}
