using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lockbox.App.Services;
using Lockbox.Core.Updates;

namespace Lockbox.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    public IPlatform Platform { get; }
    public AppSettings Settings { get; }

    [ObservableProperty] private object? _currentPage;
    [ObservableProperty] private DialogViewModel? _dialog;
    [ObservableProperty] private string _windowTitle = "Lockbox";

    // ───────────── updates ─────────────
    public UpdateService Updates { get; } = new();
    [ObservableProperty] private bool _updateBannerVisible;
    [ObservableProperty] private string _updateVersion = "";
    [ObservableProperty] private string _updateNotes = "";
    [ObservableProperty] private bool _updateBusy;
    [ObservableProperty] private int _updateProgress;
    public bool UpdateIdle => !UpdateBusy;
    partial void OnUpdateBusyChanged(bool value) => OnPropertyChanged(nameof(UpdateIdle));

    /// <summary>Checks GitHub for a newer release. Returns a sentence describing the result (for Settings).</summary>
    public async Task<string> CheckForUpdatesAsync(bool userInitiated)
    {
        var r = await Updates.CheckAsync();
        switch (r.Status)
        {
            case UpdateCheckStatus.Available:
                UpdateVersion = r.Version!;
                UpdateNotes = string.IsNullOrWhiteSpace(r.Notes) ? "No release notes for this version." : r.Notes!;
                UpdateBannerVisible = true;
                return $"Lockbox {r.Version} is available.";
            case UpdateCheckStatus.UpToDate:
                return $"You're on the latest version ({UpdateService.CurrentVersion}).";
            case UpdateCheckStatus.NotInstalled:
                return "This copy wasn't installed with Lockbox-win-Setup.exe, so it can't update itself. Install it from the Releases page once; it updates itself from then on.";
            case UpdateCheckStatus.NotConfigured:
                return "Updates aren't set up in this build (no signing key).";
            default:
                return userInitiated ? $"Couldn't check for updates: {r.Error}" : "";
        }
    }

    [RelayCommand]
    private void DismissUpdate() => UpdateBannerVisible = false;

    [RelayCommand]
    private Task ShowUpdateNotes() =>
        ShowAsync(new DialogViewModel($"What's new in Lockbox {UpdateVersion}", UpdateNotes, [new DialogButton("ok", "Close", IsPrimary: true)])
        {
            IsMarkdown = true,
            Width = 580,
        });

    [RelayCommand]
    private async Task InstallUpdate()
    {
        if (UpdateBusy) return;
        if (ActiveVault != null && !await ConfirmAsync($"Install Lockbox {UpdateVersion}?",
                "Your changes will be saved and the vault locked, then Lockbox restarts on the new version. Unlock it again afterwards.",
                "Update and restart"))
            return;

        UpdateBusy = true;
        UpdateProgress = 0;
        try
        {
            await Updates.DownloadVerifyAndRestartAsync(
                p => Dispatcher.UIThread.Post(() => UpdateProgress = p),
                beforeRestart: () =>
                {
                    ActiveVault?.SaveAndCloseOnExit();
                    Platform.Clipboard.ClearIfOursOnExit();
                });
        }
        catch (UpdateVerificationException ex)
        {
            UpdateBannerVisible = false;
            await AlertAsync("Update blocked",
                ex.Message + "\n\nLockbox only installs updates signed with the developer's key, so nothing was installed. " +
                "If this keeps happening, tell the person who sent you Lockbox.");
        }
        catch (Exception ex)
        {
            await AlertAsync("Couldn't update", ex.Message + "\n\nNothing was changed. You can try again later.");
        }
        finally { UpdateBusy = false; }
    }

    /// <summary>The unlocked vault, while there is one (it outlives the Import/Settings pages).</summary>
    public VaultViewModel? ActiveVault { get; private set; }

    public MainWindowViewModel(IPlatform platform, AppSettings? settings = null)
    {
        Platform = platform;
        Settings = settings ?? AppSettings.Load();
        CurrentPage = new UnlockViewModel(this);
        BrowserRequests = new BrowserRequestHandler(this);
    }

    // ───────────── browser extension ─────────────

    private BrowserBridgeServer? _browserBridge;

    /// <summary>Answers the browser extension (also used directly by tests).</summary>
    public BrowserRequestHandler BrowserRequests { get; }

    /// <summary>Why the browser extension can't work right now, if something is wrong (shown in Settings).</summary>
    public string? BrowserProblem { get; private set; }

    public bool BrowserBridgeRunning => _browserBridge != null;

    /// <summary>
    /// Matches the browser connection to the setting: registers the helper with the browsers and listens for it,
    /// or stops and removes the registration. Called at startup and when the setting changes.
    /// </summary>
    public void ApplyBrowserIntegration()
    {
        if (Settings.BrowserExtensionEnabled)
        {
            BrowserProblem = BrowserHostRegistration.Register();
            if (_browserBridge == null)
            {
                _browserBridge = new BrowserBridgeServer(req =>
                    Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => BrowserRequests.HandleAsync(req)));
                _browserBridge.Start();
            }
        }
        else
        {
            _browserBridge?.Dispose();
            _browserBridge = null;
            BrowserProblem = null;
            BrowserHostRegistration.Unregister();
        }
    }

    public void Navigate(object page) => CurrentPage = page;

    public void OnUnlocked(VaultViewModel vault)
    {
        ActiveVault = vault;
        WindowTitle = $"{vault.VaultName} — Lockbox";
        Navigate(vault);
    }

    public void OnLocked(string? reason)
    {
        Dialog?.CancelCommand.Execute(null); // anything waiting on a dialog gets "cancelled"
        Dialog = null;
        ActiveVault = null;
        WindowTitle = "Lockbox";
        Navigate(new UnlockViewModel(this, reason));
    }

    // ───────────── dialogs ─────────────

    public async Task<string?> ShowAsync(DialogViewModel dialog)
    {
        Dialog = dialog;
        try { return await dialog.Result; }
        finally { if (Dialog == dialog) Dialog = null; }
    }

    public Task AlertAsync(string title, string message) =>
        ShowAsync(new DialogViewModel(title, message, [new DialogButton("ok", "OK", IsPrimary: true)]));

    public async Task<bool> ConfirmAsync(string title, string message, string confirm, bool danger = false, string cancel = "Cancel") =>
        await ShowAsync(new DialogViewModel(title, message,
            [new DialogButton("cancel", cancel), new DialogButton("ok", confirm, IsPrimary: !danger, IsDanger: danger)])) == "ok";

    /// <summary>Asks for a secret (e.g. the recovery passphrase). Returns null if cancelled.</summary>
    public async Task<string?> PromptSecretAsync(string title, string message, string label, string confirm,
        string? error = null, bool withConfirmation = false, Func<string, string, string?>? validate = null)
    {
        var d = new DialogViewModel(title, message, [new DialogButton("cancel", "Cancel"), new DialogButton("ok", confirm, IsPrimary: true, Validates: true)])
        {
            InputLabel = label,
            ConfirmLabel = withConfirmation ? "Type it again" : null,
            Error = error,
            Validate = validate,
        };
        return await ShowAsync(d) == "ok" ? d.Input : null;
    }

    /// <summary>Called when the window is closing: save quietly and wipe the clipboard.</summary>
    public void OnClosing()
    {
        ActiveVault?.SaveAndCloseOnExit();
        Platform.Clipboard.ClearIfOursOnExit();
        _browserBridge?.Dispose();
    }

    public void OnMinimized()
    {
        if (ActiveVault is { } v && v.Vault.Settings.LockOnMinimize)
            _ = v.LockAsync("Locked when the window was minimized.");
    }
}

