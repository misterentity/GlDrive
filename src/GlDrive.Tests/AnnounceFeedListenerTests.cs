using GlDrive.Config;
using GlDrive.Irc;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

public sealed class AnnounceFeedListenerTests
{
    private const string Release = "Synthetic.Show.S01E01.1080p.WEB-TEST";
    private static IrcMessageItem Message(string text, string nick = "trusted") => new()
    {
        Timestamp = new DateTime(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc),
        Nick = nick, UserHost = "bot@host.example", Type = IrcMessageType.Normal, Text = text
    };

    [Fact]
    public void ChannelActivityRetainsBothChannelsWhileRaceDeduplicationStillRunsOnce()
    {
        using var listener = new IrcAnnounceListener("site-a", [], true);
        var channels = new List<string>();
        var races = 0;
        listener.ChannelAnnounceObserved += (_, channel, _) => channels.Add(channel);
        listener.ReleaseAnnounced += (_, _, _, _) => races++;
        var message = Message($"[ NEW ] in [ TV ] {Release}");
        listener.OnMessage("#one", message);
        listener.OnMessage("#two", message);
        listener.OnMessage("#ONE", message);
        Assert.Equal(new[] { "#one", "#two" }, channels);
        Assert.Equal(1, races);
    }

    [Theory]
    [InlineData("#releases", "spoof", "[ NEW ] in [ TV ] Synthetic.Show.S01E01.1080p.WEB-TEST")]
    [InlineData("#releases", "trusted", "[ NEW ] in [ TV ] father-to-be")]
    [InlineData("#releases", "trusted", "DELETE: [ NEW ] in [ TV ] Synthetic.Show.S01E01.1080p.WEB-TEST")]
    [InlineData("somebody", "trusted", "[ NEW ] in [ TV ] Synthetic.Show.S01E01.1080p.WEB-TEST")]
    public void RejectedMessagesCannotPretendTheAnnounceFeedIsHealthy(string channel, string nick, string text)
    {
        using var listener = new IrcAnnounceListener("site-a", [], true,
            [new IrcChannelConfig { Name = "#releases", Sitebot = new() { BotNick = "trusted", UserHostPattern = "bot@*.example" } }]);
        var activity = 0;
        listener.ChannelAnnounceObserved += (_, _, _) => activity++;
        listener.OnMessage(channel, Message(text, nick));
        Assert.Equal(0, activity);
    }

    [Theory]
    [InlineData("COMPLETE")]
    [InlineData("NUKE")]
    [InlineData("UNNUKE")]
    [InlineData("HALFWAY")]
    [InlineData("RACE")]
    [InlineData("TV-MAZE")]
    public void TrustedTypedTrafficProvesFeedLivenessWithoutStartingARace(string label)
    {
        using var listener = new IrcAnnounceListener("site-a", [], true,
            [new IrcChannelConfig { Name = "#releases", Sitebot = new() { BotNick = "trusted", UserHostPattern = "bot@*.example" } }]);
        var activity = 0;
        var races = 0;
        listener.ChannelAnnounceObserved += (_, _, _) => activity++;
        listener.ReleaseAnnounced += (_, _, _, _) => races++;
        listener.OnMessage("#releases", Message($"{label}: -TV- {Release} - 700MB/15F"));
        var later = Message($"{label}: -TV- {Release} - 700MB/15F");
        later.Timestamp = later.Timestamp.AddMinutes(1);
        listener.OnMessage("#releases", later);
        Assert.Equal(2, activity);
        Assert.Equal(0, races);
    }

    [Fact]
    public void TypedAndLegacyNewCountOneReceivedMessage()
    {
        using var listener = new IrcAnnounceListener("site-a", [], true,
            [new IrcChannelConfig { Name = "#releases", Sitebot = new() { BotNick = "trusted", UserHostPattern = "bot@*.example" } }]);
        var activity = 0;
        listener.ChannelAnnounceObserved += (_, _, _) => activity++;
        listener.OnMessage("#releases", Message($"[ NEW ] in [ TV ] {Release}"));
        Assert.Equal(1, activity);
    }

    [Fact]
    public void HealthObserverFailureCannotBreakRaceOrAnotherHealthConsumer()
    {
        using var listener = new IrcAnnounceListener("site-a", [], false);
        var activity = 0;
        var races = 0;
        listener.ChannelAnnounceObserved += (_, _, _) => throw new InvalidOperationException("isolated");
        listener.ChannelAnnounceObserved += (_, _, _) => activity++;
        listener.ReleaseAnnounced += (_, _, _, _) => races++;
        listener.OnMessage("#releases", Message($"[ NEW ] in [ TV ] {Release}"));
        Assert.Equal(1, activity);
        Assert.Equal(1, races);
    }
}
