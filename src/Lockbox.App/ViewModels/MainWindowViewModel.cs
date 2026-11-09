using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lockbox.App.Services;

namespace Lockbox.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    public IPlatform Platform { get; }
    public AppSettings Settings { get; }

    [ObservableProperty] private object? _currentPage;
    [ObservableProperty] private DialogViewModel? _dialog;
    [ObservableProperty] private string _windowTitle = "Lockbox";

    /// <summary>The unlocked vault, while there is one (it outlives the Import/Settings pages).</summary>
    public VaultViewModel? ActiveVault { get; private set; }

    public MainWindowViewModel(IPlatform platform, AppSettings? settings = null)
    {
        Platform = platform;
        Settings = settings ?? AppSettings.Load();
        CurrentPage = new UnlockViewModel(this);
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
