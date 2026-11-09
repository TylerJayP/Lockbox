using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lockbox.Core.Import;
using Lockbox.Core.Model;

namespace Lockbox.App.ViewModels;

public partial class ImportViewModel : ObservableObject
{
    private readonly VaultViewModel _owner;
    private string? _sourcePath;

    public ImportViewModel(VaultViewModel owner) => _owner = owner;

    public ObservableCollection<ImportRowViewModel> Rows { get; } = [];

    [ObservableProperty] private string _sourceText = "";
    [ObservableProperty] private string _sourceName = "";
    [ObservableProperty] private bool _reviewing;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _summary = "";

    public bool NotReviewing => !Reviewing;
    partial void OnReviewingChanged(bool value) => OnPropertyChanged(nameof(NotReviewing));

    [RelayCommand]
    private async Task OpenFile()
    {
        var path = await _owner.Platform.PickOpenFileAsync("Open your passwords text file", "Text files", "*.txt", "*.*");
        if (path == null) return;
        try
        {
            var info = new FileInfo(path);
            if (info.Length > 5 * 1024 * 1024) { Error = "That file is over 5 MB, which doesn't look like a passwords list."; return; }
            SourceText = await File.ReadAllTextAsync(path);
            _sourcePath = path;
            SourceName = info.Name;
            Error = null;
        }
        catch (Exception ex) { Error = "Couldn't read the file: " + ex.Message; }
    }

    [RelayCommand]
    private void FindLogins()
    {
        Error = null;
        var candidates = TextImporter.Parse(SourceText);
        if (candidates.Count == 0) { Error = "No logins found. Paste the text or open the file first."; return; }

        var existing = _owner.Vault.Entries
            .Select(e => (e.Title.Trim().ToLowerInvariant(), e.Username.Trim().ToLowerInvariant()))
            .ToHashSet();

        Rows.Clear();
        foreach (var c in candidates)
        {
            var row = new ImportRowViewModel(c);
            if (existing.Contains((c.Title.Trim().ToLowerInvariant(), c.Username.Trim().ToLowerInvariant())))
            {
                row.Include = false;
                row.Warnings = ("Already in your vault (same name and username). Unticked. " + row.Warnings).Trim();
            }
            row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ImportRowViewModel.Include)) UpdateSummary(); };
            Rows.Add(row);
        }
        Reviewing = true;
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        int included = Rows.Count(r => r.Include);
        int flagged = Rows.Count(r => r.Include && r.HasWarnings);
        int codes = Rows.Count(r => r.Include && r.BackupCodes.Trim().Length > 0);
        Summary = $"{included} of {Rows.Count} selected" +
                  (flagged > 0 ? $" · {flagged} need a look (yellow notes)" : "") +
                  (codes > 0 ? $" · {codes} with backup codes" : "");
        ImportCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void BackToText() => Reviewing = false;

    [RelayCommand]
    private void SelectAll(string? value)
    {
        bool on = value == "true";
        foreach (var r in Rows) r.Include = on;
    }

    [RelayCommand]
    private void Cancel()
    {
        SourceText = "";
        Rows.Clear();
        _owner.Main.Navigate(_owner);
    }

    private bool CanImport() => Rows.Any(r => r.Include);

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task Import()
    {
        if (IsBusy) return;
        var chosen = Rows.Where(r => r.Include).ToList();

        bool hasCodes = chosen.Any(r => r.BackupCodes.Trim().Length > 0);
        bool importCodes = hasCodes;
        if (hasCodes && !await _owner.EnsureRecoveryUnlockedAsync())
        {
            bool proceed = await _owner.Main.ConfirmAsync("Import without backup codes?",
                "Backup codes can only be saved once their own lock is set up or unlocked. Import the logins now and add the codes later?",
                "Import without codes", cancel: "Go back");
            if (!proceed) return;
            importCodes = false;
        }

        IsBusy = true;
        try
        {
            int n = 0;
            foreach (var row in chosen)
            {
                var entry = _owner.Vault.AddEntry(row.ToEntry());
                if (importCodes && row.BackupCodes.Trim().Length > 0)
                    _owner.Vault.SetBackupCodes(entry.Id, row.BackupCodes);
                n++;
            }
            if (!await _owner.PersistAsync()) return;

            _owner.Search = "";
            _owner.OnlyNeedsAttention = false;
            _owner.RebuildList();
            _owner.Status = $"Imported {n} logins.";
            SourceText = "";
            Rows.Clear();
            _owner.Main.Navigate(_owner);

            await OfferToDeleteOriginalAsync(n);
        }
        catch (Exception ex) { Error = "Import failed: " + ex.Message; }
        finally { IsBusy = false; }
    }

    private async Task OfferToDeleteOriginalAsync(int count)
    {
        const string advice =
            "• If your Desktop syncs to OneDrive (or iCloud/Google Drive), delete the file there too and empty that service's recycle bin; it keeps old versions.\n" +
            "• On an SSD, deleted data isn't guaranteed to be erased immediately. Turning on BitLocker (Windows) or FileVault (Mac) protects what's left behind.\n" +
            "• Since the passwords sat in a plain text file, consider changing the ones for your most important accounts (email first).";

        if (_sourcePath == null || !File.Exists(_sourcePath))
        {
            await _owner.Main.AlertAsync($"Imported {count} logins",
                "Next step: delete the original text file (and empty the Recycle Bin).\n\n" + advice);
            return;
        }

        var choice = await _owner.Main.ShowAsync(new DialogViewModel($"Imported {count} logins",
            $"Your logins are now encrypted in the vault. The original file is still sitting in plain text:\n{_sourcePath}\n\n" +
            "Lockbox can overwrite it and delete it permanently (it won't go to the Recycle Bin).\n\n" + advice,
            [new DialogButton("keep", "Keep it for now"), new DialogButton("delete", "Overwrite and delete the file", IsDanger: true)]));

        if (choice != "delete") return;
        try
        {
            await Services.FileShredder.OverwriteAndDeleteAsync(_sourcePath);
            _owner.Status = "Original text file overwritten and deleted.";
        }
        catch (Exception ex)
        {
            await _owner.Main.AlertAsync("Couldn't delete the file", ex.Message + "\n\nDelete it yourself, then empty the Recycle Bin.");
        }
    }
}

