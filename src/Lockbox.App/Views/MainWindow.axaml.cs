using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Lockbox.App.Services;
using Lockbox.App.ViewModels;

namespace Lockbox.App.Views;

public partial class MainWindow : Window, IPlatform
{
    private readonly MainWindowViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        Clipboard = new SecureClipboard(() => base.Clipboard, () => TryGetPlatformHandle()?.Handle ?? 0);
        _vm = new MainWindowViewModel(this);
        DataContext = _vm;
        _vm.PropertyChanged += OnVmPropertyChanged;

        // Any keyboard or mouse input counts as activity for auto-lock.
        AddHandler(KeyDownEvent, (_, _) => Activity.Touch(), RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerMovedEvent, (_, _) => Activity.Touch(), RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerPressedEvent, (_, _) => Activity.Touch(), RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerWheelChangedEvent, (_, _) => Activity.Touch(), RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);
    }

    public new SecureClipboard Clipboard { get; }

    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _vm.Dialog is { } d)
        {
            d.CancelCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.Dialog) && _vm.Dialog is { HasInput: true })
            Dispatcher.UIThread.Post(() => this.FindControl<TextBox>("DialogInput")?.Focus(), DispatcherPriority.Input);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty && WindowState == WindowState.Minimized)
            _vm.OnMinimized();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _vm.OnClosing();
        base.OnClosing(e);
    }

    // ───────────── IPlatform ─────────────

    public async Task<string?> PickOpenFileAsync(string title, string filterName, params string[] patterns)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(filterName) { Patterns = patterns }, FilePickerFileTypes.All],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickSaveFileAsync(string title, string suggestedName, string extension, string filterName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = extension.Length > 0 ? extension : null,
            FileTypeChoices = [new FilePickerFileType(filterName) { Patterns = [extension.Length > 0 ? "*." + extension : "*"] }],
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    }

    public void OpenUrl(string url) => _ = Launcher.LaunchUriAsync(new Uri(url));

    public void OpenFolder(string path) => _ = Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path));

    private bool _captureProtection;

    public bool SetCaptureProtection(bool enabled)
    {
        _captureProtection = enabled;
        if (!OperatingSystem.IsWindows()) return false;
        var hwnd = TryGetPlatformHandle()?.Handle ?? 0;
        if (hwnd == 0) return false; // applied in OnOpened once the window exists
        const uint WDA_NONE = 0, WDA_MONITOR = 1, WDA_EXCLUDEFROMCAPTURE = 0x11;
        if (!enabled) return SetWindowDisplayAffinity(hwnd, WDA_NONE);
        // EXCLUDEFROMCAPTURE (Windows 10 2004+) makes the window invisible in captures; MONITOR shows it as black.
        return SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE) || SetWindowDisplayAffinity(hwnd, WDA_MONITOR);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        SetCaptureProtection(_vm.Settings.HideFromScreenCapture);

        // Quietly look for an update a few seconds after start (never blocks or nags if offline).
        if (_vm.Settings.CheckForUpdates && UpdateService.IsConfigured)
            DispatcherTimer.RunOnce(() => _ = _vm.CheckForUpdatesAsync(userInitiated: false), TimeSpan.FromSeconds(3));
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(nint hWnd, uint dwAffinity);
}
