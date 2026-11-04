using GlDrive.Config;
using GlDrive.Irc;
using Xunit;

namespace GlDrive.Tests;

public class ServerInfoShareTests
{
    private static ServerConfig Site() => new()
    {
        Name = "siteb",
        Connection = new ConnectionConfig { Host = "bnc.example.net", Port = 31337, Username = "dave" },
        Irc = new IrcConfig
        {
            Host = "irc.example.net",
            Port = 7000,
            UseTls = true,
            Nick = "dave",
            InviteNick = "SiteBot",
            Channels =
            [
                new IrcChannelConfig { Name = "#siteb", Key = "chankey" },
                new IrcChannelConfig { Name = "#siteb-spam" },
            ],
        },
    };

    private static readonly ServerInfoSecrets Secrets = new(
        "ftppass",
        "ircpass",
        new Dictionary<string, FishKeyEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["#siteb"] = new() { Key = "fishkey", Mode = FishMode.CBC },
            ["#siteb-spam"] = new() { Key = "ecbkey", Mode = FishMode.ECB },
            ["somenick"] = new() { Key = "pmkey", Mode = FishMode.CBC },
        });

    [Fact]
    public void DefaultsShareBncIrcdAndChannelSecretsButNotTheFtpLogin()
    {
        var lines = ServerInfoShare.BuildLines(Site(), new ServerInfoShareOptions(), Secrets);

        Assert.Equal(
        [
            "[siteb]",
            "BNC: bnc.example.net:31337",
            "IRC: irc.example.net:+7000 (SSL)",
            "IRC pass: ircpass",
            "Invite bot: SiteBot",
            "#siteb (key: chankey, blowfish: cbc:fishkey)",
            "#siteb-spam (blowfish: ecbkey)",
        ], lines);
        Assert.DoesNotContain(lines, l => l.Contains("ftppass") || l.Contains("pmkey"));
    }

    [Fact]
    public void FtpLoginIsIncludedOnlyWhenAsked()
    {
        var lines = ServerInfoShare.BuildLines(Site(), new ServerInfoShareOptions { FtpLogin = true }, Secrets);
        Assert.Contains("Login: dave / ftppass", lines);
    }

    [Fact]
    public void UncheckedSecretsAreOmitted()
    {
        var options = new ServerInfoShareOptions { IrcPassword = false, FishKeys = false };
        var lines = ServerInfoShare.BuildLines(Site(), options, Secrets);

        Assert.DoesNotContain(lines, l => l.Contains("ircpass") || l.Contains("fishkey") || l.Contains("ecbkey"));
        Assert.Contains("#siteb (key: chankey)", lines);
        Assert.Contains("#siteb-spam", lines);
    }

    [Fact]
    public void PlainIrcAndMissingSecretsStayClean()
    {
        var site = Site();
        site.Irc.UseTls = false;
        site.Irc.InviteNick = "";
        var lines = ServerInfoShare.BuildLines(site, new ServerInfoShareOptions { Channels = false, FtpLogin = true },
            new ServerInfoSecrets(null, null, null));

        Assert.Equal(["[siteb]", "BNC: bnc.example.net:31337", "Login: dave", "IRC: irc.example.net:7000"], lines);
    }

    [Fact]
    public void SensitiveSendsLogOnlyTheCommandAndTarget()
    {
        Assert.Equal("PRIVMSG somenick :<redacted 16 chars>", IrcClient.RedactPayload("PRIVMSG somenick :IRC pass: secret"));
        Assert.Equal("<redacted>", IrcClient.RedactPayload("garbage"));
    }
}
