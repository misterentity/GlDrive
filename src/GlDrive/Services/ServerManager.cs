using System.Collections.Concurrent;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Irc;
using GlDrive.Spread;
using GlDrive.Tls;
using Serilog;

namespace GlDrive.Services;

public class ServerManager : IDisposable
{
    private readonly AppConfig _config;
    private readonly CertificateManager _certManager;
    private readonly NotificationStore _notificationStore;
    private readonly ConcurrentDictionary<string, MountService> _servers = new();
    private readonly ConcurrentDictionary<string, IrcService> _ircServices = new();
    private readonly Dictionary<string, IrcAnnounceListener> _announceListeners = new();
    private readonly Dictionary<string, IrcPatternDetector> _patternDetectors = new();
    private readonly Dictionary<string, RequestFiller> _requestFillers = new();
    private SpreadManager? _spreadManager;

    public SpreadManager? Spread => _spreadManager;

    public event Action<string, string, MountState>? ServerStateChanged; // serverId, serverName, state
    public event Action<string, string, string, string, string>? NewReleaseDetected; // serverId, serverName, category, release, remotePath
    public event Action<string, string, IrcServiceState>? IrcStateChanged; // serverId, serverName, state
    public event Action<string, string>? BncRateLimitDetected; // serverName, message

    public ServerManager(AppConfig config, CertificateManager certManager, NotificationStore notificationStore)
    {
        _config = config;
        _certManager = certManager;
        _notificationStore = notificationStore;
        _spreadManager = new SpreadManager(config);
        _spreadManager._getMainPool = serverId =>
            _servers.TryGetValue(serverId, out var svc) ? svc.Pool : null;
    }

    public async Task MountServer(string serverId, CancellationToken ct = default)
    {
        var serverConfig = _config.Servers.FirstOrDefault(s => s.Id == serverId);
        if (serverConfig == null)
        {
            Log.Warning("Server {ServerId} not found in config", serverId);
            return;
        }

        if (_servers.ContainsKey(serverId))
        {
            Log.Warning("Server {ServerName} is already mounted", serverConfig.Name);
            return;
        }

        var service = new MountService(serverConfig, _config.Downloads, _certManager);

        service.StateChanged += state =>
            ServerStateChanged?.Invoke(serverId, serverConfig.Name, state);

        service.BncRateLimitDetected += msg =>
            BncRateLimitDetected?.Invoke(serverConfig.Name, msg);

        service.NewReleaseDetected += (category, release, remotePath) =>
        {
            _notificationStore.Add(new NotificationItem
            {
                ServerId = serverId,
                ServerName = serverConfig.Name,
                Category = category,
                ReleaseName = release,
                RemotePath = remotePath
            });
            NewReleaseDetected?.Invoke(serverId, serverConfig.Name, category, release, remotePath);

            // Auto-race if enabled — pass known source server and path
            _spreadManager?.TryAutoRace(category, release, serverId, remotePath);
        };

        _servers[serverId] = service;

        await service.Mount(ct);

        // Initialize spread pool for FXP
        if (_spreadManager != null && service.Factory != null)
        {
            try
            {
                await _spreadManager.InitializePool(serverId, service.Factory, ct);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to init spread pool for {Server}", serverConfig.Name);
            }
        }

        // Start IRC service if configured
        if (serverConfig.Irc.Enabled && !string.IsNullOrEmpty(serverConfig.Irc.Host))
        {
            try
            {
                await StartIrcService(serverConfig);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to start IRC for {Server}", serverConfig.Name);
            }
        }
    }

    public async Task UnmountServerAsync(string serverId)
    {
        await StopIrcService(serverId);

        if (_spreadManager != null)
            await _spreadManager.DisposePool(serverId);

        if (!_servers.TryGetValue(serverId, out var service))
            return;

        await service.UnmountAsync();
        service.Dispose();
        _servers.TryRemove(serverId, out _);
    }

    public void UnmountServer(string serverId)
    {
        // Run awaits on the threadpool so we don't deadlock if this is called
        // from the UI dispatcher (e.g. tray shutdown). Capturing the dispatcher
        // SynchronizationContext in .GetResult() was the original bug risk.
        Task.Run(() => StopIrcService(serverId)).GetAwaiter().GetResult();
        if (_spreadManager != null)
            Task.Run(() => _spreadManager.DisposePool(serverId)).GetAwaiter().GetResult();

        if (!_servers.TryGetValue(serverId, out var service))
            return;

        service.Unmount();
        service.Dispose();
        _servers.TryRemove(serverId, out _);
    }

