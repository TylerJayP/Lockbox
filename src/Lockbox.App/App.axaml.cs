using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Lockbox.App.ViewModels;
using Lockbox.App.Views;

namespace Lockbox.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;

            Services.SingleInstance.ListenForActivation(() => Dispatcher.UIThread.Post(() =>
            {
                if (window.WindowState == Avalonia.Controls.WindowState.Minimized) window.WindowState = Avalonia.Controls.WindowState.Normal;
                window.Show();
                window.Activate();
            }));

            // Never crash a password manager over a stray error: unsaved edits would be lost.
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                e.Handled = true;
                if (e.Exception is ObjectDisposedException) return; // an operation finished after the vault locked
                if (window.DataContext is MainWindowViewModel vm && vm.Dialog == null)
                    _ = vm.AlertAsync("Something went wrong", e.Exception.Message);
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
