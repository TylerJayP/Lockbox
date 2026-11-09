using System.Runtime.InteropServices;

namespace Lockbox.App.Services;

/// <summary>
/// Keeps one copy of Lockbox per Windows user. Opening it again brings the running window forward
/// instead of starting a second copy that could save over the first one's changes.
/// </summary>
internal static class SingleInstance
{
    private const string MutexName = @"Local\Lockbox.SingleInstance";
    private const string ActivateEventName = @"Local\Lockbox.Activate";

    private static Mutex? _mutex;
    private static EventWaitHandle? _activate;

    /// <summary>True if this is the only copy. Otherwise the running copy is asked to show itself.</summary>
    public static bool Claim()
    {
        if (!OperatingSystem.IsWindows()) return true;
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool first);
        if (first) return true;

        // We were just started by the user, so we may hand our right to take the foreground to the running copy.
        AllowSetForegroundWindow(-1);
        _activate.Set();
        return false;
    }

    /// <summary>Calls <paramref name="activate"/> (on a background thread) whenever another launch asks this copy to show itself.</summary>
    public static void ListenForActivation(Action activate)
    {
        if (_activate is not { } ev) return;
        new Thread(() =>
        {
            while (true)
            {
                ev.WaitOne();
                activate();
            }
        }) { IsBackground = true, Name = "Lockbox activation" }.Start();
    }

    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);
}
