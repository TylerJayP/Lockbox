using Lockbox.Core.Tools;

namespace Lockbox.App.Services;

public enum AutoTypeResult { Typed, WindowGone, FocusMoved, KeysHeld }

/// <summary>The operating-system side of auto-type. Only Windows has one; elsewhere it's null.</summary>
public interface IAutoTypeHost
{
    /// <summary>Listens for the auto-type hotkey anywhere in Windows. Returns false if another app already uses it.</summary>
    bool RegisterHotkey(Action onPressed);
    void UnregisterHotkey();

    nint ForegroundWindow();

    /// <summary>The last window that was active before Lockbox (where the login form is), or 0.</summary>
    nint PreviousWindow { get; }

    string WindowTitle(nint window);
    bool IsLockboxWindow(nint window);
    void BringLockboxToFront();

    /// <summary>
    /// Switches to the window, waits for Ctrl/Alt/Shift to be let go, then types the steps.
    /// Stops at once if a different window becomes active, so text never lands in the wrong place.
    /// </summary>
    Task<AutoTypeResult> TypeAsync(nint window, IReadOnlyList<AutoTypeStep> steps);
}
