using System.Security.Cryptography;
using System.Text;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lockbox.Core;
using Lockbox.Core.Crypto;
using Lockbox.Core.Export;
using Lockbox.Core.Tools;

namespace Lockbox.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly VaultViewModel _owner;

    public SettingsViewModel(VaultViewModel owner)
    {
        _owner = owner;
        var s = owner.Vault.Settings;
        AutoLockMinutes = s.AutoLockMinutes;
        ClipboardClearSeconds = s.ClipboardClearSeconds;
        LockOnMinimize = s.LockOnMinimize;
        PasswordHistoryLimit = s.PasswordHistoryLimit;
        _hideFromScreenCapture = owner.Main.Settings.HideFromScreenCapture;
        _checkForUpdates = owner.Main.Settings.CheckForUpdates;
        RefreshInfo();
    }

    // ── behaviour ──
    [ObservableProperty] private decimal? _autoLockMinutes;
    [ObservableProperty] private decimal? _clipboardClearSeconds;
    [ObservableProperty] private bool _lockOnMinimize;
    [ObservableProperty] private decimal? _passwordHistoryLimit;
    [ObservableProperty] private string? _behaviourStatus;

    // ── updates ──
    [ObservableProperty] private bool _checkForUpdates;
    [ObservableProperty] private string? _updateStatus;
    [ObservableProperty] private bool _checkingUpdates;
    public string VersionText => $"You're running Lockbox {Services.UpdateService.CurrentVersion}.";
    public bool UpdatesConfigured => Services.UpdateService.IsConfigured;

    partial void OnCheckForUpdatesChanged(bool value)
    {
        _owner.Main.Settings.CheckForUpdates = value;
        _owner.Main.Settings.Save();
    }

    [RelayCommand]
    private async Task CheckUpdatesNow()
    {
        CheckingUpdates = true;
        UpdateStatus = "Checking…";
        try { UpdateStatus = await _owner.Main.CheckForUpdatesAsync(userInitiated: true); }
        finally { CheckingUpdates = false; }
    }

    [RelayCommand]
    private void OpenReleases() => _owner.Platform.OpenUrl(Services.UpdateTrust.RepoUrl + "/releases/latest");

    // ── this computer ──
    [ObservableProperty] private bool _hideFromScreenCapture;
    [ObservableProperty] private string? _captureStatus;

    partial void OnHideFromScreenCaptureChanged(bool value)
    {
        _owner.Main.Settings.HideFromScreenCapture = value;
        _owner.Main.Settings.Save();
        bool applied = _owner.Platform.SetCaptureProtection(value);
        CaptureStatus = !OperatingSystem.IsWindows() ? "Only available on Windows."
            : value && !applied ? "Windows didn't accept this (needs Windows 10 version 2004 or newer)."
            : value ? "On: screenshots, recordings and screen shares show Lockbox as blank." : "Off: Lockbox shows up in screenshots and screen shares.";
    }

    public static IReadOnlyList<(string Keys, string Action)> ShortcutList { get; } =
    [
        ("Ctrl+F", "Search"),
        ("Ctrl+N", "New login"),
        ("Enter or Ctrl+C (in the list)", "Copy password / card number"),
        ("Ctrl+Shift+C", "Copy password from anywhere"),
        ("Ctrl+B", "Copy username / network name"),
        ("Ctrl+U", "Open website"),
        ("Ctrl+S", "Save changes"),
        ("Delete (in the list)", "Delete item"),
        ("Esc (in search)", "Clear search and filters"),
        ("Ctrl+L", "Lock now"),
    ];
    public string ShortcutsText { get; } = string.Join("\n", ShortcutList.Select(s => $"{s.Keys,-32}{s.Action}"));
    public IReadOnlyList<ShortcutRow> Shortcuts { get; } = ShortcutList.Select(s => new ShortcutRow(s.Keys, s.Action)).ToList();

    // ── info ──
    [ObservableProperty] private string _vaultPath = "";
    [ObservableProperty] private string _kdfText = "";
    [ObservableProperty] private string _backupsText = "";
    [ObservableProperty] private string _recoveryText = "";

    // ── change master password ──
    [ObservableProperty] private string _currentPassword = "";
    [ObservableProperty] private string _newPassword = "";
    [ObservableProperty] private string _confirmNewPassword = "";
    [ObservableProperty] private bool _newKeyFile;
    [ObservableProperty] private bool _revealNew;
    [ObservableProperty] private double _strengthValue;
    [ObservableProperty] private IBrush _strengthBrush = Brushes.Transparent;
    [ObservableProperty] private string _strengthText = "";
    [ObservableProperty] private string? _changeError;
    [ObservableProperty] private string? _changeStatus;
    [ObservableProperty] private bool _isBusy;

    // ── export ──
    [ObservableProperty] private string? _exportStatus;
    [ObservableProperty] private bool _exportFailed;

    public bool RecoveryConfigured => _owner.Vault.IsRecoveryConfigured;

    private void RefreshInfo()
    {
        var v = _owner.Vault;
        VaultPath = v.FilePath;
        KdfText = $"Argon2id · {v.KdfParameters.MemoryKiB / 1024} MiB memory · {v.KdfParameters.Passes} passes (tuned to about 1 second per attempt on the computer that created it)";
        int n = v.ListBackups().Count;
        BackupsText = n == 0 ? "No backups yet. One is made the first time you save after unlocking."
            : $"{n} encrypted backup{(n == 1 ? "" : "s")} (newest {v.ListBackups()[0].LastWriteTime:g}). Up to {Core.Vault.BackupsToKeep} are kept.";
        RecoveryText = v.IsRecoveryConfigured
            ? "Backup codes are sealed with a separate recovery passphrase."
            : "Not set up yet. You'll be asked to choose a recovery passphrase the first time you add backup codes.";
        OnPropertyChanged(nameof(RecoveryConfigured));
    }

    partial void OnNewPasswordChanged(string value)
    {
        ChangeError = null;
        if (value.Length == 0) { StrengthValue = 0; StrengthText = ""; StrengthBrush = Brushes.Transparent; return; }
        var s = PasswordStrength.Evaluate(value);
        StrengthValue = (int)s.Level + 1;
        StrengthBrush = Ui.StrengthBrush(s.Level);
        StrengthText = s.Level >= PasswordStrength.MasterMinimum ? s.Label : $"{s.Label}. {s.Feedback}".Trim();
    }

    [RelayCommand]
    private void Back() => _owner.Main.Navigate(_owner);

    [RelayCommand]
    private async Task SaveBehaviour()
    {
        BehaviourStatus = null;
        int newLimit = Math.Clamp((int)(PasswordHistoryLimit ?? 10), 0, 50);
        int wouldDrop = _owner.Vault.Entries.Sum(e => Math.Max(0, e.PasswordHistory.Count - newLimit));
        if (wouldDrop > 0 && !await _owner.Main.ConfirmAsync("Delete some old passwords?",
                newLimit == 0
                    ? $"Turning history off deletes all {wouldDrop} saved old password(s)."
                    : $"Keeping only {newLimit} per login deletes {wouldDrop} older saved password(s).",
                "Delete them", danger: true))
        {
            PasswordHistoryLimit = _owner.Vault.Settings.PasswordHistoryLimit;
            return;
        }

        _owner.Vault.UpdateSettings(s =>
        {
            s.AutoLockMinutes = (int)(AutoLockMinutes ?? 5);
            s.ClipboardClearSeconds = (int)(ClipboardClearSeconds ?? 20);
            s.LockOnMinimize = LockOnMinimize;
            s.PasswordHistoryLimit = newLimit;
        });
        if (await _owner.PersistAsync())
        {
            AutoLockMinutes = _owner.Vault.Settings.AutoLockMinutes;
            ClipboardClearSeconds = _owner.Vault.Settings.ClipboardClearSeconds;
            PasswordHistoryLimit = _owner.Vault.Settings.PasswordHistoryLimit;
            BehaviourStatus = "Saved.";
        }
    }

    [RelayCommand]
    private void GeneratePassphrase()
    {
        NewPassword = ConfirmNewPassword = PasswordGenerator.GeneratePassphrase(6);
        RevealNew = true;
    }

    [RelayCommand]
    private async Task ChangeMasterPassword()
    {
        if (IsBusy) return;
        ChangeError = null;
        ChangeStatus = null;

        if (CurrentPassword.Length == 0) { ChangeError = "Enter your current master password."; return; }
        if (PasswordStrength.ValidateMasterPassword(NewPassword) is { } err) { ChangeError = err; return; }
        if (NewPassword != ConfirmNewPassword) { ChangeError = "The new passwords don't match."; return; }

        string? newKeyPath = null;
        if (NewKeyFile)
        {
            newKeyPath = await _owner.Platform.PickSaveFileAsync("Save the NEW key file (ideally on a USB stick)",
                Path.GetFileNameWithoutExtension(_owner.Vault.FilePath) + "-new" + KeyFile.DefaultExtension, KeyFile.DefaultExtension.TrimStart('.'), "Lockbox key file");
            if (newKeyPath == null) return;
            if (File.Exists(newKeyPath)) { ChangeError = "A file already exists there. Choose a new name."; return; }
        }

        IsBusy = true;
        ChangeStatus = "Checking your current password…";
        string current = CurrentPassword, next = NewPassword;
        byte[] currentSecret = _owner.CurrentKeyFileSecretCopy();
        byte[] nextSecret = NewKeyFile ? KeyFile.Generate() : currentSecret.ToArray();
        bool keySaved = false;
        try
        {
            if (!await Task.Run(() => _owner.Vault.VerifyCredentials(current, currentSecret)))
            {
                ChangeError = "Your current master password is wrong.";
                ChangeStatus = null;
                return;
            }

            ChangeStatus = "Re-encrypting the vault…";
            await Task.Run(() =>
            {
                var kdf = Kdf.Calibrate(TimeSpan.FromSeconds(1));
                if (newKeyPath != null) { KeyFile.Save(newKeyPath, nextSecret); keySaved = true; }
                _owner.Vault.ChangeCredentials(next, nextSecret, kdf);
            });
            _owner.ReplaceKeyFileSecret(nextSecret);
            _owner.RecordSaved();
            if (newKeyPath != null)
            {
                _owner.Main.Settings.LastKeyFilePath = newKeyPath;
                _owner.Main.Settings.Save();
            }

            CurrentPassword = NewPassword = ConfirmNewPassword = "";
            NewKeyFile = false;
            ChangeStatus = newKeyPath != null
                ? $"Done. Use your new password and the new key file ({Path.GetFileName(newKeyPath)}) from now on. The old key file no longer works; delete it."
                : "Done. Use your new password from now on.";
            RefreshInfo();

            int backups = _owner.Vault.ListBackups().Count;
            if (backups > 0 && await _owner.Main.ConfirmAsync("Delete old backups?",
                    $"The {backups} backup file(s) in the backups folder still open with your OLD password and key file. " +
                    "If you changed them because they may have been exposed, delete the backups.",
                    "Delete old backups", danger: true, cancel: "Keep them"))
            {
                _owner.Vault.DeleteBackups();
                RefreshInfo();
            }
        }
        catch (Exception ex)
        {
            if (keySaved && newKeyPath != null) { try { File.Delete(newKeyPath); } catch { } }
            ChangeError = "Couldn't change the password: " + ex.Message + " Nothing was changed.";
            ChangeStatus = null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(currentSecret);
            CryptographicOperations.ZeroMemory(nextSecret);
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ChangeRecoveryPassphrase()
    {
        if (!await _owner.EnsureRecoveryUnlockedAsync()) return;
        var pass = await _owner.Main.PromptSecretAsync("New recovery passphrase",
            "Choose a new passphrase for your backup codes. Keep it different from your master password.",
            "New recovery passphrase", "Change", withConfirmation: true,
            validate: (p, _) => PasswordStrength.ValidateMasterPassword(p));
        if (pass == null) return;
        var kdf = _owner.Vault.KdfParameters;
        await Task.Run(() => _owner.Vault.ChangeRecoveryPassphrase(pass, kdf));
        if (await _owner.PersistAsync()) ChangeStatus = "Recovery passphrase changed.";
        RefreshInfo();
    }

    [RelayCommand]
    private async Task SaveEncryptedCopy()
    {
        ExportStatus = null;
        var v = _owner.Vault;
        var path = await _owner.Platform.PickSaveFileAsync("Save an encrypted copy of the vault",
            $"{Path.GetFileNameWithoutExtension(v.FilePath)}-copy-{DateTime.Now:yyyy-MM-dd}{Vault.FileExtension}",
            Vault.FileExtension.TrimStart('.'), "Lockbox vault");
        if (path == null) return;
        try
        {
            if (v.HasUnsavedChanges && !await _owner.PersistAsync()) return;
            await Task.Run(() => v.SaveEncryptedCopy(path));
            _owner.RecordSaved();
            SetExportStatus($"Encrypted copy saved to {path}. It opens with the same master password and key file.", failed: false);
        }
        catch (Exception ex) { SetExportStatus("Couldn't save the copy: " + ex.Message, failed: true); }
    }

    [RelayCommand]
    private async Task ExportCsv()
    {
        ExportStatus = null;
        var v = _owner.Vault;
        if (v.Entries.Count == 0) { SetExportStatus("There's nothing to export yet.", failed: true); return; }

        // 1. Prove it's really you: an unlocked, unattended computer shouldn't be able to dump everything.
        if (!await _owner.VerifyMasterPasswordAsync("Export to a readable file",
                "This creates an UNENCRYPTED file with every username and password in your vault. Anyone who opens that file can read them all.\n\n" +
                "Enter your master password to continue."))
            return;

        // 2. Backup codes bypass 2FA, so they stay out unless explicitly wanted.
        bool includeCodes = false;
        if (v.Entries.Any(e => e.HasBackupCodes))
        {
            var choice = await _owner.Main.ShowAsync(new DialogViewModel("Include backup codes?",
                "Some logins have backup codes. A backup code gets into an account without the 2FA step, so leaving them out of a readable file is safer. " +
                "If you include them, they go into each login's notes.",
                [new DialogButton("yes", "Include them"), new DialogButton("no", "Leave them out", IsPrimary: true)]));
            if (choice == null) return;
            if (choice == "yes")
            {
                if (!await _owner.EnsureRecoveryUnlockedAsync()) return;
                includeCodes = true;
            }
        }

        // 3. Where to put it.
        var path = await _owner.Platform.PickSaveFileAsync("Save the export (delete it when you're done)",
            $"lockbox-export-{DateTime.Now:yyyy-MM-dd}.csv", "csv", "CSV file");
        if (path == null) return;

        // The codes section may have re-locked while the file dialog was open.
        if (includeCodes && !v.IsRecoveryUnlocked && !await _owner.EnsureRecoveryUnlockedAsync()) return;

        try
        {
            string csv = CsvExport.ToBitwardenCsv(v.Entries,
                includeCodes ? e => e.HasBackupCodes ? v.GetBackupCodes(e.Id) : null : null);
            await File.WriteAllTextAsync(path, csv, new UTF8Encoding(false));
            SetExportStatus(
                $"Exported {v.Entries.Count} logins to {Path.GetFileName(path)}{(includeCodes ? " (with backup codes)" : "")}. " +
                "It isn't encrypted: delete it as soon as you've used it, and empty the Recycle Bin. " +
                "Opening it in Excel and saving can mangle passwords that start with = + - or @, so import the original file.",
                failed: false);
        }
        catch (Exception ex) { SetExportStatus("Couldn't write the export: " + ex.Message, failed: true); }
    }

    private void SetExportStatus(string text, bool failed)
    {
        ExportFailed = failed;
        ExportStatus = text;
    }

    [RelayCommand]
    private void OpenBackupsFolder()
    {
        Directory.CreateDirectory(_owner.Vault.BackupDirectory);
        _owner.Platform.OpenFolder(_owner.Vault.BackupDirectory);
    }

    [RelayCommand]
    private async Task DeleteBackups()
    {
        if (!await _owner.Main.ConfirmAsync("Delete all backups?",
                "This permanently deletes every backup copy of the vault. The vault itself isn't affected.", "Delete backups", danger: true))
            return;
        int n = _owner.Vault.DeleteBackups();
        BackupsText = $"Deleted {n} backup(s).";
    }
}

public sealed record ShortcutRow(string Keys, string Action);
