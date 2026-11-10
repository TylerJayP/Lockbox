using Lockbox.Core.Web;
using static Lockbox.Core.Web.WebSite;

namespace Lockbox.Tests;

public class WebSiteTests
{
    [Theory]
    [InlineData("www.bbc.co.uk", "bbc.co.uk")]
    [InlineData("accounts.google.com", "google.com")]
    [InlineData("alice.github.io", "alice.github.io")]
    [InlineData("my-shop.netlify.app", "my-shop.netlify.app")]
    [InlineData("a.b.example.com", "example.com")]
    [InlineData("WWW.Example.COM.", "example.com")]
    [InlineData("www.city.kawasaki.jp", "city.kawasaki.jp")]       // wildcard + exception rules (official PSL test case)
    [InlineData("www.test.kawasaki.jp", "www.test.kawasaki.jp")]
    [InlineData("münchen.de", "xn--mnchen-3ya.de")]                // international names compare as punycode
    public void Registrable_domains(string host, string expected) => Assert.Equal(expected, PublicSuffix.RegistrableDomain(host));

    [Theory]
    [InlineData("github.io")]
    [InlineData("co.uk")]
    [InlineData("localhost")]
    [InlineData("192.168.1.1")]
    [InlineData("[::1]")]
    public void No_registrable_domain(string host) => Assert.Null(PublicSuffix.RegistrableDomain(host));

    [Theory]
    [InlineData("https://github.com", "https://github.com/login", Match.SameHost)]
    [InlineData("github.com", "https://github.com/session", Match.SameHost)]
    [InlineData("https://accounts.google.com", "https://mail.google.com/", Match.SameSite)]
    [InlineData("https://alice.github.io", "https://evil.github.io/login", Match.None)]
    [InlineData("https://chase.com", "https://chase.com.evil.net/", Match.None)]
    [InlineData("https://chase.com", "https://chase-login.com/", Match.None)]
    [InlineData("http://192.168.1.1", "http://192.168.1.1/admin", Match.SameHost)]
    [InlineData("http://192.168.1.1", "http://192.168.1.2/admin", Match.None)]
    [InlineData("http://localhost:8080", "http://localhost:3000/", Match.None)]
    [InlineData("http://localhost:8080", "http://localhost:8080/login", Match.SameHost)]
    [InlineData("https://example.com:8443", "https://example.com/", Match.None)]
    [InlineData("android://hash@com.spotify.music/", "https://spotify.com/", Match.None)]
    [InlineData("", "https://github.com/", Match.None)]
    [InlineData("https://github.com", "chrome://settings", Match.None)]
    [InlineData("https://github.com", "file:///C:/login.html", Match.None)]
    public void Which_logins_belong_on_a_page(string saved, string page, Match expected) => Assert.Equal(expected, Compare(saved, page));

    [Fact]
    public void Parse_reports_whether_a_page_is_secure()
    {
        Assert.True(Parse("https://github.com/login")!.IsSecure);
        Assert.False(Parse("http://neverssl.com")!.IsSecure);
        Assert.Equal("https://github.com", Parse("https://github.com/login?x=1")!.Origin);
        Assert.Equal("http://localhost:8080", Parse("http://localhost:8080/a")!.Origin);
    }

    [Fact]
    public async Task Native_messages_round_trip()
    {
        using var ms = new MemoryStream();
        await NativeMessage.WriteAsync(ms, """{"type":"status","note":"héllo ✓"}""");
        await NativeMessage.WriteAsync(ms, "{}");
        ms.Position = 0;
        int utf8Length = System.Text.Encoding.UTF8.GetByteCount("""{"type":"status","note":"héllo ✓"}""");
        Assert.Equal(new byte[] { (byte)utf8Length, 0, 0, 0 }, ms.ToArray()[..4]);   // little-endian length of the UTF-8 bytes
        Assert.Equal("""{"type":"status","note":"héllo ✓"}""", await NativeMessage.ReadAsync(ms));
        Assert.Equal("{}", await NativeMessage.ReadAsync(ms));
        Assert.Null(await NativeMessage.ReadAsync(ms));
    }

    [Fact]
    public async Task Oversized_or_cut_off_messages_are_rejected()
    {
        using var huge = new MemoryStream([0, 0, 0, 0x40]);
        await Assert.ThrowsAsync<InvalidDataException>(() => NativeMessage.ReadAsync(huge));
        using var cut = new MemoryStream([10, 0, 0, 0, (byte)'{']);
        await Assert.ThrowsAsync<EndOfStreamException>(() => NativeMessage.ReadAsync(cut));
    }
}
