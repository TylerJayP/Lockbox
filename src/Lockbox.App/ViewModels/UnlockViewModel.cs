using System.Collections.ObjectModel;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lockbox.Core;
using Lockbox.Core.Crypto;

namespace Lockbox.App.ViewModels;

public partial class UnlockViewModel : ObservableObject
{
    private readonly MainWindowViewModel _main;

    public UnlockViewModel(MainWindowViewModel main, string? lockedReason = null)
    {
        _main = main;
        LockedReason = lockedReason;
        VaultPath = main.Settings.LastVaultPath ?? "";
        RememberKeyFileLocation = main.Settings.RememberKeyFileLocation;
        KeyFilePath = main.Settings.RememberKeyFileLocation ? main.Settings.LastKeyFilePath ?? "" : "";
        foreach (var r in main.Settings.GetRecentVaults()) Recent.Add(new RecentVaultItem(r));
        MarkCurrent();
    }

    /// <summary>Vaults opened on this computer before. Shown as one-click choices when there's more than one.</summary>
    public ObservableCollection<RecentVaultItem> Recent { get; } = [];
    public bool HasRecent => Recent.Count > 1;

    /// <summary>Asks the view to put the cursor in the password box.</summary>
    public event EventHandler? FocusPasswordRequested;

    partial void OnVaultPathChanged(string value) => MarkCurrent();

    private void MarkCurrent()
    {
        foreach (var r in Recent) r.IsCurrent = string.Equals(r.Path, VaultPath, StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    private void PickRecent(RecentVaultItem item)
    {
        VaultPath = item.Path;
        TypeKeyManually = false;
        KeyFilePath = item.KeyFilePath ?? "";
        RememberKeyFileLocation = item.KeyFilePath != null;
        Password = "";
        Error = item.Exists ? null : "That vault isn't where it used to be. Browse to it, or remove it from the list.";
        FocusPasswordRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ForgetRecent(RecentVaultItem item)
    {
        _main.Settings.ForgetVault(item.Path);
        Recent.Remove(item);
        OnPropertyChanged(nameof(HasRecent));
    }

    public string? LockedReason { get; }
    public bool HasLockedReason => !string.IsNullOrEmpty(LockedReason);

    [ObservableProperty] private string _vaultPath = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _keyFilePath = "";
    [ObservableProperty] private bool _rememberKeyFileLocation;
    [ObservableProperty] private bool _typeKeyManually;
    [ObservableProperty] private string _manualKey = "";
    [ObservableProperty] private string _manualCheck = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _error;

    partial void OnPasswordChanged(string value) => Error = null;

    [RelayCommand]
    private async Task BrowseVault()
    {
        var path = await _main.Platform.PickOpenFileAsync("Open your vault", "Lockbox vault", "*" + Vault.FileExtension);
        if (path != null) VaultPath = path;
    }

    [RelayCommand]
    private async Task BrowseKeyFile()
    {
        var path = await _main.Platform.PickOpenFileAsync("Select your key file", "Lockbox key file", "*" + KeyFile.DefaultExtension, "*.txt");
        if (path != null) KeyFilePath = path;
    }

    [RelayCommand]
    private void CreateNew() => _main.Navigate(new CreateVaultViewModel(_main));

    [RelayCommand]
    private async Task Unlock()
    {
        if (IsBusy) return;
        Error = null;

        if (string.IsNullOrWhiteSpace(VaultPath) || !File.Exists(VaultPath)) { Error = "Choose your vault file (.lbx)."; return; }
        if (Password.Length == 0) { Error = "Enter your master password."; return; }

        byte[] secret;
        try
        {
            secret = TypeKeyManually ? KeyFile.ParseTyped(ManualKey, ManualCheck)
                : string.IsNullOrWhiteSpace(KeyFilePath) ? throw new KeyFileException("Choose your key file, or type the key from your emergency sheet.")
                : KeyFile.Load(KeyFilePath);
        }
        catch (KeyFileException ex) { Error = ex.Message; return; }
        catch (IOException ex) { Error = "Couldn't read the key file: " + ex.Message; return; }

        IsBusy = true;
        string path = VaultPath, password = Password;
        try
        {
            var vault = await Task.Run(() => Vault.Open(path, password, secret));

            // Rollback check: is this file older than the newest version of this vault this PC has seen?
            if (_main.Settings.Known(vault.VaultId) is { } known && vault.Revision < known.Revision)
            {
                bool openAnyway = await _main.ConfirmAsync("This is an older copy of your vault",
                    $"This computer has seen a newer version of this vault (saved {known.SavedUtc.ToLocalTime():MMM d, yyyy h:mm tt}), " +
                    $"but this file was last saved {vault.LastSavedUtc.ToLocalTime():MMM d, yyyy h:mm tt}.\n\n" +
                    "Changes made since then aren't in it. This can happen when a sync app (OneDrive, Google Drive) restores an old copy, " +
                    "or if someone swapped the file.\n\n" +
                    "If you're opening a backup on purpose, that's fine. Otherwise, close it and look for the newer file " +
                    "(check OneDrive's version history and the backups folder).",
                    "Open this copy anyway", danger: true, cancel: "Close it");
                if (!openAnyway)
                {
                    vault.Dispose();
                    CryptographicOperations.ZeroMemory(secret);
                    return;
                }
                vault.AdoptRevisionAtLeast(known.Revision + 1);
            }
            _main.Settings.RecordRevision(vault.VaultId, vault.Revision, vault.LastSavedUtc);

            _main.Settings.RememberVault(path, TypeKeyManually ? null : KeyFilePath, RememberKeyFileLocation && !TypeKeyManually);

            Password = "";
            _main.OnUnlocked(new VaultViewModel(_main, vault, secret));
        }
        catch (WrongCredentialsException ex) { Error = ex.Message; CryptographicOperations.ZeroMemory(secret); }
        catch (VaultFormatException ex) { Error = ex.Message; CryptographicOperations.ZeroMemory(secret); }
        catch (Exception ex) { Error = "Couldn't open the vault: " + ex.Message; CryptographicOperations.ZeroMemory(secret); }
        finally { IsBusy = false; }
    }
}

public partial class RecentVaultItem(Services.RecentVault vault) : ObservableObject
{
    public string Path { get; } = vault.Path;
    public string? KeyFilePath { get; } = vault.KeyFilePath;
    // Split on both separators so a Windows path reads the same everywhere.
    private static readonly char[] Separators = ['\\', '/'];
    private readonly string[] _parts = vault.Path.Split(Separators, StringSplitOptions.RemoveEmptyEntries);

    public string Name => _parts.Length > 0 ? System.IO.Path.GetFileNameWithoutExtension(_parts[^1]) : Path;
    public string Folder => _parts.Length > 1 ? _parts[^2] : "";
    public bool Exists => File.Exists(Path);
    public string Tip => Path + (KeyFilePath != null ? "\nKey file: " + KeyFilePath : "");

    [ObservableProperty] private bool _isCurrent;
}
