using Lockbox.Core.Model;
using Lockbox.Core.Tools;
using Kinds = Lockbox.Core.Tools.AutoTypeStep.Kinds;

namespace Lockbox.Tests;

public class AutoTypeTests
{
    private static Entry Login(string title, string url, string user = "me", string pass = "pw") =>
        new() { Title = title, Url = url, Username = user, Password = pass };

    [Fact]
    public void Default_sequence_is_username_tab_password_enter()
    {
        var steps = AutoTypeSequence.For(Login("GitHub", "github.com", "TylerJayP", "s3cret!"));
        Assert.Equal([Kinds.Text, Kinds.Tab, Kinds.Text, Kinds.Enter], steps.Select(s => s.Kind));
        Assert.Equal("TylerJayP", steps[0].Text);
        Assert.Equal("s3cret!", steps[2].Text);
    }

    [Fact]
    public void No_username_types_just_the_password()
    {
        var steps = AutoTypeSequence.For(Login("Router", "192.168.1.1", user: "", pass: "admin1"));
        Assert.Equal([Kinds.Text, Kinds.Enter], steps.Select(s => s.Kind));
        Assert.Equal("admin1", steps[0].Text);
    }

    [Fact]
    public void Custom_field_overrides_the_sequence_for_two_step_sign_ins()
    {
        var e = Login("Google", "accounts.google.com", "me@gmail.com", "g-pass");
        e.Fields.Add(new CustomField("auto-type", "{USERNAME}{ENTER}{DELAY 1500}{PASSWORD}{ENTER}", false));
        var steps = AutoTypeSequence.For(e);
        Assert.Equal([Kinds.Text, Kinds.Enter, Kinds.Delay, Kinds.Text, Kinds.Enter], steps.Select(s => s.Kind));
        Assert.Equal(1500, steps[2].DelayMs);
        Assert.Equal("g-pass", steps[3].Text);
    }

    [Fact]
    public void Literal_text_braces_and_unknown_tokens_are_typed_as_written()
    {
        var steps = AutoTypeSequence.Parse("id:{USERNAME} {{}x{}} {WHAT}{DELAY=99999}", "bob", "pw");
        Assert.Equal("id:bob {x} {WHAT}", steps[0].Text);
        Assert.Equal(AutoTypeSequence.MaxDelayMs, steps[1].DelayMs);   // capped
    }

    [Fact]
    public void Passwords_with_braces_are_never_reinterpreted()
    {
        var steps = AutoTypeSequence.For(Login("X", "x.com", "u", "{TAB}{ENTER}"));
        Assert.Equal("{TAB}{ENTER}", steps[2].Text);
    }

    [Theory]
    [InlineData("Sign in to GitHub · GitHub — Mozilla Firefox", "GitHub")]
    [InlineData("Netflix - Google Chrome", "Netflix")]
    [InlineData("Netflix and 3 more pages - Personal - Microsoft​ Edge", "Netflix")]
    [InlineData("Steam", "Steam")]
    [InlineData("store.steampowered.com/login - Brave", "Steam")]
    [InlineData("Sign in - Google Accounts - Google Chrome", "Google")]
    [InlineData("Sign in - Google Accounts - Work - Microsoft Edge", "Google")]
    public void Matches_windows_to_logins(string windowTitle, string expected)
    {
        Entry[] vault =
        [
            Login("GitHub", "https://github.com"),
            Login("Netflix", "netflix.com"),
            Login("Steam", "https://store.steampowered.com"),
            Login("Google", "https://accounts.google.com"),
        ];
        var matches = AutoTypeMatcher.Find(vault, windowTitle);
        Assert.Equal(expected, matches.First().Entry.Title);
        Assert.Single(matches);
    }

    [Fact]
    public void The_browser_name_never_matches_a_login()
    {
        Entry[] vault = [Login("Google", "https://accounts.google.com"), Login("Edge", ""), Login("Opera", "")];
        Assert.Empty(AutoTypeMatcher.Find(vault, "Some Recipe Blog - Google Chrome"));
        Assert.Empty(AutoTypeMatcher.Find(vault, "Some Recipe Blog - Work - Microsoft Edge"));
        Assert.Equal("Sign in - Google Accounts", AutoTypeMatcher.CleanWindowTitle("Sign in - Google Accounts - Personal - Microsoft\u200b Edge"));
        Assert.Equal("Sign in - Google Accounts", AutoTypeMatcher.CleanWindowTitle("Sign in - Google Accounts - Google Chrome"));
    }

    [Fact]
    public void Matches_need_whole_words_and_a_password()
    {
        Entry[] vault =
        [
            Login("Art", "art.com"),
            Login("Notes", "notes.example.com", pass: ""),
            new() { Type = ItemType.SecureNote, Title = "Bank PIN", Password = "1234" },
        ];
        Assert.Empty(AutoTypeMatcher.Find(vault, "Smart TV setup"));
        Assert.Empty(AutoTypeMatcher.Find(vault, "Notes"));
        Assert.Empty(AutoTypeMatcher.Find(vault, "Bank PIN"));
    }

    [Fact]
    public void Site_matches_rank_above_title_matches()
    {
        Entry[] vault = [Login("My github notes", "notes.example.com"), Login("Work account", "https://github.com")];
        var matches = AutoTypeMatcher.Find(vault, "My github notes on GitHub");
        Assert.Equal("Work account", matches[0].Entry.Title);
        Assert.Equal(2, matches.Count);
    }
}
