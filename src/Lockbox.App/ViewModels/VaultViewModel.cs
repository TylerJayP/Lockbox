using System.Collections.ObjectModel;
using System.Security.Cryptography;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lockbox.App.Services;
using Lockbox.Core;
using Lockbox.Core.Model;
using Lockbox.Core.Tools;
using Microsoft.Win32;

namespace Lockbox.App.ViewModels;

public partial class VaultViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan SleepGap = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan RecoveryRelockAfter = TimeSpan.FromMinutes(3);

    private readonly MainWindowViewModel _main;
    private readonly byte[] _keyFileSecret;
    private readonly DispatcherTimer _timer;
    private DateTime _lastTickUtc = DateTime.UtcNow;
    private DateTime? _recoveryUnlockedAtUtc;
    private Dictionary<Guid, EntryIssue> _issues = [];
    private bool _locking;
    private bool _disposed;

    public VaultViewModel(MainWindowViewModel main, Vault vault, byte[] keyFileSecret)
    {
        _main = main;
        Vault = vault;
        _keyFileSecret = keyFileSecret;
        VaultName = Path.GetFileNameWithoutExtension(vault.FilePath);
        Activity.Touch();

        RefreshAudit();
        RebuildList();
        RefreshHome();   // open on Home: nothing private on screen until you pick an item

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTick;
        _timer.Start();

        if (OperatingSystem.IsWindows())
        {
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
        }
        RefreshAutoType();
    }

    public Vault Vault { get; }
    public IPlatform Platform => _main.Platform;
    public MainWindowViewModel Main => _main;
    public string VaultName { get; }
    public ObservableCollection<EntryItemViewModel> Items { get; } = [];

    public static IReadOnlyList<TypeFilter> TypeFilters { get; } =
    [
        new(null, "All"), new(ItemType.Login, "Logins"), new(ItemType.SecureNote, "Notes"),
        new(ItemType.Card, "Cards"), new(ItemType.WiFi, "Wi-Fi"),
    ];

    [ObservableProperty] private TypeFilter _typeFilter = TypeFilters[0];
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _onlyNeedsAttention;
    [ObservableProperty] private EntryItemViewModel? _selected;
    [ObservableProperty] private EntryEditorViewModel? _editor;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _countdown = "";
    [ObservableProperty] private int _attentionCount;
    [ObservableProperty] private int _totalCount;

    public bool IsEmpty => TotalCount == 0;
    public bool HasAttention => AttentionCount > 0;
    partial void OnAttentionCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasAttention));
        NotifyHealth();
    }
    public bool HasNoMatches => TotalCount > 0 && Items.Count == 0;

    partial void OnSearchChanged(string value) => RebuildList();
    partial void OnOnlyNeedsAttentionChanged(bool value) => RebuildList();
    partial void OnTypeFilterChanged(TypeFilter value) => RebuildList();

    /// <summary>Raised by Ctrl+F so the view can focus the search box.</summary>
    public event EventHandler? FocusSearchRequested;
    partial void OnTotalCountChanged(int value)
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasNoMatches));
        NotifyHealth();
    }

    private void NotifyHealth()
    {
        OnPropertyChanged(nameof(IsHealthy));
        OnPropertyChanged(nameof(HealthTitle));
        OnPropertyChanged(nameof(HealthDetail));
    }

    partial void OnSelectedChanged(EntryItemViewModel? oldValue, EntryItemViewModel? newValue)
    {
        if (newValue != null && Editor?.EntryId == newValue.Id) return; // list was rebuilt, same entry
        if (newValue == null && Editor != null && Vault.Entries.Any(e => e.Id == Editor.EntryId)) return; // filtered out, keep editing

        if (Editor is { IsDirty: true } previous)
        {
            previous.ApplyToVault();
            // Save after the selection change finishes, so the list rebuild doesn't re-enter this handler.
            Dispatcher.UIThread.Post(() => _ = PersistAsync());
        }
        var entry = newValue == null ? null : Vault.Entries.FirstOrDefault(e => e.Id == newValue.Id);
        Editor = entry == null ? null : new EntryEditorViewModel(this, entry);
    }

    // ───────────── list ─────────────

    public void RebuildList()
    {
        var keepId = Selected?.Id ?? Editor?.EntryId;
        string q = Search.Trim();

        bool Has(string? s) => s != null && s.Contains(q, StringComparison.OrdinalIgnoreCase);
        var rows = Vault.Entries
            .Where(e => TypeFilter.Type == null || e.Type == TypeFilter.Type)
            .Where(e => q.Length == 0
                        || Has(e.Title) || Has(e.Username) || Has(e.Url)
                        || Has(e.Wifi?.NetworkName) || Has(e.Card?.CardholderName)
                        || e.Tags.Any(Has) || e.Fields.Any(f => Has(f.Name)))
            .Select(e => new EntryItemViewModel(e, _issues.GetValueOrDefault(e.Id), _main.Settings.ShowFullUsernames))
            .Where(r => !OnlyNeedsAttention || r.NeedsAttention)
            .OrderBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        Items.Clear();
        foreach (var r in rows) Items.Add(r);
        TotalCount = Vault.Entries.Count;
        OnPropertyChanged(nameof(HasNoMatches));

        Selected = keepId is { } id ? Items.FirstOrDefault(i => i.Id == id) : null;
    }

    private void RefreshAudit()
    {
        _issues = VaultAudit.Analyze(Vault.Entries);
        AttentionCount = _issues.Values.Count(i => (i & VaultAudit.AttentionMask) != 0);
    }

    public EntryIssue IssuesFor(Guid id) => _issues.GetValueOrDefault(id);

    // ───────────── commands ─────────────

    [RelayCommand]
    private void NewEntry() => NewItem(nameof(ItemType.Login));

    [RelayCommand]
    private void NewItem(string? kind)
    {
        var type = Enum.TryParse<ItemType>(kind, out var t) ? t : ItemType.Login;
        if (Editor is { IsDirty: true } ed) { ed.ApplyToVault(); _ = PersistAsync(); }
        var e = Vault.AddEntry(type switch
        {
            ItemType.SecureNote => new Entry { Type = type, Title = "New note" },
            ItemType.Card => new Entry { Type = type, Title = "New card", Card = new CardData() },
            ItemType.WiFi => new Entry { Type = type, Title = "New Wi-Fi network", Wifi = new WifiData() },
            _ => new Entry { Type = ItemType.Login, Title = "New login" },
        });
        Search = "";
        OnlyNeedsAttention = false;
        if (TypeFilter.Type != null && TypeFilter.Type != type) TypeFilter = TypeFilters[0];
        RefreshAudit();
        RebuildList();
        Selected = Items.FirstOrDefault(i => i.Id == e.Id);
        Editor?.BeginNew();
    }

    [RelayCommand]
    private async Task Lock() => await LockAsync(null);

    [RelayCommand]
    private void OpenImport()
    {
        if (Editor is { IsDirty: true } ed) { ed.ApplyToVault(); _ = PersistAsync(); }
        _main.Navigate(new ImportViewModel(this));
    }

    [RelayCommand]
    private void OpenSettings()
    {
        if (Editor is { IsDirty: true } ed) { ed.ApplyToVault(); _ = PersistAsync(); }
        _main.Navigate(new SettingsViewModel(this));
    }

    [RelayCommand]
    private void ToggleAttention() => OnlyNeedsAttention = !OnlyNeedsAttention;

    [RelayCommand]
    private async Task CopyUsername(EntryItemViewModel? item) => await CopyPartAsync(item?.Id, secret: false);

    [RelayCommand]
    private async Task CopyPassword(EntryItemViewModel? item) => await CopyPartAsync(item?.Id, secret: true);

    // Keyboard shortcuts act on the selected item.
    [RelayCommand] private async Task CopySelectedPassword() => await CopyPartAsync(Selected?.Id ?? Editor?.EntryId, secret: true);
    [RelayCommand] private async Task CopySelectedUsername() => await CopyPartAsync(Selected?.Id ?? Editor?.EntryId, secret: false);
    [RelayCommand] private void FocusSearch() => FocusSearchRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] private void ClearSearch() { Search = ""; OnlyNeedsAttention = false; TypeFilter = TypeFilters[0]; }

    [RelayCommand]
    private void OpenSelectedUrl()
    {
        if (Editor is { IsLogin: true, IsSealed: false } ed) ed.OpenUrlCommand.Execute(null);
    }

    [RelayCommand]
    private async Task DeleteSelected()
    {
        if ((Selected?.Id ?? Editor?.EntryId) is { } id) await DeleteEntryAsync(id);
    }

    /// <summary>
    /// Copies an item's main secret (password, card number) or its "username" part
    /// (username, Wi-Fi network name, cardholder). Protected items ask for the master password first.
    /// </summary>
    private async Task CopyPartAsync(Guid? id, bool secret)
    {
        var e = id == null ? null : Vault.Entries.FirstOrDefault(x => x.Id == id);
        if (e == null) return;
        (string value, string what) = (e.Type, secret) switch
        {
            (ItemType.Card, true) => (e.Card?.Number ?? "", "Card number"),
            (ItemType.Card, false) => (e.Card?.CardholderName ?? "", "Cardholder name"),
            (ItemType.WiFi, true) => (e.Password, "Wi-Fi password"),
            (ItemType.WiFi, false) => (e.Wifi?.NetworkName ?? "", "Network name"),
            (ItemType.SecureNote, _) => ("", "A secure note"),
            (_, true) => (e.Password, "Password"),
            _ => (e.Username, "Username"),
        };
        if (e.Type == ItemType.SecureNote) { Status = "Secure notes have nothing to copy. Open the note instead."; return; }
        if (secret && !await EnsureUnsealedAsync(e)) return;
        await CopySecretAsync(value, what);
    }

    // ───────────── home ─────────────
    // The screen you land on after unlocking. It deliberately shows no names, usernames or sites,
    // so nothing private is on screen until you open an item.

    public bool IsHome => Editor == null;
    partial void OnEditorChanged(EntryEditorViewModel? value) => OnPropertyChanged(nameof(IsHome));

    [ObservableProperty] private int _loginCount;
    [ObservableProperty] private int _noteCount;
    [ObservableProperty] private int _cardCount;
    [ObservableProperty] private int _wifiCount;
    [ObservableProperty] private string _lastSavedText = "";
    [ObservableProperty] private string _backupText = "";
    [ObservableProperty] private string _autoLockText = "";

    public string Greeting => DateTime.Now.Hour switch
    {
        < 5 => "Good evening",
        < 12 => "Good morning",
        < 18 => "Good afternoon",
        _ => "Good evening",
    };

    public bool IsHealthy => AttentionCount == 0;
    public string HealthTitle => AttentionCount switch
    {
        0 => TotalCount == 0 ? "Nothing to check yet" : "Everything looks good",
        1 => "1 item needs attention",
        _ => $"{AttentionCount} items need attention",
    };
    public string HealthDetail => AttentionCount == 0
        ? (TotalCount == 0 ? "Add or import some passwords and Lockbox will keep an eye on them." : "No weak, reused, empty or very old passwords.")
        : "Weak, reused or very old passwords, or accounts that could use 2FA. The check-up shows which ones.";

    /// <summary>Recounts the Home screen's numbers. Cheap; called after saves and list changes.</summary>
    public void RefreshHome()
    {
        LoginCount = Vault.Entries.Count(e => e.Type == ItemType.Login);
        NoteCount = Vault.Entries.Count(e => e.Type == ItemType.SecureNote);
        CardCount = Vault.Entries.Count(e => e.Type == ItemType.Card);
        WifiCount = Vault.Entries.Count(e => e.Type == ItemType.WiFi);
        LastSavedText = Vault.LastSavedUtc == default ? "Not saved yet" : $"Saved {Ui.Ago(Vault.LastSavedUtc)}";
        int backups;
        try { backups = Vault.ListBackups().Count; } catch (IOException) { backups = 0; } catch (UnauthorizedAccessException) { backups = 0; }
        BackupText = backups switch
        {
            0 => "Automatic backups start with your next save",
            1 => "1 automatic backup kept next to the vault",
            _ => $"{backups} automatic backups kept next to the vault",
        };
        AutoLockText = $"Locks itself after {Vault.Settings.AutoLockMinutes} min without activity";
        NotifyHealth();
        OnPropertyChanged(nameof(Greeting));
    }

    /// <summary>Close the open item (saving any edits) and show Home, e.g. when someone walks up.</summary>
    [RelayCommand]
    private void GoHome()
    {
        if (Editor is { IsDirty: true } ed)
        {
            ed.ApplyToVault();
            Dispatcher.UIThread.Post(() => _ = PersistAsync());
        }
        Editor = null;
        Selected = null;
        RefreshHome();
    }

    /// <summary>Home tile: show just that kind of item in the list.</summary>
    [RelayCommand]
    private void ShowType(string? kind)
    {
        var type = Enum.TryParse<ItemType>(kind, out var t) ? t : (ItemType?)null;
        TypeFilter = TypeFilters.First(f => f.Type == type);
    }

    // ───────────── auto-type (Windows) ─────────────

    public const string AutoTypeHotkey = "Ctrl+Alt+A";
    private bool _hotkeyRegistered;
    private bool _autoTyping;

    /// <summary>Auto-type is turned on and this system supports it.</summary>
    public bool AutoTypeAvailable => !_disposed && _main.Settings.AutoTypeEnabled && Platform.AutoType != null;

    /// <summary>Whether the global hotkey is live (false if another app already owns it).</summary>
    public bool AutoTypeHotkeyActive => _hotkeyRegistered;

    /// <summary>Starts or stops listening for the hotkey to match the setting. Only listens while this vault is unlocked.</summary>
    public void RefreshAutoType()
    {
        if (Platform.AutoType is not { } host) return;
        if (AutoTypeAvailable)
        {
            if (!_hotkeyRegistered)
            {
                _hotkeyRegistered = host.RegisterHotkey(() => _ = AutoTypeFromHotkeyAsync());
                if (!_hotkeyRegistered)
                    Status = $"Auto-type: {AutoTypeHotkey} is already used by another app, so use the Auto-type button on an item instead.";
            }
        }
        else if (_hotkeyRegistered)
        {
            host.UnregisterHotkey();
            _hotkeyRegistered = false;
        }
        OnPropertyChanged(nameof(AutoTypeAvailable));
        OnPropertyChanged(nameof(AutoTypeHotkeyActive));
        Editor?.NotifyAutoTypeChanged();
    }

    /// <summary>Hotkey pressed in another app: type the login that matches its window, or let you pick one.</summary>
    public async Task AutoTypeFromHotkeyAsync()
    {
        if (_disposed || _autoTyping || Platform.AutoType is not { } host) return;
        nint target = host.ForegroundWindow();
        if (target == 0 || host.IsLockboxWindow(target)) return;

        string title = host.WindowTitle(target);
        var matches = AutoTypeMatcher.Find(Vault.Entries, title);
        if (matches.Count == 1)
        {
            await AutoTypeAsync(matches[0].Entry, target);
            return;
        }

        // None or several: show Lockbox so you can pick. Auto-type on the item then types into that window.
        host.BringLockboxToFront();
        ClearSearch();
        if (matches.Count > 1)
        {
            Search = matches[0].MatchedOn;
            Selected = Items.FirstOrDefault(i => i.Id == matches[0].Entry.Id) ?? Items.FirstOrDefault();
        }
        Status = matches.Count == 0
            ? $"No login matched \"{Shorten(title)}\". Pick one, then click Auto-type to type it there."
            : $"{matches.Count} logins match \"{Shorten(title)}\". Pick one, then click Auto-type.";
    }

    /// <summary>Auto-type button on an item: types it into the window you were in before Lockbox.</summary>
    public async Task AutoTypeEntryAsync(Guid id)
    {
        if (Platform.AutoType is not { } host) return;
        Editor?.ApplyToVault();
        var e = Vault.Entries.FirstOrDefault(x => x.Id == id);
        if (e == null || e.Type != ItemType.Login) return;
        nint target = host.PreviousWindow;
        if (target == 0)
        {
            Status = "Click into the sign-in form you want to fill, then come back and click Auto-type.";
            return;
        }
        await AutoTypeAsync(e, target);
    }

    private async Task AutoTypeAsync(Entry entry, nint target)
    {
        if (Platform.AutoType is not { } host || _autoTyping) return;
        if (entry.Password.Length == 0 && entry.Username.Length == 0) { Status = $"{entry.Title} has nothing to type."; return; }
        if (entry.Reprompt)
        {
            host.BringLockboxToFront();
            if (!await EnsureUnsealedAsync(entry)) return;
        }

        _autoTyping = true;
        try
        {
            string window = Shorten(host.WindowTitle(target));
            var result = await host.TypeAsync(target, AutoTypeSequence.For(entry));
            Status = result switch
            {
                AutoTypeResult.Typed => $"Typed {entry.Title} into \"{window}\".",
                AutoTypeResult.WindowGone => "That window has closed. Click into the sign-in form, then try again.",
                AutoTypeResult.FocusMoved => "Stopped typing because a different window became active. Nothing more was typed.",
                AutoTypeResult.KeysHeld => $"Let go of Ctrl and Alt after pressing {AutoTypeHotkey}, then try again.",
                _ => "",
            };
        }
        finally { _autoTyping = false; }
    }

    private static string Shorten(string s) => s.Length <= 60 ? s : s[..57] + "…";

    // ───────────── protected items ─────────────

    /// <summary>Asks for the master password (verified against the vault key). Returns false if cancelled.</summary>
    public async Task<bool> VerifyMasterPasswordAsync(string title, string message)
    {
        string? error = null;
        while (true)
        {
            var pw = await _main.PromptSecretAsync(title, message, "Master password", "Continue", error);
            if (pw == null) return false;
            var secret = CurrentKeyFileSecretCopy();
            try
            {
                if (await Task.Run(() => Vault.VerifyCredentials(pw, secret))) return true;
            }
            finally { CryptographicOperations.ZeroMemory(secret); }
            error = "That's not your master password.";
        }
    }

    /// <summary>For items marked "ask for master password", confirms it (once per time the item is opened).</summary>
    public async Task<bool> EnsureUnsealedAsync(Entry e)
    {
        if (!e.Reprompt) return true;
        if (Editor is { } ed && ed.EntryId == e.Id && !ed.IsSealed) return true;
        if (!await VerifyMasterPasswordAsync($"\"{e.Title}\" is protected", "Enter your master password to view or copy this item.")) return false;
        if (Editor is { } ed2 && ed2.EntryId == e.Id) ed2.Unseal();
        return true;
    }

    // ───────────── navigation helpers ─────────────

    [RelayCommand]
    private void OpenCheckup()
    {
        if (Editor is { IsDirty: true } ed) { ed.ApplyToVault(); _ = PersistAsync(); }
        RefreshAudit();
        _main.Navigate(new CheckupViewModel(this));
    }

    /// <summary>Returns to the vault and selects an item (used by the check-up page).</summary>
    public void ShowItem(Guid id)
    {
        _main.Navigate(this);
        Search = "";
        OnlyNeedsAttention = false;
        TypeFilter = TypeFilters[0];
        RebuildList();
        Selected = Items.FirstOrDefault(i => i.Id == id);
    }

    public IReadOnlyDictionary<Guid, EntryIssue> Issues => _issues;

    /// <summary>Remember the newest saved revision on this computer (for the rollback warning).</summary>
    public void RecordSaved()
    {
        _main.Settings.RecordRevision(Vault.VaultId, Vault.Revision, Vault.LastSavedUtc);
        RefreshHome();
    }

    public async Task CopySecretAsync(string value, string what)
    {
        if (string.IsNullOrEmpty(value)) { Status = $"{what} is empty."; return; }
        try
        {
            await Platform.Clipboard.CopyAsync(value, Vault.Settings.ClipboardClearSeconds);
            Status = $"{what} copied. The clipboard clears in {Vault.Settings.ClipboardClearSeconds} s.";
        }
        catch (Exception ex) { Status = "Couldn't copy: " + ex.Message; }
    }

    public async Task DeleteEntryAsync(Guid id)
    {
        var entry = Vault.Entries.FirstOrDefault(e => e.Id == id);
        if (entry == null) return;
        if (!await EnsureUnsealedAsync(entry)) return;
        var extras = new List<string>();
        if (entry.HasBackupCodes) extras.Add("its saved backup codes");
        if (entry.Attachments.Count > 0) extras.Add($"{entry.Attachments.Count} attached file(s)");
        string extra = extras.Count > 0 ? $"\n\nThis also deletes {string.Join(" and ", extras)}." : "";
        if (!await _main.ConfirmAsync("Delete this item?", $"\"{entry.Title}\" will be removed from the vault. A backup of the vault from before this session is kept in the backups folder.{extra}", "Delete", danger: true))
            return;

        int index = Items.ToList().FindIndex(i => i.Id == id);
        Editor = null;
        Vault.DeleteEntry(id);
        await PersistAsync();
        RefreshAudit();
        Selected = null;
        RebuildList();
        if (Items.Count > 0) Selected = Items[Math.Clamp(index, 0, Items.Count - 1)];
    }

    // ───────────── saving ─────────────

    public async Task<bool> PersistAsync()
    {
        try
        {
            Vault.Save();
            RecordSaved();
            Status = $"Saved at {DateTime.Now:t}.";
            RefreshAudit();
            RebuildList();
            return true;
        }
        catch (VaultConflictException ex)
        {
            bool overwrite = await _main.ConfirmAsync("The vault changed somewhere else",
                ex.Message + "\n\nIf you overwrite, changes made elsewhere since you unlocked will be lost (a backup is kept). " +
                "If you cancel, your edits stay here unsaved; lock and re-open the vault to see the other version.",
                "Overwrite with my version", danger: true, cancel: "Don't save");
            if (!overwrite) { Status = "Not saved. The vault file changed elsewhere."; return false; }
            try
            {
                Vault.Save(overwriteExternalChanges: true);
                RecordSaved();
                Status = $"Saved at {DateTime.Now:t}.";
                RefreshAudit();
                RebuildList();
                return true;
            }
            catch (Exception ex2) { await _main.AlertAsync("Couldn't save", ex2.Message); return false; }
        }
        catch (Exception ex)
        {
            await _main.AlertAsync("Couldn't save", ex.Message + "\n\nYour previous vault file hasn't been touched.");
            return false;
        }
    }

    // ───────────── sealed backup codes ─────────────

    /// <summary>Sets up or unlocks the backup-codes section. Returns false if the user cancels.</summary>
    public async Task<bool> EnsureRecoveryUnlockedAsync()
    {
        if (Vault.IsRecoveryUnlocked) { _recoveryUnlockedAtUtc = DateTime.UtcNow; return true; }

        if (!Vault.IsRecoveryConfigured)
        {
            var pass = await _main.PromptSecretAsync(
                "Give backup codes their own lock",
                "Backup codes are a way into your accounts that skips 2FA, so Lockbox seals them behind a second passphrase. " +
                "Even if someone learns your master password, they still can't read your codes.\n\n" +
                "Pick something different from your master password. There's no way to reset it.",
                "Recovery passphrase", "Set passphrase", withConfirmation: true,
                validate: (p, _) => PasswordStrength.ValidateMasterPassword(p));
            if (pass == null) return false;

            Status = "Setting up the backup-code lock…";
            await Task.Run(() => Vault.SetupRecovery(pass, Vault.KdfParameters));
            await PersistAsync();
            _recoveryUnlockedAtUtc = DateTime.UtcNow;
            Status = "Backup codes are now sealed with their own passphrase.";
            return true;
        }

        string? error = null;
        while (true)
        {
            var pass = await _main.PromptSecretAsync("Unlock backup codes",
                "Enter your recovery passphrase. Codes lock again automatically after 3 minutes.",
                "Recovery passphrase", "Unlock", error);
            if (pass == null) return false;
            Status = "Checking passphrase…";
            if (await Task.Run(() => Vault.UnlockRecovery(pass)))
            {
                _recoveryUnlockedAtUtc = DateTime.UtcNow;
                Status = "Backup codes unlocked.";
                if (Vault.HasUnsavedChanges) await PersistAsync();
                return true;
            }
            error = "That's not the recovery passphrase.";
            Status = "";
        }
    }

    public void LockRecoveryNow()
    {
        if (!Vault.IsRecoveryUnlocked) return;
        if (Editor is { CodesDirty: true } ed) { ed.ApplyToVault(); _ = PersistAsync(); }
        Vault.LockRecovery();
        _recoveryUnlockedAtUtc = null;
        Editor?.OnRecoveryLocked();
    }

    // ───────────── auto-lock ─────────────

    private void OnTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        // Windows reports sleep directly (PowerModeChanged). Elsewhere, a long gap between ticks means we were suspended.
        if (!OperatingSystem.IsWindows() && now - _lastTickUtc > SleepGap)
        {
            _ = LockAsync("Locked because the computer went to sleep.");
            return;
        }
        _lastTickUtc = now;
        if (IsHome && Vault.LastSavedUtc != default) LastSavedText = $"Saved {Ui.Ago(Vault.LastSavedUtc)}";

        var limit = TimeSpan.FromMinutes(Vault.Settings.AutoLockMinutes);
        var idle = now - Activity.LastInputUtc;
        if (idle >= limit)
        {
            _ = LockAsync($"Locked after {Vault.Settings.AutoLockMinutes} min without activity.");
            return;
        }

        if (_recoveryUnlockedAtUtc is { } at && now - at > RecoveryRelockAfter) LockRecoveryNow();

        var parts = new List<string>();
        if (Platform.Clipboard.ClearsAtUtc is { } clears && clears > now)
            parts.Add($"Clipboard clears in {(int)Math.Ceiling((clears - now).TotalSeconds)} s");
        var remaining = limit - idle;
        if (remaining < TimeSpan.FromSeconds(60))
            parts.Add($"Locking in {(int)Math.Ceiling(remaining.TotalSeconds)} s");
        Countdown = string.Join("   ·   ", parts);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect)
            Dispatcher.UIThread.Post(() => _ = LockAsync("Locked because Windows was locked."));
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
            Dispatcher.UIThread.Post(() => _ = LockAsync("Locked because the computer went to sleep."));
    }

    /// <summary>Saves pending edits, wipes the clipboard, destroys the keys and returns to the unlock screen.</summary>
    public async Task LockAsync(string? reason)
    {
        if (_locking || _disposed) return;
        _locking = true;
        string? problem = null;
        try
        {
            Editor?.ApplyToVault();
            if (Vault.HasUnsavedChanges)
            {
                try { Vault.Save(); RecordSaved(); }
                catch (Exception ex) { problem = "Your last changes couldn't be saved: " + ex.Message; }
            }
            await Platform.Clipboard.ClearIfOursAsync();
        }
        finally
        {
            Dispose();
            _main.OnLocked(problem == null ? reason : $"{reason} {problem}".Trim());
        }
    }

    /// <summary>Window is closing: save quietly, never prompt.</summary>
    public void SaveAndCloseOnExit()
    {
        try
        {
            Editor?.ApplyToVault();
            if (Vault.HasUnsavedChanges) { Vault.Save(); RecordSaved(); }
        }
        catch { /* nothing we can show at this point; the previous file is intact */ }
        Dispose();
    }

    // ───────────── settings support ─────────────

    public bool VerifyKeyFileSecret(ReadOnlySpan<byte> other) => CryptographicOperations.FixedTimeEquals(_keyFileSecret, other);

    public void ReplaceKeyFileSecret(ReadOnlySpan<byte> newSecret) => newSecret.CopyTo(_keyFileSecret);

    public byte[] CurrentKeyFileSecretCopy() => _keyFileSecret.ToArray();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
        if (OperatingSystem.IsWindows())
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        }
        if (_hotkeyRegistered)
        {
            Platform.AutoType?.UnregisterHotkey();
            _hotkeyRegistered = false;
        }
        CryptographicOperations.ZeroMemory(_keyFileSecret);
        Vault.Dispose();
    }
}
