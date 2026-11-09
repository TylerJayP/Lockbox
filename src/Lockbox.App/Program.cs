using Avalonia;
using Velopack;

namespace Lockbox.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run first: handles install/uninstall/update hooks from the Velopack installer.
        // Auto-apply is off so an update is only ever installed after Lockbox has checked its signature.
        VelopackApp.Build().SetAutoApplyOnStartup(false).Run();

        // Already running? Bring that window forward instead of opening a second copy.
        if (!Services.SingleInstance.Claim()) return;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