    public async Task MountAll(CancellationToken ct = default)
    {
        // Mount all servers in parallel — don't let one slow server block the others
        var servers = _config.Servers.Where(s => s.Enabled && s.Mount.AutoMountOnStart).ToList();
        var tasks = servers.Select(async server =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await MountServer(server.Id, ct);
                Log.Information("Server {Name} mounted in {Elapsed}ms", server.Name, sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to mount server {Name} after {Elapsed}ms", server.Name, sw.ElapsedMilliseconds);
            }
        });
        await Task.WhenAll(tasks);

        // Start IRC for all enabled servers, even those whose FTP mount failed
        await EnsureAllIrcServices();
    }

    /// <summary>
    /// Start IRC services for all enabled servers with IRC configured,
    /// regardless of whether their FTP connection is mounted.
    /// </summary>
    private async Task EnsureAllIrcServices()
    {
        foreach (var server in _config.Servers.Where(s => s.Enabled &&
            s.Irc.Enabled && !string.IsNullOrEmpty(s.Irc.Host)))
        {
            if (_ircServices.ContainsKey(server.Id)) continue;
            try
            {
                Log.Information("Starting IRC for {ServerName} (independent of FTP)", server.Name);
                await StartIrcService(server);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to start IRC for {ServerName}", server.Name);
            }
        }
    }

    public async Task UnmountAllAsync()
    {
        foreach (var serverId in _servers.Keys.ToList())
            await UnmountServerAsync(serverId);
    }

    public void UnmountAll()
    {
        foreach (var serverId in _servers.Keys.ToList())
            UnmountServer(serverId);
    }

    /// <summary>
    /// Sync running services with current config after settings change.
    /// Mounts new enabled servers, unmounts removed servers, and starts/stops IRC as needed.
    /// </summary>
    public async Task SyncAfterConfigChange()
    {
        var configIds = new HashSet<string>(_config.Servers.Select(s => s.Id));

        // Unmount servers that were removed from config
        foreach (var serverId in _servers.Keys.ToList())
        {
            if (!configIds.Contains(serverId))
            {
                Log.Information("Server {ServerId} removed from config, unmounting", serverId);
                await UnmountServerAsync(serverId);
            }
        }

        // Mount new enabled servers that aren't mounted yet
        foreach (var server in _config.Servers.Where(s => s.Enabled))
        {
            if (!_servers.ContainsKey(server.Id))
            {
                try
                {
                    Log.Information("New server {ServerName} detected, mounting", server.Name);
                    await MountServer(server.Id);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to mount new server {ServerName}", server.Name);
                }
            }
            else
            {
                // Server already mounted — check if IRC config changed
                var ircEnabled = server.Irc.Enabled && !string.IsNullOrEmpty(server.Irc.Host);
                var ircRunning = _ircServices.ContainsKey(server.Id);

                if (ircEnabled && !ircRunning)
                {
                    try
                    {
                        await StartIrcService(server);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Failed to start IRC for {ServerName}", server.Name);
                    }
                }
                else if (!ircEnabled && ircRunning)
                {
                    await StopIrcService(server.Id);
                }
            }
        }

        // Unmount disabled servers that are still mounted
        foreach (var server in _config.Servers.Where(s => !s.Enabled))
        {
            if (_servers.ContainsKey(server.Id))
            {
                Log.Information("Server {ServerName} disabled, unmounting", server.Name);
                await UnmountServerAsync(server.Id);
            }
            // Also stop IRC for disabled servers
            if (_ircServices.ContainsKey(server.Id))
                await StopIrcService(server.Id);
        }

        // Start IRC for all enabled servers, even those whose FTP mount failed
        await EnsureAllIrcServices();

        // Stop IRC for servers where it was disabled
        foreach (var server in _config.Servers.Where(s => s.Enabled && (!s.Irc.Enabled || string.IsNullOrEmpty(s.Irc.Host))))
        {
            if (_ircServices.ContainsKey(server.Id))
                await StopIrcService(server.Id);
        }

        // Fire a state change to refresh tray menu
        ServerStateChanged?.Invoke("", "", MountState.Unmounted);
    }

    public MountService? GetServer(string serverId)
    {
        _servers.TryGetValue(serverId, out var service);
        return service;
    }

    public IReadOnlyList<MountService> GetMountedServers() => _servers.Values.ToList();

    /// <summary>Returns all currently mounted/active MountService instances. Used by HealthRollup.</summary>
    public IEnumerable<MountService> GetAllMountServices() => _servers.Values;

    /// <summary>IDs of currently mounted/connected servers.</summary>
    public IReadOnlyList<string> ConnectedServerIds => _servers.Keys.ToList();

    public IrcService? GetIrcService(string serverId)
    {
        _ircServices.TryGetValue(serverId, out var service);
        return service;
    }

    public IDictionary<string, IrcService> GetIrcServices() => _ircServices;

    /// <summary>
    /// Analyze buffered IRC messages for a server and return detected announce patterns.
    /// </summary>
    public List<DetectedPattern> DetectIrcPatterns(string serverId)
    {
        if (_patternDetectors.TryGetValue(serverId, out var detector))
            return detector.Analyze();
        return [];
    }

    /// <summary>
    /// Returns recent raw IRC channel messages captured for a server. Used by
    /// AI Setup to give the model IRC context alongside SITE RULES.
    /// </summary>
    public List<string> GetRecentIrcMessages(string serverId, int maxMessages = 60)
    {
        if (_patternDetectors.TryGetValue(serverId, out var detector))
            return detector.GetRecentMessages(maxMessages);
        return [];
    }

    private async Task StartIrcService(ServerConfig serverConfig)
    {
        if (_ircServices.ContainsKey(serverConfig.Id)) return;

        var ircService = new IrcService(serverConfig, _certManager);
        ircService.StateChanged += state =>
            IrcStateChanged?.Invoke(serverConfig.Id, serverConfig.Name, state);

        // Wire up SITE INVITE via the FTP connection pool
        ircService.SiteInviteFunc = async (nick, ct) =>
        {
            if (!_servers.TryGetValue(serverConfig.Id, out var mountService) || mountService.Pool == null)
                return "SITE INVITE skipped: server not mounted";

            await using var conn = await mountService.Pool.Borrow(ct);
            var sanitized = nick.Replace("\r", "").Replace("\n", "").Replace("\0", "");
                if (sanitized.Contains(' ') || sanitized.Length == 0 || sanitized.Length > 30)
                    return "SITE INVITE skipped: invalid nick";
            var reply = await conn.Client.Execute($"SITE INVITE {sanitized}", ct);
            return reply.Message;
        };

        _ircServices[serverConfig.Id] = ircService;
        await ircService.StartAsync();

        // Start pattern detector for learning announce formats
        var detector = new IrcPatternDetector(ircService, serverConfig.Id);
        _patternDetectors[serverConfig.Id] = detector;

        // Wire IRC announce listener for auto-racing.
        // Registered whenever the spread engine exists, so the built-in verbose pattern
        // works without requiring users to configure custom announce rules.
        if (_spreadManager != null)
        {
            var defaultAutoRace = _config.Spread.AutoRaceOnNotification;
            var listener = new IrcAnnounceListener(serverConfig.Id, ircService,
                serverConfig.Irc.AnnounceRules, defaultAutoRace);
            listener.ReleaseAnnounced += (serverId, section, release, autoRace) =>
            {
                Log.Information("IRC announce: [{Section}] {Release} (autoRace={AutoRace})",
                    section, release, autoRace);
                if (autoRace)
                {
                    // The announcing site usually has the release — pass it as source hint.
                    _spreadManager?.TryAutoRace(section, release, serverId);
                }
            };
            _announceListeners[serverConfig.Id] = listener;
        }

        // Wire auto request filler (RaceTrade-style)
        if (serverConfig.Irc.RequestFiller.Enabled && _spreadManager != null)
        {
            var filler = new RequestFiller(serverConfig.Id, ircService,
                serverConfig.Irc.RequestFiller, this, _spreadManager);
            _requestFillers[serverConfig.Id] = filler;
        }
    }

    private async Task StopIrcService(string serverId)
    {
        if (_patternDetectors.Remove(serverId, out var detector))
            detector.Dispose();
        if (_announceListeners.Remove(serverId, out var listener))
            listener.Dispose();
        if (_requestFillers.Remove(serverId, out var filler))
            filler.Dispose();

        if (!_ircServices.TryGetValue(serverId, out var ircService)) return;
        await ircService.StopAsync();
        ircService.Dispose();
        _ircServices.TryRemove(serverId, out _);
    }

    public void Dispose()
    {
        _spreadManager?.Dispose();
        _spreadManager = null;

        foreach (var det in _patternDetectors.Values) det.Dispose();
        _patternDetectors.Clear();
        foreach (var listener in _announceListeners.Values) listener.Dispose();
        _announceListeners.Clear();
        foreach (var filler in _requestFillers.Values) filler.Dispose();
        _requestFillers.Clear();

        foreach (var irc in _ircServices.Values)
        {
            try { irc.StopAsync().GetAwaiter().GetResult(); } catch { }
            irc.Dispose();
        }
        _ircServices.Clear();

        UnmountAll();
        GC.SuppressFinalize(this);
    }
}
