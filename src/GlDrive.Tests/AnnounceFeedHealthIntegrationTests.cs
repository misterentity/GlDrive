using GlDrive.Config;
using GlDrive.Irc;
using GlDrive.Services;
using Xunit;

namespace GlDrive.Tests;

public sealed class AnnounceFeedHealthIntegrationTests
{
    [Theory]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    public void DisabledSupervisorServerIrcOrAutojoinCannotMonitorOrRecover(bool health, bool server, bool irc, bool autoJoin)
    {
        var config = new AppConfig { Health = new() { Enabled = health }, Servers =
            [new() { Id = "site-a", Enabled = server, Irc = new() { Enabled = irc, Channels = [new() { Name = "#feed", AutoJoin = autoJoin }] } }] };
        Assert.False(AnnounceFeedHealthIntegration.IsMonitored(config, "site-a", "#feed"));
    }

    [Fact]
    public void RemovedChannelsAndServersCannotReusePersistedRecoveryState()
    {
        var config = new AppConfig { Servers = [new() { Id = "site-a", Enabled = true, Irc = new()
            { Enabled = true, Channels = [new() { Name = "#feed", AutoJoin = true }] } }] };
        Assert.True(AnnounceFeedHealthIntegration.IsMonitored(config, "site-a", "#FEED"));
        Assert.False(AnnounceFeedHealthIntegration.IsMonitored(config, "site-b", "#feed"));
        Assert.False(AnnounceFeedHealthIntegration.IsMonitored(config, "site-a", "#removed"));
        config.Servers[0].Irc.Channels.Clear();
        Assert.False(AnnounceFeedHealthIntegration.IsMonitored(config, "site-a", "#feed"));
    }

    [Fact]
    public void BotDiagnosisUsesOnlyEnabledConfiguredIdentityAndStripsNickModes()
    {
        var configured = new IrcChannelConfig { Name = "#feed", Sitebot = new() { BotNick = "fixtureBot", Enabled = true } };
        var joined = new IrcChannel { Name = "#feed", Nicks = ["@+FixtureBot", "other"] };
        Assert.True(AnnounceFeedHealthIntegration.Observation("site-a", configured, true, joined, 0).TrustedBotPresent);
        joined.Nicks.Clear();
        Assert.False(AnnounceFeedHealthIntegration.Observation("site-a", configured, true, joined, 0).TrustedBotPresent);
        configured.Sitebot.Enabled = false;
        Assert.Null(AnnounceFeedHealthIntegration.Observation("site-a", configured, true, joined, 0).TrustedBotPresent);
        Assert.Null(AnnounceFeedHealthIntegration.Observation("site-a", configured, true, null, 0).TrustedBotPresent);
    }
}
