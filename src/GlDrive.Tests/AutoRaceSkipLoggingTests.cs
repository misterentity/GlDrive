using System.IO;
using System.Reflection;
using GlDrive.Config;
using GlDrive.Ftp;
using GlDrive.Spread;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace GlDrive.Tests;

[CollectionDefinition("Auto-race skip logger", DisableParallelization = true)]
public sealed class AutoRaceSkipLoggerCollection { }

/// <summary>
/// v3.19.4 — while zephyr and SYN were down, every announce produced the same WRN
/// ("only 1 connected spread pool(s) ... check login cap"): 355 of 360 warnings in 9.5 h,
/// none naming the sites actually missing.
/// </summary>
[Collection("Auto-race skip logger")]
public sealed class AutoRaceSkipLoggingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "gldrive-autorace-skip-" + Guid.NewGuid().ToString("N"));
    private readonly ILogger _previousLogger = Log.Logger;
    private readonly CaptureSink _sink = new();
    private readonly Logger _logger;
    private readonly List<string> _attempts = [];
    private readonly SpreadManager _manager;

    public AutoRaceSkipLoggingTests()
    {
        Directory.CreateDirectory(_directory);
        _logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(_sink).CreateLogger();
        Log.Logger = _logger;
        var config = new AppConfig
        {
            Servers =
            [
                Site("bnc", "superbnc"), Site("zep", "zephyr"), Site("syn", "SYN"),
                new() { Id = "off", Name = "disabled-site", Enabled = false, SpreadSite = new() { Sections = { ["TV"] = "/tv" } } },
            ]
        };
        config.Spread.AutoRaceOnNotification = true;
        // A race that gets past the skip stops at pure skiplist evaluation before any FTP command.
        config.Spread.GlobalSkiplist.Add(new() { Pattern = "*", Action = SkiplistAction.Deny });
        _manager = new SpreadManager(config, _directory);
        Pools().Add("bnc", new FtpConnectionPool(null!));
        _manager.AutoRaceAttempted += (_, _, result) => _attempts.Add(result);
    }

    public void Dispose()
    {
        Log.Logger = _previousLogger;
        _logger.Dispose();
        _manager.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private static ServerConfig Site(string id, string name) =>
        new() { Id = id, Name = name, Enabled = true, SpreadSite = new() { Sections = { ["TV"] = "/tv" } } };

    private Dictionary<string, FtpConnectionPool> Pools() => (Dictionary<string, FtpConnectionPool>)typeof(SpreadManager)
        .GetField("_spreadPools", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_manager)!;

    private List<LogEvent> Skips(LogEventLevel level) => _sink.Events
        .Where(e => e.Level == level && e.MessageTemplate.Text.StartsWith("Auto-race skipped for", StringComparison.Ordinal)).ToList();

    [Fact]
    public void Peer_outage_warns_once_naming_unavailable_sites_then_logs_repeats_at_debug()
    {
        for (var i = 0; i < 5; i++) _manager.TryAutoRace("TV", $"Show.S01E0{i}.1080p.WEB.h264-GRP");

        var warning = Assert.Single(Skips(LogEventLevel.Warning));
        var text = warning.RenderMessage();
        Assert.Contains("zephyr", text);
        Assert.Contains("SYN", text);
        Assert.DoesNotContain("disabled-site", text);
        Assert.DoesNotContain("superbnc", text);
        Assert.DoesNotContain("check login cap", text);
        Assert.Equal(4, Skips(LogEventLevel.Debug).Count);
        Assert.Equal(5, _attempts.Count);
        Assert.All(_attempts, a => Assert.Contains("zephyr", a));
    }

    [Fact]
    public void Change_in_unavailable_sites_warns_again()
    {
        _manager.TryAutoRace("TV", "Show.S01E01.1080p.WEB.h264-GRP");
        Pools().Add("syn", new FtpConnectionPool(null!));
        Pools().Remove("bnc");
        _manager.TryAutoRace("TV", "Show.S01E02.1080p.WEB.h264-GRP");

        Assert.Equal(2, Skips(LogEventLevel.Warning).Count);
    }

    [Fact]
    public void Recovery_rearms_so_the_next_outage_warns_immediately()
    {
        _manager.TryAutoRace("TV", "Show.S01E01.1080p.WEB.h264-GRP");
        Pools().Add("zep", new FtpConnectionPool(null!));
        _manager.TryAutoRace("TV", "Show.S01E02.1080p.WEB.h264-GRP");
        Pools().Remove("zep");
        _manager.TryAutoRace("TV", "Show.S01E03.1080p.WEB.h264-GRP");

        Assert.Equal(2, Skips(LogEventLevel.Warning).Count);
    }

    [Fact]
    public void No_live_pool_keeps_the_login_cap_hint()
    {
        Pools().Clear();
        _manager.TryAutoRace("TV", "Show.S01E01.1080p.WEB.h264-GRP");

        Assert.Contains("check login cap", Assert.Single(Skips(LogEventLevel.Warning)).RenderMessage());
    }

    private sealed class CaptureSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];
        public void Emit(LogEvent logEvent) { lock (Events) Events.Add(logEvent); }
    }
}
