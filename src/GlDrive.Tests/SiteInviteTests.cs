using System.IO;
using GlDrive.Config;
using GlDrive.Irc;
using Xunit;

namespace GlDrive.Tests;

public sealed class SiteInviteTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gldrive-invite-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    [Fact]
    public void Addline_text_and_masks()
    {
        Assert.Equal("site adduser user Pw123 *@1.2.3.4 *@5.6.7.*", AddlineText.Build("user", "Pw123", ["*@1.2.3.4", "*@5.6.7.*"]));
        Assert.Equal(["*@1.2.3.4", "ident@5.6.7.*"], AddlineText.ParseMasks("1.2.3.4, ident@5.6.7.* 1.2.3.4"));
        var pw = AddlineText.GeneratePassword();
        Assert.Equal(16, pw.Length);
        Assert.True(AddlineText.IsSafeToken(pw));
        Assert.False(AddlineText.IsSafeToken("has space"));
    }

    [Fact]
    public void Offer_store_round_trips_encrypted_and_remembers_last_used()
    {
        var path = Path.Combine(_dir, "offers.json");
        var store = new AddlineOfferStore(path);
        store.Record(new AddlineOffer("srv", "Bob", "user", "secret1", ["*@1.2.3.4"], DateTimeOffset.UtcNow.AddMinutes(-5)));
        store.Record(new AddlineOffer("srv", "bob", "user", "secret2", ["*@1.2.3.4"], DateTimeOffset.UtcNow));
        var reread = new AddlineOfferStore(path);
        Assert.Equal("secret2", reread.Find("srv", "BOB")!.Password);
        Assert.Null(reread.Find("other", "bob"));
        Assert.Equal("user", reread.LastUsed().Username);
        Assert.Equal(["*@1.2.3.4"], reread.LastUsed().Masks);
        Assert.DoesNotContain("secret2", File.ReadAllText(path));
    }

    [Fact]
    public void Parses_a_typical_siteop_reply()
    {
        var info = SiteInviteParser.Parse([
            "added you :)",
            "[COOLSITE]",
            "bnc: 10.0.0.5:31337 or cool.example.org:31338",
            "irc: irc.coolnet.org +6697",
            "#cool-chat key: hunter2 blowfish: cbc:AbCdEf123",
            "#cool-spam (blowfish: plainkey)",
            "invite bot: CoolBot",
        ]);
        Assert.Equal("COOLSITE", info.Name);
        Assert.Equal("10.0.0.5", info.FtpHost);
        Assert.Equal(31337, info.FtpPort);
        Assert.Equal(["cool.example.org:31338"], info.AlternateFtp);
        Assert.Equal("irc.coolnet.org", info.IrcHost);
        Assert.Equal(6697, info.IrcPort);
        Assert.True(info.IrcTls);
        Assert.Equal("CoolBot", info.InviteBot);
        Assert.Equal(new SiteInviteChannel("#cool-chat", "hunter2", "cbc:AbCdEf123"), info.Channels[0]);
        Assert.Equal(new SiteInviteChannel("#cool-spam", null, "plainkey"), info.Channels[1]);
        Assert.Null(info.Username);
    }

    [Fact]
    public void Parses_gldrive_send_server_info_format_with_login()
    {
        var info = SiteInviteParser.Parse([
            "[sited]", "BNC: 192.168.50.86:2010", "Login: newuser / s3cret", "IRC: irc.example.net:+7000 (SSL)",
            "IRC pass: ircpw", "Invite bot: Zbot", "#chan1 (key: k1, blowfish: cbc:Fishy)",
        ]);
        Assert.Equal(("192.168.50.86", 2010), (info.FtpHost, info.FtpPort));
        Assert.Equal(("newuser", "s3cret"), (info.Username, info.Password));
        Assert.Equal(("irc.example.net", 7000, true), (info.IrcHost, info.IrcPort, info.IrcTls));
        Assert.Equal("ircpw", info.IrcPassword);
        Assert.Equal(new SiteInviteChannel("#chan1", "k1", "cbc:Fishy"), Assert.Single(info.Channels));
    }

    [Fact]
    public void Draft_uses_the_sent_login_and_is_not_mounted()
    {
        var info = SiteInviteParser.Parse(["site: 10.0.0.5:31337", "irc.coolnet.org:6697", "#a (blowfish: cbc:K1)", "#b key: j blowfish: K2"]);
        var offer = new AddlineOffer("srv", "bob", "user", "secretpw", ["*@1.2.3.4"], DateTimeOffset.UtcNow);
        var draft = SiteInviteServer.Build(info, offer, "bob", "user");
        var s = draft.Server;
        Assert.Equal(("10.0.0.5", 31337, "user"), (s.Connection.Host, s.Connection.Port, s.Connection.Username));
        Assert.Equal("secretpw", draft.Password);
        Assert.False(s.Mount.MountDrive);
        Assert.True(s.Irc.Enabled);
        Assert.Equal("[cbc:K1]", s.Irc.Channels[0].Key);
        Assert.Equal("j", s.Irc.Channels[1].Key);
        Assert.Equal(("#b", "K2", FishMode.ECB), Assert.Single(draft.FishKeys));
    }

    [Fact]
    public void New_servers_are_not_mounted_as_drives_by_default() => Assert.False(new ServerConfig().Mount.MountDrive);

    [Fact]
    public void Prose_without_an_address_finds_no_host()
    {
        var info = SiteInviteParser.Parse(["hey thanks, will add you later today", "version 2.0 is out"]);
        Assert.False(info.HasFtp);
    }

    [Theory]
    [InlineData("grp-show.nfo 12 KB")]
    [InlineData("[NEW] TV-HD :: Show.Name.S01E01.WEB 1080p by GRP")]
    [InlineData("we have 3 boxes in total, around 200 slots")]
    [InlineData("speed was example.com 50 mb/s today")]
    public void Announce_and_file_lines_are_not_site_addresses(string line) =>
        Assert.False(SiteInviteParser.Parse([line]).HasFtp);

    [Theory]
    [InlineData("host 10.0.0.5 port 31337", "10.0.0.5", 31337)]
    [InlineData("10.0.0.5 31337", "10.0.0.5", 31337)]
    [InlineData("site: cool.example.org 1234", "cool.example.org", 1234)]
    [InlineData("cool.example.org:1234", "cool.example.org", 1234)]
    public void Labelled_or_ip_addresses_are_found(string line, string host, int port)
    {
        var info = SiteInviteParser.Parse([line]);
        Assert.Equal((host, port), (info.FtpHost, info.FtpPort));
    }
}
