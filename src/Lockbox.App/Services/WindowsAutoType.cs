using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Avalonia.Controls;
using Avalonia.Threading;
using Lockbox.Core.Tools;

namespace Lockbox.App.Services;

/// <summary>
/// Auto-type on Windows: a global hotkey (RegisterHotKey), tracking which window was active before
/// Lockbox (a foreground-change hook), and typing with SendInput's Unicode mode, which ignores the
/// keyboard layout and Caps Lock so every character arrives exactly as stored.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsAutoType : IAutoTypeHost, IDisposable
{
    private const int HotkeyId = 0x4C42; // "LB"
    private const uint ModAlt = 0x1, ModControl = 0x2, ModNoRepeat = 0x4000, VkA = 0x41;
    private const uint WmHotkey = 0x0312;
    private const uint EventSystemForeground = 0x0003, WineventOutOfContext = 0x0, WineventSkipOwnProcess = 0x2;

    private readonly Window _window;
    private readonly Win32Properties.CustomWndProcHookCallback _wndProc;
    private readonly WinEventProc _foregroundChanged;
    private readonly nint _winEventHook;
    private readonly uint _ownPid = (uint)Environment.ProcessId;
    private Action? _onHotkey;
    private bool _registered;
    private nint _previous;

    public WindowsAutoType(Window window)
    {
        _window = window;
        _wndProc = WndProc;                       // keep the delegates alive as long as the hooks
        _foregroundChanged = OnForegroundChanged;
        Win32Properties.AddWndProcHookCallback(window, _wndProc);
        _winEventHook = SetWinEventHook(EventSystemForeground, EventSystemForeground, 0, _foregroundChanged, 0, 0,
            WineventOutOfContext | WineventSkipOwnProcess);
    }

    private nint OwnHandle => _window.TryGetPlatformHandle()?.Handle ?? 0;

    public bool RegisterHotkey(Action onPressed)
    {
        _onHotkey = onPressed;
        if (_registered) return true;
        _registered = OwnHandle != 0 && RegisterHotKey(OwnHandle, HotkeyId, ModControl | ModAlt | ModNoRepeat, VkA);
        return _registered;
    }

    public void UnregisterHotkey()
    {
        _onHotkey = null;
        if (_registered) UnregisterHotKey(OwnHandle, HotkeyId);
        _registered = false;
    }

    private nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam == HotkeyId && _onHotkey is { } act)
        {
            handled = true;
            Dispatcher.UIThread.Post(act);
        }
        return 0;
    }

    private void OnForegroundChanged(nint hook, uint ev, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd != 0 && !IsLockboxWindow(hwnd)) _previous = hwnd;
    }

    public nint ForegroundWindow() => GetForegroundWindow();

    public nint PreviousWindow => _previous != 0 && IsWindow(_previous) ? _previous : 0;

    public string WindowTitle(nint window)
    {
        int len = GetWindowTextLengthW(window);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        GetWindowTextW(window, sb, sb.Capacity);
        return sb.ToString();
    }

    public bool IsLockboxWindow(nint window)
    {
        GetWindowThreadProcessId(window, out uint pid);
        return pid == _ownPid;
    }

    public void BringLockboxToFront() => Dispatcher.UIThread.Post(() =>
    {
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Show();
        _window.Activate();
    });

    public Task<AutoTypeResult> TypeAsync(nint window, IReadOnlyList<AutoTypeStep> steps) => Task.Run(() =>
    {
        if (window == 0 || !IsWindow(window)) return AutoTypeResult.WindowGone;
        if (GetForegroundWindow() != window)
        {
            if (IsIconic(window)) ShowWindow(window, SwRestore);
            SetForegroundWindow(window);
            if (!WaitFor(() => GetForegroundWindow() == window, 1500)) return AutoTypeResult.FocusMoved;
        }
        // The hotkey's Ctrl and Alt may still be held down; typing now would send shortcuts instead of letters.
        if (!WaitFor(() => !ModifiersDown(), 3000)) return AutoTypeResult.KeysHeld;
        Thread.Sleep(80);

        foreach (var step in steps)
        {
            if (GetForegroundWindow() != window) return AutoTypeResult.FocusMoved;
            switch (step.Kind)
            {
                case AutoTypeStep.Kinds.Text:
                    foreach (char ch in step.Text)
                    {
                        if (GetForegroundWindow() != window) return AutoTypeResult.FocusMoved;
                        if (char.IsControl(ch)) continue;
                        SendUnicode(ch);
                        Thread.Sleep(4);
                    }
                    break;
                case AutoTypeStep.Kinds.Tab: SendVirtualKey(0x09); Thread.Sleep(40); break;
                case AutoTypeStep.Kinds.Enter: SendVirtualKey(0x0D); Thread.Sleep(40); break;
                case AutoTypeStep.Kinds.Delay: Thread.Sleep(step.DelayMs); break;
            }
        }
        return AutoTypeResult.Typed;
    });

    private static bool WaitFor(Func<bool> condition, int timeoutMs)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > until) return false;
            Thread.Sleep(20);
        }
        return true;
    }

    private static bool ModifiersDown() =>
        new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(vk => (GetAsyncKeyState(vk) & 0x8000) != 0);

    private static void SendUnicode(char ch)
    {
        var inputs = new INPUT[]
        {
            new() { type = InputKeyboard, U = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = KeyeventfUnicode } } },
            new() { type = InputKeyboard, U = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = KeyeventfUnicode | KeyeventfKeyUp } } },
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static void SendVirtualKey(ushort vk)
    {
        var inputs = new INPUT[]
        {
            new() { type = InputKeyboard, U = new InputUnion { ki = new KEYBDINPUT { wVk = vk } } },
            new() { type = InputKeyboard, U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = KeyeventfKeyUp } } },
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    public void Dispose()
    {
        UnregisterHotkey();
        if (_winEventHook != 0) UnhookWinEvent(_winEventHook);
        Win32Properties.RemoveWndProcHookCallback(_window, _wndProc);
    }

    // ───────────── Win32 ─────────────

    private const uint InputKeyboard = 1, KeyeventfKeyUp = 0x2, KeyeventfUnicode = 0x4;
    private const int SwRestore = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion U; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;   // largest member: keeps the size Windows expects
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public nint dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public nint dwExtraInfo; }

    private delegate void WinEventProc(nint hook, uint ev, nint hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(nint hWnd, int id);
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint hmod, WinEventProc proc, uint idProcess, uint idThread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hWnd, int cmd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(nint hWnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern int GetWindowTextLengthW(nint hWnd);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
}
