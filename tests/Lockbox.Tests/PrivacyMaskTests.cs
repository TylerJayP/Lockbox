using Lockbox.Core.Tools;

namespace Lockbox.Tests;

public class PrivacyMaskTests
{
    [Theory]
    [InlineData("tyler@example.com", "ty•••@example.com")]
    [InlineData("al@gmail.com", "a•••@gmail.com")]
    [InlineData("first.last+shop@outlook.com", "fi•••@outlook.com")]
    [InlineData("TylerJayP", "Ty•••yP")]
    [InlineData("tylerjp1", "ty•••p1")]
    [InlineData("tylerjp2", "ty•••p2")]
    [InlineData("tyler#0001", "ty•••01")]
    [InlineData("8015551234", "80•••34")]
    [InlineData("admin", "ad•••")]
    [InlineData("bob", "b•••")]
    [InlineData("  spaced  ", "sp•••ed")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Hides_most_of_a_username(string? input, string expected) => Assert.Equal(expected, PrivacyMask.Username(input));

    [Fact]
    public void Never_splits_an_emoji_or_accented_letter()
    {
        Assert.Equal("🦊é•••", PrivacyMask.Username("🦊é1x"));
        Assert.Equal("😀•••", PrivacyMask.Username("😀ab"));
    }

    [Fact]
    public void Masked_text_never_contains_the_middle_of_the_name()
    {
        string masked = PrivacyMask.Username("secretname@example.com");
        Assert.DoesNotContain("cretna", masked);
        Assert.StartsWith("se", masked);
    }
}