public sealed record DialogButton(string Id, string Label, bool IsPrimary = false, bool IsDanger = false, bool Validates = false);

public partial class DialogViewModel : ObservableObject
{
    private readonly TaskCompletionSource<string?> _tcs = new();

    public DialogViewModel(string title, string message, IReadOnlyList<DialogButton> buttons)
    {
        Title = title;
        Message = message;
        Buttons = buttons;
    }

    public string Title { get; }
    public string Message { get; }
    public IReadOnlyList<DialogButton> Buttons { get; }
    public Task<string?> Result => _tcs.Task;

    /// <summary>Render <see cref="Message"/> as Markdown (release notes) instead of plain text.</summary>
    public bool IsMarkdown { get; init; }
    public bool IsPlainText => !IsMarkdown;
    public double Width { get; init; } = 500;

    public string? InputLabel { get; init; }
    public string? ConfirmLabel { get; init; }
    public bool HasInput => InputLabel != null;
    public bool HasConfirm => ConfirmLabel != null;
    public Func<string, string, string?>? Validate { get; init; }

    [ObservableProperty] private string _input = "";
    [ObservableProperty] private string _confirmInput = "";
    [ObservableProperty] private string? _error;

    partial void OnInputChanged(string value) => Error = null;

    [RelayCommand]
    private void Press(DialogButton button)
    {
        if (button.Validates)
        {
            if (HasInput && Input.Length == 0) { Error = "This can't be empty."; return; }
            if (HasConfirm && Input != ConfirmInput) { Error = "The two entries don't match."; return; }
            if (Validate?.Invoke(Input, button.Id) is { } err) { Error = err; return; }
        }
        _tcs.TrySetResult(button.Id);
    }

    /// <summary>Enter key: press the primary button.</summary>
    [RelayCommand]
    private void Submit()
    {
        var primary = Buttons.FirstOrDefault(b => b.IsPrimary) ?? Buttons.LastOrDefault();
        if (primary != null) Press(primary);
    }

    [RelayCommand]
    private void Cancel() => _tcs.TrySetResult(null);
}
