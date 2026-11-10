using System.Diagnostics;
using System.Runtime.InteropServices;
using Lockbox.Core.Web;

// Started by Chrome, Edge, Brave or Firefox when the Lockbox extension connects. Passes messages between the
// browser (stdin/stdout) and the running Lockbox (a local pipe). See NativeHostRelay for what it does and doesn't do.
string? caller = BrowserBridge.CallerFromArgs(args);
if (caller == null)
{
    Console.Error.WriteLine("This helper is started by your browser for the Lockbox extension. Open Lockbox.exe instead.");
    return 2;
}

using var stdin = Console.OpenStandardInput();
using var stdout = Console.OpenStandardOutput();

await NativeHostRelay.RunAsync(stdin, stdout, caller,
    launchLockbox: LaunchLockbox,
    // We were started by the browser you're using, so we may let Lockbox come to the front
    // (to ask for approval, the master password, or to unlock).
    beforeForward: AllowForeground);
return 0;

static bool LaunchLockbox()
{
    string exe = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Lockbox.exe" : "Lockbox");
    if (!File.Exists(exe)) return false;
    AllowForeground();
    try
    {
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = AppContext.BaseDirectory });
        return true;
    }
    catch (System.ComponentModel.Win32Exception) { return false; }
}

// Best effort: only decides whether Lockbox's window may come forward, so it must never stop the helper.
static void AllowForeground()
{
    if (!OperatingSystem.IsWindows()) return;
    try { AllowSetForegroundWindow(-1); }
    catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
}

[DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int processId);
