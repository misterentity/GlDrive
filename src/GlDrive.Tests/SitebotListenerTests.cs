using System.IO;
using System.Reflection;
using GlDrive.Config;
using GlDrive.Irc;
using GlDrive.Spread;
using GlDrive.Tls;
using Xunit;

namespace GlDrive.Tests;

public sealed class SitebotListenerTests : IDisposable
{
    private const string Release = "Fixture.Show.S01E02.1080p.WEB.H264-GRP";
    private readonly IrcService _service;
    private readonly IrcAnnounceListener _listener;
    private readonly SitebotChannelConfig _config = new() { BotNick = "fixtureBot", UserHostPattern = "bot@*.example" };
    private readonly List<SitebotEvent> _events = [];
    private readonly List<string> _announces = [];
    private readonly string _serverId = "sitebot-test-" + Guid.NewGuid().ToString("N");

    public SitebotListenerTests()
    {
        _service = new IrcService(new ServerConfig { Id = _serverId },
            new CertificateManager(Path.Combine(Path.GetTempPath(), _serverId + ".json")));
        _listener = new IrcAnnounceListener(_serverId, _service,
            [new IrcAnnounceRule { Channel = "#fixture", Pattern = @"(?<section>-TV-)\s+(?<release>\S+)" }], true,
            [new IrcChannelConfig { Name = "#fixture", Sitebot = _config }]);
        _listener.SitebotEventReceived += e => _events.Add(e);
        _listener.ReleaseAnnounced += (_, _, release, _) => _announces.Add(release);
    }

    [Theory]
    [InlineData("PRIVMSG")]
    [InlineData("NOTICE")]
    public void Receive_propagates_host_and_typed_completion_without_starting_a_race(string command)
    {
        Receive($"\u0002COMPLETE:\u0002 -TV- {Release} - 700MB/15F - Solo by racerOne", command);
        var observation = Assert.Single(_events);
        Assert.Equal("bot@host.example", observation.UserHost);
        Assert.Equal(SitebotEventKind.Complete, observation.Kind);
        Assert.Equal("racerOne", observation.Winner);
        Assert.Empty(_announces);
    }

    [Theory]
    [InlineData("NUKE")]
    [InlineData("UNNUKE")]
    [InlineData("HALFWAY")]
    [InlineData("RACE")]
    [InlineData("TV-MAZE")]
    public void Nonnew_events_do_not_leak_through_broad_legacy_rule(string label)
    {
        Receive($"{label}: -TV- {Release} leader: racerOne");
        Assert.Single(_events);
        Assert.Empty(_announces);
    }

    [Fact]
    public void Real_new_emits_both_surfaces_and_dedupes_only_legacy_new()
    {
        Receive($"NEW RELEASE: -TV- {Release} by racerOne");
        Receive($"NEW RELEASE: -TV- {Release} by racerOne");
        Assert.Equal(2, _events.Count);
        Assert.Single(_announces);
        Assert.All(_events, e => Assert.Equal(SitebotEventKind.New, e.Kind));
    }

    [Theory]
    [InlineData("spoof!bot@host.example", "#fixture")]
    [InlineData("fixtureBot!bot@wrong.invalid", "#fixture")]
    [InlineData("fixtureBot!bot@host.example", "somebody")]
    [InlineData("fixtureBot!bot@host.example", "#other")]
    public void Untrusted_sender_or_wrong_channel_cannot_emit_typed_events(string prefix, string channel)
    {
        Receive($"COMPLETE: -TV- {Release} - 700MB/15F", prefix: prefix, channel: channel);
        Assert.Empty(_events);
        Assert.Empty(_announces);
    }

    [Fact]
    public void Configured_trust_protects_legacy_new_and_disabled_sensor_retains_legacy_behavior()
    {
        Receive($"NEW RELEASE: -TV- {Release} by racerOne", prefix: "spoof!bot@host.example");
        Assert.Empty(_events);
        Assert.Empty(_announces);
        _config.Enabled = false;
        Receive($"NEW RELEASE: -TV- {Release} by racerOne", prefix: "spoof!bot@host.example");
        Assert.Empty(_events);
        Assert.Single(_announces);
    }

    private void Receive(string text, string command = "PRIVMSG", string prefix = "fixtureBot!bot@host.example", string channel = "#fixture")
    {
        var message = IrcMessage.Parse($":{prefix} {command} {channel} :{text}");
        typeof(IrcService).GetMethod(command == "NOTICE" ? "HandleNotice" : "HandlePrivmsg",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_service, [message]);
    }

    public void Dispose()
    {
        _listener.Dispose();
        _service.Dispose();
        var path = Path.Combine(ConfigManager.AppDataPath, $"pm-history-{_serverId}.json");
        File.Delete(path);
        File.Delete(path + ".bak");
    }
}
