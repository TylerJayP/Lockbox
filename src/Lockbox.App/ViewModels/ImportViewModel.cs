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
    private string? _format;

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
        var path = await _owner.Platform.PickOpenFileAsync("Open a passwords file", "Password exports and text files", "*.csv", "*.txt", "*.*");
        if (path == null) return;
        try
        {
            var info = new FileInfo(path);
            if (info.Length > 10 * 1024 * 1024) { Error = "That file is over 10 MB, which doesn't look like a passwords list."; return; }
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
        var csv = CsvImporter.TryParse(SourceText);
        var candidates = csv?.Candidates ?? TextImporter.Parse(SourceText);
        _format = csv?.Source;
        if (candidates.Count == 0)
        {
            Error = csv != null ? "That export has no logins in it." : "No logins found. Paste the text or open the file first.";
            return;
        }

        // Spot logins you already have: same name and username, or same site and username
        // (browsers name logins after the site, e.g. "github.com", so names alone often differ).
        var byName = _owner.Vault.Entries.Select(e => (Norm(e.Title), Norm(e.Username))).ToHashSet();
        var bySite = _owner.Vault.Entries
            .Where(e => e.Type == ItemType.Login && e.Url.Length > 0)
            .Select(e => (CsvImporter.SiteKey(e.Url), Norm(e.Username)))
            .ToHashSet();

        Rows.Clear();
        foreach (var c in candidates)
        {
            var row = new ImportRowViewModel(c);
            bool known = byName.Contains((Norm(c.Title), Norm(c.Username)))
                         || (c.Type == ItemType.Login && c.Url.Length > 0 && bySite.Contains((CsvImporter.SiteKey(c.Url), Norm(c.Username))));
            if (known && row.Include)
            {
                row.Include = false;
                string why = c.Type == ItemType.Login ? "same site or name, same username" : "same name";
                row.Warnings = ($"Looks like it's already in your vault ({why}). Unticked. " + row.Warnings).Trim();
            }
            row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ImportRowViewModel.Include)) UpdateSummary(); };
            Rows.Add(row);
        }
        Reviewing = true;
        UpdateSummary();
    }

    private static string Norm(string s) => s.Trim().ToLowerInvariant();

    private void UpdateSummary()
    {
        int included = Rows.Count(r => r.Include);
        int flagged = Rows.Count(r => r.Include && r.HasWarnings);
        int codes = Rows.Count(r => r.Include && r.BackupCodes.Trim().Length > 0);
        Summary = (_format != null ? $"Read as a {_format} export · " : "") +
                  $"{included} of {Rows.Count} selected" +
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
            _owner.Status = n == 1 ? "Imported 1 item." : $"Imported {n} items.";
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
        string what = count == 1 ? "1 item" : $"{count} items";
        string browserTip = _format is "Chrome, Edge or Brave" or "Firefox" or "Safari / Apple Passwords"
            ? "\n• Your browser still has its own copy of these passwords. Once you're happy with Lockbox, you can remove them in the browser's password settings."
            : "";
        string advice =
            "• If your Desktop syncs to OneDrive (or iCloud/Google Drive), delete the file there too and empty that service's recycle bin; it keeps old versions.\n" +
            "• On an SSD, deleted data isn't guaranteed to be erased immediately. Turning on BitLocker (Windows) or FileVault (Mac) protects what's left behind.\n" +
            "• Since the passwords sat in a plain text file, consider changing the ones for your most important accounts (email first)." + browserTip;

        if (_sourcePath == null || !File.Exists(_sourcePath))
        {
            await _owner.Main.AlertAsync($"Imported {what}",
                "Next step: delete the original file (and empty the Recycle Bin).\n\n" + advice);
            return;
        }

        var choice = await _owner.Main.ShowAsync(new DialogViewModel($"Imported {what}",
            $"Your passwords are now encrypted in the vault. The original file is still sitting in plain text:\n{_sourcePath}\n\n" +
            "Lockbox can overwrite it and delete it permanently (it won't go to the Recycle Bin).\n\n" + advice,
            [new DialogButton("keep", "Keep it for now"), new DialogButton("delete", "Overwrite and delete the file", IsDanger: true)]));

        if (choice != "delete") return;
        try
        {
            await Services.FileShredder.OverwriteAndDeleteAsync(_sourcePath);
            _owner.Status = "Original file overwritten and deleted.";
        }
        catch (Exception ex)
        {
            await _owner.Main.AlertAsync("Couldn't delete the file", ex.Message + "\n\nDelete it yourself, then empty the Recycle Bin.");
        }
    }
}

public partial class ImportRowViewModel : ObservableObject
{
    private readonly ImportCandidate _candidate;

    public ImportRowViewModel(ImportCandidate c)
    {
        _candidate = c;
        Include = !c.SuggestSkip;
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
    public bool IsLogin => _candidate.Type == ItemType.Login;
    public string Extras => _candidate.ExtrasSummary;
    public bool HasExtras => Extras.Length > 0;
    public bool HasWarnings => Warnings.Length > 0;
    public bool HasCodes => BackupCodes.Trim().Length > 0;
    public bool HasNotes => Notes.Trim().Length > 0;

    partial void OnWarningsChanged(string value) => OnPropertyChanged(nameof(HasWarnings));

    /// <summary>The candidate with your edits from the review screen; card, Wi-Fi, fields and tags come along unchanged.</summary>
    public Entry ToEntry()
    {
        _candidate.Title = Title;
        _candidate.Url = Url;
        _candidate.Username = Username;
        _candidate.Password = Password;
        _candidate.TwoFactorEnabled = TwoFactorEnabled;
        _candidate.BackupCodes = BackupCodes;
        _candidate.Notes = Notes;
        return _candidate.ToEntry();
    }

    [RelayCommand]
    private void ToggleSource() => ShowSource = !ShowSource;
}
