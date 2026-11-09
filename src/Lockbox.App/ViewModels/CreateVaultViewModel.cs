using System.Security.Cryptography;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lockbox.Core;
using Lockbox.Core.Crypto;
using Lockbox.Core.Tools;

namespace Lockbox.App.ViewModels;

public partial class CreateVaultViewModel : ObservableObject
{
    private readonly MainWindowViewModel _main;
    private readonly byte[] _keySecret = KeyFile.Generate();

    public CreateVaultViewModel(MainWindowViewModel main)
    {
        _main = main;
        KeyPreview = KeyFile.FormatGroups(_keySecret) + "\nCHECK " + KeyFile.Checksum(_keySecret);
    }

    [ObservableProperty] private string _vaultPath = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _confirmPassword = "";
    [ObservableProperty] private bool _revealPassword;
    [ObservableProperty] private string _keyFilePath = "";
    [ObservableProperty] private bool _understandsNoRecovery;
    [ObservableProperty] private bool _emergencySheetSaved;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyText = "";
    [ObservableProperty] private string? _error;

    [ObservableProperty] private StrengthResult? _strength;
    [ObservableProperty] private double _strengthValue;
    [ObservableProperty] private IBrush _strengthBrush = Brushes.Transparent;
    [ObservableProperty] private string _strengthText = "";

    public string KeyPreview { get; }

