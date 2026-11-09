using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Input.Platform;
using Avalonia.Threading;

namespace Lockbox.App.Services;

/// <summary>
/// Copies secrets to the clipboard and wipes them after a delay, but only if the clipboard still
/// holds what we put there (so we never wipe something the user copied afterwards).
///
/// On Windows it also marks the data so it is kept out of Win+V clipboard history and cloud
/// clipboard sync (the same formats KeePassXC and 1Password use).
/// </summary>
public sealed class SecureClipboard(Func<IClipboard?> clipboard, Func<nint> windowHandle)
{
    private uint _ourWindowsSequence;
    private string? _ourText;
    private DispatcherTimer? _timer;

    /// <summary>When the copied secret will be wiped, for the countdown in the status bar.</summary>
    public DateTime? ClearsAtUtc { get; private set; }

    public async Task CopyAsync(string text, int clearAfterSeconds)
    {
        if (string.IsNullOrEmpty(text)) return;

        bool done = false;
        if (OperatingSystem.IsWindows())
        {
            try { done = Win32.SetSensitiveText(windowHandle(), text, out _ourWindowsSequence); }
            catch { done = false; }
        }
        if (!done)
        {
            var cb = clipboard() ?? throw new InvalidOperationException("Clipboard isn't available.");
            await cb.SetTextAsync(text);
            _ourText = text;
            _ourWindowsSequence = 0;
        }

        ClearsAtUtc = DateTime.UtcNow.AddSeconds(clearAfterSeconds);
        _timer?.Stop();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(clearAfterSeconds) };
        _timer.Tick += async (_, _) => await ClearIfOursAsync();
        _timer.Start();
    }

    public async Task ClearIfOursAsync()
    {
        _timer?.Stop();
        _timer = null;
        ClearsAtUtc = null;
        try
        {
            if (OperatingSystem.IsWindows() && _ourWindowsSequence != 0)
            {
                Win32.ClearIfSequence(windowHandle(), _ourWindowsSequence);
            }
            else if (_ourText != null && clipboard() is { } cb)
            {
                var current = await cb.TryGetTextAsync();
                if (current == _ourText) await cb.ClearAsync();
            }
        }
        catch
        {
            // Clipboard can be briefly locked by another app; nothing more we can safely do.
        }
        finally
        {
            _ourText = null;
            _ourWindowsSequence = 0;
        }
    }

    /// <summary>
    /// Synchronous wipe for app exit. On Windows the clipboard keeps our data after the process ends,
    /// so this must run before shutdown. (Other platforms drop it when the app exits.)
    /// </summary>
    public void ClearIfOursOnExit()
    {
        _timer?.Stop();
        try
        {
            if (OperatingSystem.IsWindows() && _ourWindowsSequence != 0) Win32.ClearIfSequence(windowHandle(), _ourWindowsSequence);
        }
        catch { }
        _ourWindowsSequence = 0;
        _ourText = null;
        ClearsAtUtc = null;
    }

    [SupportedOSPlatform("windows")]
    private static class Win32
    {
        private const uint CF_UNICODETEXT = 13;
        private const uint GMEM_MOVEABLE = 0x0002;

        [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(nint hWndNewOwner);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
        [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
        [DllImport("user32.dll", SetLastError = true)] private static extern nint SetClipboardData(uint uFormat, nint hMem);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormatW(string lpszFormat);
        [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GlobalAlloc(uint uFlags, nuint dwBytes);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GlobalLock(nint hMem);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(nint hMem);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GlobalFree(nint hMem);

        private static bool Open(nint hwnd)
        {
            // Another app may hold the clipboard for a moment.
            for (int i = 0; i < 10; i++)
            {
                if (OpenClipboard(hwnd)) return true;
                Thread.Sleep(15);
            }
            return false;
        }

        public static bool SetSensitiveText(nint hwnd, string text, out uint sequence)
        {
            sequence = 0;
            // A null owner makes SetClipboardData fail after EmptyClipboard, so require a real window.
            if (hwnd == 0 || !Open(hwnd)) return false;
            try
            {
                if (!EmptyClipboard()) return false;

                nint hText = AllocUnicode(text);
                if (SetClipboardData(CF_UNICODETEXT, hText) == 0) { GlobalFree(hText); return false; }

                // Presence of this format tells clipboard monitors/history to ignore the content.
                SetDword("ExcludeClipboardContentFromMonitorProcessing", 1);
                // Explicit opt-outs for Win+V history and cross-device cloud clipboard.
                SetDword("CanIncludeInClipboardHistory", 0);
                SetDword("CanUploadToCloudClipboard", 0);
            }
            finally
            {
                CloseClipboard();
            }
            sequence = GetClipboardSequenceNumber();
            return true;
        }

        public static void ClearIfSequence(nint hwnd, uint sequence)
        {
            if (GetClipboardSequenceNumber() != sequence) return; // user copied something else since
            if (!Open(hwnd)) return;
            try { EmptyClipboard(); }
            finally { CloseClipboard(); }
        }

        private static void SetDword(string formatName, uint value)
        {
            uint fmt = RegisterClipboardFormatW(formatName);
            if (fmt == 0) return;
            nint h = GlobalAlloc(GMEM_MOVEABLE, 4);
            if (h == 0) return;
            nint p = GlobalLock(h);
            Marshal.WriteInt32(p, unchecked((int)value));
            GlobalUnlock(h);
            if (SetClipboardData(fmt, h) == 0) GlobalFree(h);
        }

        private static nint AllocUnicode(string text)
        {
            int bytes = (text.Length + 1) * 2;
            nint h = GlobalAlloc(GMEM_MOVEABLE, (nuint)bytes);
            if (h == 0) throw new OutOfMemoryException();
            nint p = GlobalLock(h);
            Marshal.Copy(text.ToCharArray(), 0, p, text.Length);
            Marshal.WriteInt16(p, text.Length * 2, 0);
            GlobalUnlock(h);
            return h;
        }
    }
}
