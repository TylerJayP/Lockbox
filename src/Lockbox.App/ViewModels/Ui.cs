using Avalonia.Media;
using Lockbox.Core.Model;
using Lockbox.Core.Tools;

namespace Lockbox.App.ViewModels;

internal static class Ui
{
    public static IBrush StrengthBrush(StrengthLevel level) => level switch
    {
        StrengthLevel.VeryWeak => new SolidColorBrush(Color.Parse("#EF4444")),
        StrengthLevel.Weak => new SolidColorBrush(Color.Parse("#F97316")),
        StrengthLevel.Fair => new SolidColorBrush(Color.Parse("#EAB308")),
        StrengthLevel.Strong => new SolidColorBrush(Color.Parse("#22C55E")),
        _ => new SolidColorBrush(Color.Parse("#10B981")),
    };

    public static Geometry? Icon(string key) =>
        Avalonia.Application.Current?.TryGetResource(key, null, out var r) == true ? r as Geometry : null;

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024):0.#} MB",
    };

    public static string Ago(DateTimeOffset when)
    {
        var d = DateTimeOffset.UtcNow - when;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours} h ago";
        if (d.TotalDays < 60) return $"{(int)d.TotalDays} days ago";
        if (d.TotalDays < 730) return $"{(int)(d.TotalDays / 30)} months ago";
        return $"{(int)(d.TotalDays / 365)} years ago";
    }
}

public sealed record TwoFactorOption(TwoFactorMethod Value, string Label)
{
    public static IReadOnlyList<TwoFactorOption> All { get; } =
    [
        new(TwoFactorMethod.AuthenticatorApp, "Authenticator app"),
        new(TwoFactorMethod.Sms, "Text message (SMS)"),
        new(TwoFactorMethod.Email, "Email code"),
        new(TwoFactorMethod.HardwareKey, "Security key / passkey"),
        new(TwoFactorMethod.Other, "Other"),
    ];

    public static TwoFactorOption For(TwoFactorMethod m) => All.FirstOrDefault(o => o.Value == m) ?? All[0];
    public override string ToString() => Label;
}

public sealed record TypeFilter(ItemType? Type, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One row in the sidebar list.</summary>
public sealed class EntryItemViewModel(Entry e, EntryIssue issues)
{
    public Guid Id { get; } = e.Id;
    public ItemType Type { get; } = e.Type;
    public string Title { get; } = string.IsNullOrWhiteSpace(e.Title) ? "(untitled)" : e.Title;
    public string Subtitle { get; } = e.Type switch
    {
        ItemType.Card => CardInfo.Summary(e.Card?.Number ?? ""),
        ItemType.WiFi => e.Wifi?.NetworkName is { Length: > 0 } n ? n : "Wi-Fi network",
        ItemType.SecureNote => "Secure note",
        _ => e.Username.Length > 0 ? e.Username : e.Url,
    };
    public string Initial { get; } = string.IsNullOrWhiteSpace(e.Title) ? "?" : e.Title.Trim()[..1].ToUpperInvariant();
    public bool ShowInitial => Type == ItemType.Login;
    public Avalonia.Media.Geometry? TypeIcon { get; } = e.Type switch
    {
        ItemType.SecureNote => Ui.Icon("IconNote"),
        ItemType.Card => Ui.Icon("IconCard"),
        ItemType.WiFi => Ui.Icon("IconWifi"),
        _ => null,
    };
    public bool HasTwoFactor { get; } = e.Type == ItemType.Login && e.TwoFactorEnabled;
    public bool HasCodes { get; } = e.HasBackupCodes;
    public bool HasAttachments { get; } = e.Attachments.Count > 0;
    public bool IsProtected { get; } = e.Reprompt;
    public bool NeedsAttention { get; } = (issues & VaultAudit.AttentionMask) != 0;
    public string IssueText { get; } = string.Join(" · ", new[]
    {
        issues.HasFlag(EntryIssue.Weak) ? "Weak password" : null,
        issues.HasFlag(EntryIssue.Reused) ? "Reused password" : null,
        issues.HasFlag(EntryIssue.Empty) ? "No password" : null,
    }.Where(s => s != null));
    public IBrush AvatarBrush { get; } = AvatarColor(e.Title);

    private static IBrush AvatarColor(string title)
    {
        string[] palette = ["#4F8CFF", "#A855F7", "#14B8A6", "#F59E0B", "#EC4899", "#22C55E", "#6366F1", "#EF4444"];
        int h = 0;
        foreach (char c in title.ToLowerInvariant()) h = h * 31 + c;
        return new SolidColorBrush(Color.Parse(palette[Math.Abs(h % palette.Length)]));
    }
}
