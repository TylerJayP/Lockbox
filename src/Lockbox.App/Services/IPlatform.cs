namespace Lockbox.App.Services;

/// <summary>Things view-models need from the window: file pickers, clipboard, launching URLs.</summary>
public interface IPlatform
{
    Task<string?> PickOpenFileAsync(string title, string filterName, params string[] patterns);
    Task<string?> PickSaveFileAsync(string title, string suggestedName, string extension, string filterName);
    SecureClipboard Clipboard { get; }
    void OpenUrl(string url);
    void OpenFolder(string path);

    /// <summary>Hide or show the window in screenshots and screen sharing. Returns false if the OS doesn't support it.</summary>
    bool SetCaptureProtection(bool enabled);

    /// <summary>Typing logins into other apps. Null where it isn't supported (anything but Windows).</summary>
    IAutoTypeHost? AutoType { get; }
}

/// <summary>Time of the last keyboard/mouse input in the window, for auto-lock.</summary>
public static class Activity
{
    public static DateTime LastInputUtc { get; private set; } = DateTime.UtcNow;
    public static void Touch() => LastInputUtc = DateTime.UtcNow;
}
