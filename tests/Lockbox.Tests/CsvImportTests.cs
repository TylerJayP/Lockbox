using Lockbox.Core.Export;
using Lockbox.Core.Import;
using Lockbox.Core.Model;

namespace Lockbox.Tests;

public class CsvImportTests
{
    private static List<ImportCandidate> Import(string csv, string? expectSource = null)
    {
        var result = CsvImporter.TryParse(csv);
        Assert.NotNull(result);
        if (expectSource != null) Assert.Equal(expectSource, result!.Source);
        return result!.Candidates;
    }

    [Fact]
    public void Chrome_and_Edge()
    {
        var rows = Import(""""
            name,url,username,password,note
            github.com,https://github.com/login,TylerJayP,"Vq7#pL2!x,Rz9",
            accounts.google.com,https://accounts.google.com/,me@gmail.com,"has ""quotes""","line one
            line two"
            github.com,https://github.com/session,TylerJayP,"Vq7#pL2!x,Rz9",
            ,android://abc123==@com.spotify.music/,me@gmail.com,sp0tify!,
            """", "Chrome, Edge or Brave");

        Assert.Equal(4, rows.Count);
        Assert.Equal(("github.com", "TylerJayP", "Vq7#pL2!x,Rz9"), (rows[0].Title, rows[0].Username, rows[0].Password));
        Assert.Equal("has \"quotes\"", rows[1].Password);
        Assert.Equal("line one\nline two", rows[1].Notes);
        Assert.True(rows[2].SuggestSkip);                                   // same login exported twice
        Assert.False(rows[0].SuggestSkip);
        Assert.Equal("Spotify (Android app)", rows[3].Title);
        Assert.Contains(rows[3].Warnings, w => w.Contains("Android"));
        Assert.All(rows, r => Assert.Equal(ItemType.Login, r.Type));
    }

    [Fact]
    public void Firefox_titles_come_from_the_site()
    {
        var rows = Import("""
            "url","username","password","httpRealm","formActionOrigin","guid","timeCreated","timeLastUsed","timePasswordChanged"
            "https://www.netflix.com","tyler@example.com","hunter2",,"https://www.netflix.com","{a1}","1700000000000","1700000000000","1700000000000"
            "https://store.steampowered.com","tylerjp","St3am!",,"https://store.steampowered.com","{a2}","1","1","1"
            "https://www.bbc.co.uk","t@example.com","bbc-pass",,"","{a3}","1","1","1"
            """, "Firefox");

        Assert.Equal(["netflix.com", "steampowered.com", "bbc.co.uk"], rows.Select(r => r.Title));
        Assert.Equal("https://www.netflix.com", rows[0].Url);
    }

    [Fact]
    public void Bitwarden_export_with_folders_totp_fields_and_notes()
    {
        var rows = Import("""
            folder,favorite,type,name,notes,fields,reprompt,login_uri,login_username,login_password,login_totp
            Gaming,1,login,Steam,,"Steam Guard: on
            Security answer: Fluffy",0,https://store.steampowered.com,tylerjp,St3am!,JBSWY3DPEHPK3PXP
            ,,note,Locker combo,12-34-56,,1,,,,
            """, "Bitwarden or Lockbox");

        var steam = rows[0];
        Assert.Equal(["Gaming"], steam.Tags);
        Assert.True(steam.TwoFactorEnabled);
        Assert.Equal(TwoFactorMethod.AuthenticatorApp, steam.TwoFactorMethod);
        Assert.Contains(steam.Fields, f => f is { Name: CsvImporter.TotpFieldName, Value: "JBSWY3DPEHPK3PXP", Hidden: true });
        Assert.Contains(steam.Fields, f => f is { Name: "Steam Guard", Value: "on", Hidden: false });
        Assert.Contains(steam.Fields, f => f is { Name: "Security answer", Value: "Fluffy", Hidden: true });

        var note = rows[1];
        Assert.Equal(ItemType.SecureNote, note.Type);
        Assert.Equal("12-34-56", note.Notes);
        Assert.True(note.Reprompt);
        Assert.Equal("", note.ToEntry().Password);
    }

    [Fact]
    public void Lockbox_export_round_trips_every_item_type()
    {
        var github = new Entry
        {
            Title = "GitHub", Url = "https://github.com", Username = "TylerJayP", Password = "Vq7#pL2!xRz9@mK4", Notes = "work account",
            TwoFactorEnabled = true, TwoFactorMethod = TwoFactorMethod.HardwareKey, Tags = ["dev", "work"], Reprompt = true,
            Fields = [new CustomField("Recovery email", "me@example.com", false)],
        };
        var card = new Entry
        {
            Type = ItemType.Card, Title = "Visa", Tags = ["money"],
            Card = new CardData { CardholderName = "TYLER PERKINS", Number = "4111111111111111", Expiry = "08/29", SecurityCode = "123", Pin = "9999" },
        };
        var wifi = new Entry
        {
            Type = ItemType.WiFi, Title = "Home Wi-Fi", Password = "correct horse",
            Wifi = new WifiData { NetworkName = "Perkins-5G", Security = WifiSecurity.Wpa3, HiddenNetwork = true },
        };
        var note = new Entry { Type = ItemType.SecureNote, Title = "Safe", Notes = "left 3, right 7" };
        Entry[] originals = [github, card, wifi, note];

        string csv = CsvExport.ToBitwardenCsv(originals, e => e.Title == "GitHub" ? "1111 2222\n3333 4444" : null);
        var back = Import(csv).ToDictionary(c => c.Title);

        var g = back["GitHub"];
        var ge = g.ToEntry();
        Assert.Equal((github.Url, github.Username, github.Password, github.Notes), (ge.Url, ge.Username, ge.Password, ge.Notes));
        Assert.Equal(TwoFactorMethod.HardwareKey, ge.TwoFactorMethod);
        Assert.Equal(["dev", "work"], ge.Tags);
        Assert.True(ge.Reprompt);
        Assert.Equal([new CustomField("Recovery email", "me@example.com", false)], ge.Fields);
        Assert.Equal("1111 2222\n3333 4444", g.BackupCodes);

        var ce = back["Visa"].ToEntry();
        Assert.Equal(ItemType.Card, ce.Type);
        Assert.Equal(card.Card, ce.Card);
        Assert.Equal(["money"], ce.Tags);
        Assert.Empty(ce.Fields);

        var we = back["Home Wi-Fi"].ToEntry();
        Assert.Equal(ItemType.WiFi, we.Type);
        Assert.Equal(wifi.Wifi, we.Wifi);
        Assert.Equal("correct horse", we.Password);
        Assert.Equal("", we.Username);

        var ne = back["Safe"].ToEntry();
        Assert.Equal((ItemType.SecureNote, "left 3, right 7"), (ne.Type, ne.Notes));
    }

    [Fact]
    public void LastPass_logins_and_card_notes()
    {
        var rows = Import("""
            url,username,password,totp,extra,name,grouping,fav
            https://www.amazon.com,tyler@example.com,Zx9!kQ,,,Amazon,Shopping,0
            http://sn,,,,"NoteType:Credit Card
            Language:en-US
            Name on Card:TYLER PERKINS
            Type:Visa
            Number:4111111111111111
            Security Code:123
            Start Date:,
            Expiration Date:January,2027
            Notes:",My Visa,(none),0
            """, "LastPass");

        Assert.Equal(["Shopping"], rows[0].Tags);
        var card = rows[1];
        Assert.Equal(ItemType.Card, card.Type);
        Assert.Equal("My Visa", card.Title);
        Assert.Equal(new CardData { CardholderName = "TYLER PERKINS", Number = "4111111111111111", Expiry = "01/27", SecurityCode = "123" }, card.Card);
        Assert.Empty(card.Tags);
    }

    [Fact]
    public void KeePassXC_1Password_and_Apple()
    {
        var kp = Import("""
            "Group","Title","Username","Password","URL","Notes","TOTP","Icon","Last Modified","Created"
            "Root/Email","Gmail","me@gmail.com","g-pass","https://mail.google.com","","otpauth://totp/x?secret=ABC","0","2026-01-01","2026-01-01"
            "Root","Bank","tyler","b-pass","","","","0","2026-01-01","2026-01-01"
            """, "KeePassXC");
        Assert.Equal(["Email"], kp[0].Tags);
        Assert.Empty(kp[1].Tags);
        Assert.True(kp[0].TwoFactorEnabled);

        var op = Import("""
            Title,Url,Username,Password,OTPAuth,Favorite,Archived,Tags,Notes
            Discord,https://discord.com,tyler#0001,N8$wq!,,false,false,"games,chat",
            """, "1Password");
        Assert.Equal(["games", "chat"], op[0].Tags);

        var apple = Import("""
            Title,URL,Username,Password,Notes,OTPAuth
            uvu.edu (10912345),https://my.uvu.edu/,10912345,correct-Orbit,,
            """, "Safari / Apple Passwords");
        Assert.Equal("uvu.edu (10912345)", apple[0].Title);
    }

    [Fact]
    public void Semicolons_and_unknown_layouts_work()
    {
        var rows = Import("Website;Login;Password;Comments\r\nexample.com;me;pw;hi\r\n", "CSV");
        Assert.Equal(("example.com", "me", "pw", "hi"), (rows[0].Url, rows[0].Username, rows[0].Password, rows[0].Notes));
        Assert.Equal("example.com", rows[0].Title);
    }

    [Fact]
    public void Empty_rows_and_rows_with_nothing_useful_are_skipped()
    {
        var rows = Import("name,url,username,password\n\n,https://example.com,,\nx.com,https://x.com,me,\n");
        var only = Assert.Single(rows);
        Assert.Contains(only.Warnings, w => w.Contains("No password"));
    }

    [Theory]
    [InlineData("Netflix\nemail: me@example.com\npassword: hunter2")]
    [InlineData("Steam | myname | St3amPass\nAmazon - me@example.com - Amaz0n!")]
    [InlineData("name, password\nNetflix, hunter2")]
    [InlineData("")]
    public void Ordinary_text_files_are_not_mistaken_for_csv(string text)
    {
        Assert.False(CsvImporter.LooksLikeCsv(text));
        Assert.Null(CsvImporter.TryParse(text));
    }

    [Theory]
    [InlineData("https://accounts.google.com/signin", "google.com")]
    [InlineData("www.bbc.co.uk", "bbc.co.uk")]
    [InlineData("github.com", "github.com")]
    [InlineData("http://192.168.1.1/admin", "192.168.1.1")]
    [InlineData("android://hash@com.spotify.music/", "music.spotify.com")]
    [InlineData("", "")]
    public void Site_keys(string url, string expected)
    {
        if (url.StartsWith("android://")) { Assert.NotEqual("", CsvImporter.SiteKey(url)); return; }
        Assert.Equal(expected, CsvImporter.SiteKey(url));
    }
}
