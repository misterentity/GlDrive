using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading.Channels;
using FluentFTP;
using GlDrive.Config;
using GlDrive.Ftp;
using GlDrive.Services;
using GlDrive.Tls;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace GlDrive.Tests;

[CollectionDefinition("Stats refresh logger", DisableParallelization = true)]
public sealed class StatsRefreshLoggerCollection { }

[Collection("Stats refresh logger")]
public sealed class StatsRefreshDeadlineTests : IDisposable
{
    private readonly ILogger _previousLogger = Log.Logger;
    private readonly CaptureSink _sink = new();
    private readonly Logger _logger;

    public StatsRefreshDeadlineTests()
    {
        _logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(_sink).CreateLogger();
        Log.Logger = _logger;
    }

    [Fact]
    public async Task Busy_pool_deadline_retains_stats_and_cache_then_recovers_when_the_lease_returns()
    {
        await using var fixture = await Fixture.Create("reply");
        var previous = new SiteStats("12GB", "1:3", DateTime.UtcNow.AddMinutes(-5));
        typeof(MountService).GetProperty(nameof(MountService.Stats))!.SetValue(fixture.Mount, previous);
        var cache = Get<StatsProbeCache>(fixture.Mount, "_statsProbeCache");
        Assert.True(cache.TryPublish(cache.Read() with { WorkingCommand = "SITE STATS" }));
        var notifications = 0;
        fixture.Mount.StatsChanged += () => notifications++;

        await using (var occupied = await fixture.Pool.Borrow())
        {
            await fixture.Mount.RefreshStatsAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Same(previous, fixture.Mount.Stats);
            Assert.Equal("SITE STATS", cache.Read().WorkingCommand);
            Assert.Equal(DateTime.MinValue, cache.Read().UnavailableUntil);
            Assert.Equal(0, notifications);
            Assert.Equal(0, fixture.Server.StatsCommands);
            Assert.Equal(1, fixture.Pool.ActiveCount);
            Assert.Equal(0, fixture.Pool.QuarantineSize);
            var events = RefreshEvents();
            Assert.DoesNotContain(events, e => e.Level >= LogEventLevel.Warning);
            Assert.Null(Assert.Single(events, e => e.Level == LogEventLevel.Information).Exception);
        }

        await fixture.Mount.RefreshStatsAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("42GB", fixture.Mount.Stats?.Credits);
        Assert.Equal("1:3", fixture.Mount.Stats?.Ratio);
        Assert.Equal(1, notifications);
        Assert.Equal(1, fixture.Server.StatsCommands);
        Assert.Equal(0, fixture.Pool.ActiveCount);
    }

    [Theory]
    [InlineData("stall")]
    [InlineData("disconnect")]
    public async Task Failures_after_SITE_starts_remain_warnings_and_quarantine_the_connection(string behavior)
    {
        await using var fixture = await Fixture.Create(behavior);
        var previous = new SiteStats("12GB", "1:3", DateTime.UtcNow.AddMinutes(-5));
        typeof(MountService).GetProperty(nameof(MountService.Stats))!.SetValue(fixture.Mount, previous);

        await fixture.Mount.RefreshStatsAsync().WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(1, fixture.Server.StatsCommands);
        Assert.Same(previous, fixture.Mount.Stats);
        Assert.Equal(DateTime.MinValue, Get<StatsProbeCache>(fixture.Mount, "_statsProbeCache").Read().UnavailableUntil);
        var warning = Assert.Single(RefreshEvents(), e => e.Level == LogEventLevel.Warning);
        Assert.NotNull(warning.Exception);
        if (behavior == "stall") Assert.IsAssignableFrom<OperationCanceledException>(warning.Exception);
        Assert.Equal(0, fixture.Pool.ActiveCount);
        Assert.True(fixture.Pool.QuarantineSize > 0);
    }

    private LogEvent[] RefreshEvents() => _sink.Events.Where(e =>
        e.MessageTemplate.Text.StartsWith("RefreshStatsAsync", StringComparison.Ordinal)).ToArray();

    private static void Set(object target, string field, object value) => target.GetType()
        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static T Get<T>(object target, string field) => (T)target.GetType()
        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    public void Dispose()
    {
        Log.Logger = _previousLogger;
        _logger.Dispose();
    }

    private sealed class CaptureSink : ILogEventSink
    {
        internal ConcurrentQueue<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal required ControlServer Server { get; init; }
        internal required FtpConnectionPool Pool { get; init; }
        internal required MountService Mount { get; init; }

        internal static async Task<Fixture> Create(string behavior)
        {
            var server = new ControlServer(behavior);
            var client = new AsyncFtpClient("127.0.0.1", "test", "test", server.Port);
            client.Config.NoopInterval = 0;
            client.Config.ConnectTimeout = 3000;
            client.Config.ReadTimeout = 20000;
            await client.Connect();
            var pool = new FtpConnectionPool(null!, 1);
            Set(pool, "_created", 1);
            Get<Channel<AsyncFtpClient>>(pool, "_pool").Writer.TryWrite(client);
            var config = new ServerConfig { Id = "stats-test", Name = "synthetic stats server", SiteStatsCommand = "SITE STATS" };
            var mount = new MountService(config, new DownloadConfig(), new CertificateManager(
                Path.Combine(Path.GetTempPath(), "gldrive-stats-" + Guid.NewGuid().ToString("N"), "certs.json")));
            Set(mount, "_pool", pool);
            Set(mount, "_ftp", new FtpOperations(pool));
            typeof(MountService).GetProperty(nameof(MountService.CurrentState))!.SetValue(mount, MountState.Connected);
            return new Fixture { Server = server, Pool = pool, Mount = mount };
        }

        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            await Pool.DisposeAsync();
        }
    }

    // A control-only FTP peer keeps these regressions independent of WinFsp,
    // native TLS and configured production servers while exercising real borrows.
    private sealed class ControlServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _worker;
        private int _statsCommands;
        internal int StatsCommands => Volatile.Read(ref _statsCommands);
        internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        internal ControlServer(string behavior)
        {
            _listener.Start();
            _worker = Run(behavior);
        }

        private async Task Run(string behavior)
        {
            try
            {
                using var peer = await _listener.AcceptTcpClientAsync(_cts.Token);
                using var stream = peer.GetStream();
                using var reader = new StreamReader(stream);
                using var writer = new StreamWriter(stream) { AutoFlush = true, NewLine = "\r\n" };
                await writer.WriteLineAsync("220 Synthetic FTP");
                while (await reader.ReadLineAsync(_cts.Token) is { } command)
                {
                    if (command.StartsWith("SITE ", StringComparison.Ordinal))
                    {
                        Interlocked.Increment(ref _statsCommands);
                        if (behavior == "disconnect") return;
                        if (behavior == "stall")
                        {
                            await Task.Delay(Timeout.InfiniteTimeSpan, _cts.Token);
                            return;
                        }
                        await writer.WriteLineAsync("200 Credits: 42 GB Ratio: 1:3");
                        continue;
                    }
                    await writer.WriteLineAsync(command.Split(' ')[0] switch
                    {
                        "USER" => "331 Password required",
                        "PASS" => "230 Logged in",
                        "SYST" => "215 UNIX Type: L8",
                        "PWD" => "257 \"/\" is current directory",
                        "FEAT" => "211 No extensions",
                        "QUIT" => "221 Goodbye",
                        _ => "200 OK"
                    });
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException) { }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            await _worker;
            _cts.Dispose();
        }
    }
}