    public string? SameFolderWarning
    {
        get
        {
            if (VaultPath.Length == 0 || KeyFilePath.Length == 0) return null;
            string? a = Path.GetDirectoryName(Path.GetFullPath(VaultPath)), b = Path.GetDirectoryName(Path.GetFullPath(KeyFilePath));
            if (string.IsNullOrEmpty(a) || !string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return null;
            return "The key file is in the same folder as the vault. Anyone who copies that folder gets both. A USB stick or a different drive is much better.";
        }
    }

    public bool HasSameFolderWarning => SameFolderWarning != null;

    partial void OnVaultPathChanged(string value) { OnPropertyChanged(nameof(SameFolderWarning)); OnPropertyChanged(nameof(HasSameFolderWarning)); }
    partial void OnKeyFilePathChanged(string value) { OnPropertyChanged(nameof(SameFolderWarning)); OnPropertyChanged(nameof(HasSameFolderWarning)); }

    partial void OnPasswordChanged(string value)
    {
        Error = null;
        if (value.Length == 0) { Strength = null; StrengthValue = 0; StrengthText = ""; StrengthBrush = Brushes.Transparent; return; }
        var s = PasswordStrength.Evaluate(value, [Path.GetFileNameWithoutExtension(VaultPath)]);
        Strength = s;
        StrengthValue = (int)s.Level + 1;
        StrengthBrush = Ui.StrengthBrush(s.Level);
        StrengthText = s.Level >= PasswordStrength.MasterMinimum
            ? $"{s.Label}. Estimated time to crack offline: {s.CrackTime} (before Argon2 slows it down further)."
            : $"{s.Label}. {s.Feedback}".Trim();
    }

    [RelayCommand]
    private void GeneratePassphrase()
    {
        var p = PasswordGenerator.GeneratePassphrase(6);
        Password = p;
        ConfirmPassword = p;
        RevealPassword = true;
    }

    [RelayCommand]
    private async Task BrowseVault()
    {
        var path = await _main.Platform.PickSaveFileAsync("Where should the vault live?", "MyVault" + Vault.FileExtension, Vault.FileExtension.TrimStart('.'), "Lockbox vault");
        if (path != null) VaultPath = path;
    }

    [RelayCommand]
    private async Task BrowseKeyFile()
    {
        var path = await _main.Platform.PickSaveFileAsync("Where should the key file go? (ideally a USB stick)", "MyVault" + KeyFile.DefaultExtension, KeyFile.DefaultExtension.TrimStart('.'), "Lockbox key file");
        if (path != null) KeyFilePath = path;
    }

    [RelayCommand]
    private async Task SaveEmergencySheet()
    {
        var path = await _main.Platform.PickSaveFileAsync("Save the emergency sheet (print it, then delete the file)", "Lockbox emergency sheet.txt", "txt", "Text file");
        if (path == null) return;
        string vaultName = VaultPath.Length > 0 ? Path.GetFileName(VaultPath) : "(not chosen yet)";
        string sheet =
            "LOCKBOX EMERGENCY SHEET\n" +
            "=======================\n\n" +
            $"Vault file: {vaultName}\n" +
            $"Created:    {DateTime.Now:MMMM d, yyyy}\n\n" +
            "If you lose your key file, unlock with the key below instead:\n" +
            "on the unlock screen, choose \"Type the key from my emergency sheet\".\n\n" +
            "KEY\n" +
            "   " + KeyFile.FormatGroups(_keySecret, "   ") + "\n\n" +
            $"CHECK  {KeyFile.Checksum(_keySecret)}\n\n" +
            "Your master password is NOT on this sheet. If you write it down, keep that paper\n" +
            "somewhere different from this one. Anyone with both can open your vault.\n\n" +
            "Print this page, store it somewhere safe and offline, then delete this file.\n";
        try
        {
            await File.WriteAllTextAsync(path, sheet);
            EmergencySheetSaved = true;
        }
        catch (Exception ex) { Error = "Couldn't save the sheet: " + ex.Message; }
    }

    [RelayCommand]
    private void Back()
    {
        CryptographicOperations.ZeroMemory(_keySecret);
        _main.Navigate(new UnlockViewModel(_main));
    }

    [RelayCommand]
    private async Task Create()
    {
        if (IsBusy) return;
        Error = null;

        if (VaultPath.Length == 0) { Error = "Choose where to save the vault."; return; }
        if (File.Exists(VaultPath)) { Error = "A file already exists there. Pick a new name."; return; }
        if (PasswordStrength.ValidateMasterPassword(Password, [Path.GetFileNameWithoutExtension(VaultPath)]) is { } pwErr) { Error = pwErr; return; }
        if (Password != ConfirmPassword) { Error = "The two passwords don't match."; return; }
        if (KeyFilePath.Length == 0) { Error = "Choose where to save the key file."; return; }
        if (File.Exists(KeyFilePath)) { Error = "A file already exists where the key file would go. Pick a new name."; return; }
        if (!UnderstandsNoRecovery) { Error = "Tick the box confirming you understand there's no password reset."; return; }

        IsBusy = true;
        BusyText = "Tuning encryption strength for this computer…";
        string vaultPath = VaultPath, keyPath = KeyFilePath, password = Password;
        bool keySaved = false;
        try
        {
            var kdf = await Task.Run(() => Kdf.Calibrate(TimeSpan.FromSeconds(1)));
            BusyText = "Creating your vault…";
            var vault = await Task.Run(() =>
            {
                KeyFile.Save(keyPath, _keySecret);
                keySaved = true;
                return Vault.Create(vaultPath, password, _keySecret, kdf);
            });

            _main.Settings.LastVaultPath = vaultPath;
            _main.Settings.LastKeyFilePath = keyPath;
            _main.Settings.Save();
            _main.Settings.RecordRevision(vault.VaultId, vault.Revision, vault.LastSavedUtc);

            var secretCopy = _keySecret.ToArray();
            CryptographicOperations.ZeroMemory(_keySecret);
            Password = ConfirmPassword = "";
            var vm = new VaultViewModel(_main, vault, secretCopy) { Status = "Vault created. Add a login or import your old text file." };
            _main.OnUnlocked(vm);
        }
        catch (Exception ex)
        {
            if (keySaved) { try { File.Delete(keyPath); } catch { } }
            Error = "Couldn't create the vault: " + ex.Message;
        }
        finally { IsBusy = false; }
    }
}
