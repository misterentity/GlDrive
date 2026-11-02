using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Services;
using GlDrive.Tls;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace GlDrive.Tests;

[CollectionDefinition("Config mount logger", DisableParallelization = true)]
public sealed class ConfigMountLoggerCollection { }

[Collection("Config mount logger")]
public sealed class ConfigMountLoggingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-config-mount-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previousRoot = ConfigManager.AppDataPathOverride;
    private readonly ILogger _previousLogger = Log.Logger;
    private readonly CaptureSink _sink = new();
    private readonly Logger _logger;
    private readonly AppConfig _config;
    private readonly ServerConfig _server;
    private readonly CertificateManager _certificates;
    private readonly ServerManager _manager;
    private int _probes;

    public ConfigMountLoggingTests()
    {
        Directory.CreateDirectory(_root);
        ConfigManager.AppDataPathOverride = _root;
        _logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(_sink).CreateLogger();
        Log.Logger = _logger;
        _server = new() { Id = "config-mount-fixture", Name = "existing unavailable site" };
        _server.Connection.Host = "config-mount-fixture.invalid";
        _server.Mount.MountDrive = false;
        _server.Irc.Enabled = false;
        _config = new() { Servers = [_server] };
        _config.Health.TcpProbesEnabled = true;
        _certificates = new CertificateManager();
        _manager = new ServerManager(_config, _certificates, new NotificationStore(persistToDisk: false));
        _manager.TcpProbe = (_, _) => { _probes++; return Task.FromResult(false); };
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Config_refresh_reports_retry_deadline_without_warning_and_keeps_background_retry(
        bool changedServer, bool deniedTcpProbe)
    {
        SeedFailure(deniedTcpProbe ? DateTimeOffset.UtcNow.AddHours(-1) : DateTimeOffset.UtcNow);
        if (changedServer) SeedServiceToRemount();
        var before = _manager.GetSiteHealth(_server.Id);

        // Exercise both real SyncAfterConfigChange branches, including the actual
        // mount admission guard and queue registration. No FTP or drive mount occurs.
        await _manager.SyncAfterConfigChange().WaitAsync(TimeSpan.FromSeconds(10));
        var after = _manager.GetSiteHealth(_server.Id);
        Assert.Equal(deniedTcpProbe ? 1 : 0, _probes);
        Assert.Equal(before.Attempts + (deniedTcpProbe ? 1 : 0), after.Attempts);
        Assert.True(after.NextRetryUtc > DateTimeOffset.UtcNow);
        Assert.Equal(after.NextRetryUtc, _manager.RemountRetryNotBefore(_server.Id));
        Assert.Null(_manager.GetServer(_server.Id));
        Assert.True(Get<ConcurrentDictionary<string, int>>("_pendingRemounts").ContainsKey(_server.Id));
        Assert.DoesNotContain(_sink.Events, e => e.Level >= LogEventLevel.Warning);
        Assert.DoesNotContain(_sink.Events, e => e.RenderMessage().Contains("new server", StringComparison.OrdinalIgnoreCase));
        var deferred = Assert.Single(_sink.Events, e => e.Properties.ContainsKey("RetryAt"));
        Assert.Equal(LogEventLevel.Information, deferred.Level);
        Assert.Null(deferred.Exception);
        Assert.Equal(after.NextRetryUtc, Assert.IsType<ScalarValue>(deferred.Properties["RetryAt"]).Value);

        // Another unrelated settings edit must not make a new outage observation,
        // probe again, or discard the already queued background retry.
        await _manager.SyncAfterConfigChange().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(after, _manager.GetSiteHealth(_server.Id));
        Assert.Equal(deniedTcpProbe ? 1 : 0, _probes);
        Assert.DoesNotContain(_sink.Events, e => e.Level >= LogEventLevel.Warning);
        Assert.Equal(2, _sink.Events.Count(e => e.Properties.ContainsKey("RetryAt")));
        Assert.Single(Get<ConcurrentDictionary<string, int>>("_pendingRemounts"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unexpected_mount_failures_still_warn_with_original_exception(bool changedServer)
    {
        SeedFailure(DateTimeOffset.UtcNow.AddHours(-1));
        if (changedServer) SeedServiceToRemount();
        var error = new IOException("fixture probe failed unexpectedly");
        _manager.TcpProbe = (_, _) => { _probes++; throw error; };

        await _manager.SyncAfterConfigChange().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, _probes);
        var warning = Assert.Single(_sink.Events, e => e.Level >= LogEventLevel.Warning);
        Assert.Equal(LogEventLevel.Warning, warning.Level);
        Assert.Same(error, warning.Exception);
        Assert.Contains(_server.Name, warning.RenderMessage());
        Assert.Contains(changedServer ? "remount" : "mount", warning.RenderMessage());
        Assert.DoesNotContain(_sink.Events, e => e.RenderMessage().Contains("new server", StringComparison.OrdinalIgnoreCase));
    }

    private void SeedFailure(DateTimeOffset at)
    {
        var identity = (string)typeof(ServerManager).GetMethod("EndpointIdentity", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [_server])!;
        Get<SiteHealthTracker>("_siteHealth").Failed(_server.Id, identity, SiteFailureKind.Timeout, at);
    }

    private void SeedServiceToRemount()
    {
        // An existing service with no active configuration snapshot takes the real
        // changed-server branch. It owns no sockets, workers or mounted drive.
        Get<ConcurrentDictionary<string, MountService>>("_servers")[_server.Id] =
            new MountService(_server, _config.Downloads, _certificates);
    }

    private T Get<T>(string name) => (T)typeof(ServerManager)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_manager)!;

    public void Dispose()
    {
        _manager.Dispose();
        Log.Logger = _previousLogger;
        _logger.Dispose();
        ConfigManager.AppDataPathOverride = _previousRoot;
        Directory.Delete(_root, recursive: true);
    }

    private sealed class CaptureSink : ILogEventSink
    {
        internal ConcurrentQueue<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
    }
}