public partial class ImportRowViewModel : ObservableObject
{
    public ImportRowViewModel(ImportCandidate c)
    {
        Include = true;
        Title = c.Title;
        Url = c.Url;
        Username = c.Username;
        Password = c.Password;
        TwoFactorEnabled = c.TwoFactorEnabled;
        TwoFactorMethod = c.TwoFactorMethod;
        BackupCodes = c.BackupCodes;
        Notes = c.Notes;
        Warnings = string.Join(" ", c.Warnings);
        SourceText = c.SourceText;
    }

    [ObservableProperty] private bool _include;
    [ObservableProperty] private string _title;
    [ObservableProperty] private string _url;
    [ObservableProperty] private string _username;
    [ObservableProperty] private string _password;
    [ObservableProperty] private bool _twoFactorEnabled;
    [ObservableProperty] private string _backupCodes;
    [ObservableProperty] private string _notes;
    [ObservableProperty] private string _warnings;
    [ObservableProperty] private bool _showSource;

    public TwoFactorMethod TwoFactorMethod { get; }
    public string SourceText { get; }
    public bool HasWarnings => Warnings.Length > 0;
    public bool HasCodes => BackupCodes.Trim().Length > 0;
    public bool HasNotes => Notes.Trim().Length > 0;

    partial void OnWarningsChanged(string value) => OnPropertyChanged(nameof(HasWarnings));

    public Entry ToEntry() => new ImportCandidate
    {
        Title = Title, Url = Url, Username = Username, Password = Password,
        TwoFactorEnabled = TwoFactorEnabled, TwoFactorMethod = TwoFactorMethod, BackupCodes = BackupCodes, Notes = Notes,
    }.ToEntry();

    [RelayCommand]
    private void ToggleSource() => ShowSource = !ShowSource;
}
