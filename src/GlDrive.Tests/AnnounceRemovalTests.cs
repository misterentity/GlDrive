using System.IO;
using System.Reflection;
using GlDrive.Config;
using GlDrive.Irc;
using GlDrive.Spread;
using GlDrive.Tls;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// A section/release regex also matches DELETE messages. Exercise the subscribed
/// listener through the IRC receive path, including formatting and deduplication.
/// </summary>
public sealed class AnnounceRemovalTests : IDisposable
{
    private const string Release = "Fixture.Show.S01E01.1080p.WEB.H264-TEST";
    private readonly string _serverId = "announce-removal-test-" + Guid.NewGuid().ToString("N");
    private readonly IrcService _service;
    private readonly IrcAnnounceListener _listener;
    private readonly List<(string Section, string Release, bool AutoRace)> _announces = [];

    public AnnounceRemovalTests()
    {
        _service = new IrcService(new ServerConfig { Id = _serverId },
            new CertificateManager(Path.Combine(Path.GetTempPath(), _serverId + ".json")));
        _listener = new IrcAnnounceListener(_serverId, _service,
            [new IrcAnnounceRule
            {
                Channel = "#fixture", Enabled = true, AutoRace = true,
                Pattern = @"(?<section>-\w+-)\s*(?<release>\S+)"
            }], defaultAutoRace: true);
        _listener.ReleaseAnnounced += (_, section, release, autoRace) =>
            _announces.Add((section, release, autoRace));
    }

    [Theory]
    [InlineData("DELETE:")]
    [InlineData("deleted :")]
    [InlineData("DELDIR:")]
    [InlineData("RMDIR:")]
    [InlineData("DELPRE:")]
    [InlineData("NUKE:")]
    [InlineData("NUKED:")]
    [InlineData("[ DELETE ]")]
    [InlineData("[ NUKED ]")]
    public void Removal_event_does_not_launch_a_race_through_a_loose_custom_rule(string prefix)
    {
        Receive($"{prefix} -TV- {Release} has been deleted by fixture");

        Assert.Empty(_announces);
    }

    [Theory]
    [InlineData("PRIVMSG")]
    [InlineData("NOTICE")]
    public void Removal_does_not_deduplicate_a_later_real_new_announce(string command)
    {
        // The live failures used this DELETE shape. Formatting is stripped by IrcService.
        Receive($"\u0002DELETE:\u0002 -TV- {Release} has been deleted by fixture", command);
        Assert.Empty(_announces);

        Receive($"NEW RELEASE: -TV- {Release} by fixture", command);

        Assert.Equal(("-TV-", Release, true), Assert.Single(_announces));
    }

    [Fact]
    public void Builtin_pattern_cannot_reinterpret_a_removal_event_as_new()
    {
        Receive($"DELETE: [ NEW ] in [ tv-hd ] {Release}");

        Assert.Empty(_announces);
    }

    [Theory]
    [InlineData("Delete.Me.2026.1080p.WEB.H264-TEST")]
    [InlineData("Nuke.2026.1080p.WEB.H264-TEST")]
    [InlineData("NUKED.2026.1080p.WEB.H264-TEST")]
    [InlineData("The.Deleted.2026.1080p.WEB.H264-TEST")]
    public void Removal_words_inside_release_names_remain_valid(string release)
    {
        Receive($"NEW RELEASE: -TV- {release} by fixture");
        Receive($"[ NEW ] in [ tv-hd ] {release} OK pred 2s ago.");

        Assert.Equal(2, _announces.Count);
        Assert.All(_announces, a => Assert.Equal(release, a.Release));
        Assert.All(_announces, a => Assert.True(a.AutoRace));
    }

    [Fact]
    public void Bare_custom_announce_is_still_supported()
    {
        Receive($"-TV- {Release}");

        Assert.Equal(("-TV-", Release, true), Assert.Single(_announces));
    }

    private void Receive(string text, string command = "PRIVMSG")
    {
        var message = IrcMessage.Parse($":fixture!bot@localhost {command} #fixture :{text}");
        typeof(IrcService).GetMethod(command == "NOTICE" ? "HandleNotice" : "HandlePrivmsg",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_service, [message]);
    }

    public void Dispose()
    {
        _listener.Dispose();
        _service.Dispose();
        // Only the uniquely named fixture store; no live IRC state is changed.
        var path = Path.Combine(ConfigManager.AppDataPath, $"pm-history-{_serverId}.json");
        File.Delete(path);
        File.Delete(path + ".bak");
    }
}
