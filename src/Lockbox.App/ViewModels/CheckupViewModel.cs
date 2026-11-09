using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lockbox.Core.Model;
using Lockbox.Core.Tools;

namespace Lockbox.App.ViewModels;

/// <summary>Security check-up: problems grouped and ordered by what to fix first.</summary>
public partial class CheckupViewModel : ObservableObject
{
    private readonly VaultViewModel _owner;

    public CheckupViewModel(VaultViewModel owner)
    {
        _owner = owner;
        var entries = owner.Vault.Entries;
        var issues = owner.Issues;

        CheckupSection Make(EntryIssue flag, string title, string why, string severity) => new(
            title, why, severity,
            entries.Where(e => issues.GetValueOrDefault(e.Id).HasFlag(flag))
                   .OrderBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase)
                   .Select(e => new CheckupItem(e.Id, string.IsNullOrWhiteSpace(e.Title) ? "(untitled)" : e.Title, Subtitle(e)))
                   .ToList());

        Sections =
        [
            Make(EntryIssue.Reused, "Reused passwords",
                "When one site using a shared password leaks, attackers try it everywhere else. Give each of these its own password.", "high"),
            Make(EntryIssue.Weak, "Weak passwords",
                "These could be guessed quickly. Use the generator to replace them.", "high"),
            Make(EntryIssue.Empty, "Missing passwords",
                "These items have no password saved.", "medium"),
            Make(EntryIssue.Old, "Not changed in over 2 years",
                "Old passwords have had more time to appear in a breach. Change the important ones (email, banking) first.", "medium"),
            Make(EntryIssue.NoTwoFactor, "Logins without two-factor",
                "If a site offers 2FA (an authenticator app is best), turn it on, then tick the 2FA box here.", "low"),
        ];
        Sections = Sections.Where(s => s.Items.Count > 0).ToList();

        int checkable = entries.Count(e => e.HasCheckablePassword);
        int clean = entries.Count(e => e.HasCheckablePassword && (issues.GetValueOrDefault(e.Id) & (VaultAudit.AttentionMask | EntryIssue.Old)) == 0);
        int urgent = Sections.Where(s => s.Severity == "high").Sum(s => s.Items.Count);
        Headline = checkable == 0 ? "Nothing to check yet"
            : urgent == 0 ? "No urgent problems"
            : $"{urgent} password problem{(urgent == 1 ? "" : "s")} to fix";
        Summary = checkable == 0
            ? "Add some logins or Wi-Fi passwords and they'll be checked here."
            : $"{clean} of {checkable} passwords are strong, unique and recently changed. Everything is checked on this computer; nothing is sent anywhere.";
    }

    public string Headline { get; }
    public string Summary { get; }
    public IReadOnlyList<CheckupSection> Sections { get; }
    public bool AllClear => Sections.Count == 0;

    [RelayCommand]
    private void Back() => _owner.Main.Navigate(_owner);

    [RelayCommand]
    private void Open(CheckupItem? item)
    {
        if (item != null) _owner.ShowItem(item.Id);
    }

    private static string Subtitle(Entry e) => e.Type switch
    {
        ItemType.WiFi => e.Wifi?.NetworkName ?? "Wi-Fi",
        _ => e.Username.Length > 0 ? e.Username : e.Url,
    };
}

public sealed record CheckupItem(Guid Id, string Title, string Subtitle);

public sealed record CheckupSection(string Title, string Why, string Severity, IReadOnlyList<CheckupItem> Items)
{
    public string CountText => Items.Count.ToString();
    public bool IsHigh => Severity == "high";
    public bool IsMedium => Severity == "medium";
    public bool IsLow => Severity == "low";
}
