using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Ftp;
using GlDrive.Irc;
using GlDrive.Spread;
using GlDrive.Tls;
using Serilog;

namespace GlDrive.Services;

public partial class ServerManager : IDisposable
{
    private readonly AppConfig _config;
    private readonly CertificateManager _certManager;
    private readonly NotificationStore _notificationStore;
    private readonly ConcurrentDictionary<string, MountService> _servers = new();
    private readonly ConcurrentDictionary<string, RuntimeConfigPolicy.Snapshot> _runtimeConfigSnapshots = new();
    private readonly ConcurrentDictionary<string, string> _ircConfigFingerprints = new();
    private readonly ConcurrentDictionary<string, IrcService> _ircServices = new();
    private readonly ConcurrentDictionary<string, IrcAnnounceListener> _announceListeners = new();
    private readonly ConcurrentDictionary<string, IrcPatternDetector> _patternDetectors = new();
    private readonly ConcurrentDictionary<string, RequestFiller> _requestFillers = new();
    // Servers whose mount failed, awaiting retry. Value is the attempt count so far.
    private readonly ConcurrentDictionary<string, int> _pendingRemounts = new();
    // Background scheduling only: manual mounts bypass this deadline. Local
    // admission holds must not be recorded as new remote outage observations.
    private readonly ConcurrentDictionary<string, (string Identity, DateTimeOffset Due)> _remountRetryAfter = new();
    // Last remount error message per server, so a CHANGED error always logs in full
    // even deep into a failure streak — that transition is the diagnostic moment.
    private readonly ConcurrentDictionary<string, string> _lastRemountError = new();
    private readonly CancellationTokenSource _remountCts = new();
    private Task? _remountLoop;
    private readonly object _remountLoopLock = new();
    private SpreadManager? _spreadManager;
    private readonly SemaphoreSlim _configSync = new(1, 1);
    private volatile bool _disposed;

    public SpreadManager? Spread => _spreadManager;

    public event Action<string, string, MountState>? ServerStateChanged; // serverId, serverName, state
    public event Action<string, string, string, string, string>? NewReleaseDetected; // serverId, serverName, category, release, remotePath
    public event Action<string, string, IrcServiceState>? IrcStateChanged; // serverId, serverName, state
    public event Action<string, string>? BncRateLimitDetected; // serverName, message
    public event Action<FtpAuthenticationNotice>? AuthenticationRejected;

    public ServerManager(AppConfig config, CertificateManager certManager, NotificationStore notificationStore)
    {
        _config = config;
        _certManager = certManager;
        _notificationStore = notificationStore;
        _spreadManager = new SpreadManager(config);
        _spreadManager._getMainPool = serverId =>
            _servers.TryGetValue(serverId, out var svc) ? svc.Pool : null;
        _spreadManager._getSearchService = serverId =>
            _servers.TryGetValue(serverId, out var svc) ? svc.Search : null;
        InitializeSiteHealth();
        InitializeNukes();
        InitializeWanted();
        InitializeAnnounceFeedHealth();
        GuardedFtpLogin.AuthenticationRejected += OnAuthenticationRejected;
    }

    internal bool OnAuthenticationRejected(FtpAuthenticationNotice notice)
    {
        var listeners = AuthenticationRejected;
        if (_disposed || listeners == null) return false;
        listeners(notice);
        return true;
    }

    public bool IsAuthenticationHeld(string serverId) =>
        _config.Servers.FirstOrDefault(s => s.Id == serverId) is { } server
        && GuardedFtpLogin.IsAuthenticationHeld(FtpLoginAccount.From(server.Connection));

    public DateTime? AuthenticationRetryAtUtc(string serverId) =>
        _config.Servers.FirstOrDefault(s => s.Id == serverId) is { } server
            ? GuardedFtpLogin.AuthenticationRetryAtUtc(FtpLoginAccount.From(server.Connection)) : null;

    public void RetryAuthentication(string serverId)
    {
        if (!_disposed && _config.Servers.FirstOrDefault(s => s.Id == serverId) is { } server)
            GuardedFtpLogin.RequestManualRetry(FtpLoginAccount.From(server.Connection));
    }

    public async Task MountServer(string serverId, CancellationToken ct = default, bool manualRetry = false)
    {
        if (_disposed) return;
        var serverConfig = _config.Servers.FirstOrDefault(s => s.Id == serverId);
        if (serverConfig == null)
        {
            Log.Warning("Server {ServerId} not found in config", serverId);
            return;
        }

        var retryHeldAccount = manualRetry && IsAuthenticationHeld(serverId);
        if (manualRetry) RetryAuthentication(serverId);
        if (_servers.TryGetValue(serverId, out var existing))
        {
            if (retryHeldAccount)
            {
                // A held account can still have a mounted service and usable leases.
                // Reinitialize through the existing pool policy; do not remount it.
                if (existing.Pool is { } pool) await pool.Reinitialize(ct);
                return;
            }
            Log.Warning("Server {ServerName} is already mounted", serverConfig.Name);
            return;
        }

        try { await CheckAutomaticSiteRetry(serverConfig, ct, manualRetry); }
        catch (SiteRetryDeferredException) { QueueRemount(serverId); throw; }
        var mountAttemptStarted = DateTime.UtcNow;

        var service = new MountService(serverConfig, _config.Downloads, _certManager, _spreadManager, _wantedCoordinator);

        service.StateChanged += state =>
        {
            if (state == MountState.Connected) RecordSiteRecovery(serverConfig);
            ServerStateChanged?.Invoke(serverId, serverConfig.Name, state);
        };
        service.RetryNotBeforeUtc = () => SiteRetryNotBefore(serverConfig);
        service.ConnectionFailureObserved += (failure, connectAttempt, started) =>
            RecordSiteFailure(serverConfig, failure, started, connectAttempt);

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

        if (!_servers.TryAdd(serverId, service))
        {
            service.Dispose();
            Log.Warning("Server {ServerName} is already mounting or mounted", serverConfig.Name);
            return;
        }

        try
        {
            await service.Mount(ct);
            _runtimeConfigSnapshots[serverId] = CaptureRuntimeConfig(serverConfig);
            if (service.Search is { } search) search.IndexPublished += _ => QueueWantedSweep();
            ReplayWantedCompletions(serverId);
            ReplayRacePulls(serverId);
            QueueWantedSweep();
        }
        catch (Exception exception)
        {
            if (!ct.IsCancellationRequested)
            {
                RecordSiteFailure(serverConfig, exception, mountAttemptStarted, connectAttempt: true);
                QueueRemount(serverId);
            }
            _wantedCoordinator.RemoveServer(serverId);
            _servers.TryRemove(serverId, out _);
            _runtimeConfigSnapshots.TryRemove(serverId, out _);
            service.Dispose();
            throw;
        }

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
        _wantedCoordinator.RemoveServer(serverId);
        await StopIrcService(serverId);

        if (_spreadManager != null)
            await _spreadManager.DisposePool(serverId);

        if (!_servers.TryGetValue(serverId, out var service))
            return;

        await service.UnmountAsync();
        service.Dispose();
        _servers.TryRemove(serverId, out _);
        _runtimeConfigSnapshots.TryRemove(serverId, out _);
    }

    public void UnmountServer(string serverId)
    {
        _wantedCoordinator.RemoveServer(serverId);
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
        _runtimeConfigSnapshots.TryRemove(serverId, out _);
    }

    public async Task MountAll(CancellationToken ct = default, bool manualRetry = false)
    {
        // Mount all servers in parallel — don't let one slow server block the others
        var servers = _config.Servers.Where(s => s.Enabled && s.Mount.AutoMountOnStart).ToList();
        var tasks = servers.Select(async server =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await MountServer(server.Id, ct, manualRetry);
                Log.Information("Server {Name} mounted in {Elapsed}ms", server.Name, sw.ElapsedMilliseconds);
            }
            catch (SiteRetryDeferredException deferred)
            {
                Log.Information("FTP retry for {Name} remains deferred until {RetryAt}", server.Name, deferred.RetryAtUtc);
                QueueRemount(server.Id);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // A queued, forever-retried mount is degraded availability, not an
                // unhandled application error. Keep the exception at Warning so the
                // remote/network cause remains diagnosable without producing a false ERR.
                Log.Warning(ex, "Failed to mount server {Name} after {Elapsed}ms — queued for retry",
                    server.Name, sw.ElapsedMilliseconds);
                // A failed mount used to be permanent for the process lifetime: the
                // MountService (and the ConnectionMonitor inside it that would have
                // reconnected) is disposed by MountServer's catch. Queue it instead.
                QueueRemount(server.Id);
            }
        });
        await Task.WhenAll(tasks);

        // Start IRC for all enabled servers, even those whose FTP mount failed
        await EnsureAllIrcServices();
    }

    /// <summary>
    /// Register a server for background remount attempts and make sure the retry
    /// loop is running. Safe to call repeatedly for the same server.
    /// </summary>
    private void QueueRemount(string serverId)
    {
        if (_remountCts.IsCancellationRequested) return;
        _pendingRemounts.TryAdd(serverId, 0);

        lock (_remountLoopLock)
        {
            if (_remountLoop is { IsCompleted: false }) return;
            _remountLoop = Task.Run(() => RemountLoop(_remountCts.Token));
        }
    }

    internal DateTimeOffset? RemountRetryNotBefore(string serverId)
    {
        DateTimeOffset? due = null;
        var server = _config.Servers.FirstOrDefault(s => s.Id == serverId);
        if (server != null && _config.Health.Enabled)
        {
            // Derive supervisor holds live: recovery or disabling health/probes must
            // remove them immediately, including holds shared by account aliases.
            due = AccountRetryNotBefore(FtpLoginAccount.From(server.Connection));
            var health = GetSiteHealth(serverId);
            if (_config.Health.TcpProbesEnabled &&
                health.FailureClass is SiteFailureKind.Unreachable or SiteFailureKind.Timeout &&
                health.FirstFailureUtc is { } first && DateTimeOffset.UtcNow - first >= TimeSpan.FromMinutes(30) &&
                health.LastProbeUtc is { } probe && (due == null || probe.AddMinutes(15) > due))
                due = probe.AddMinutes(15);
        }
        if (server != null && _remountRetryAfter.TryGetValue(serverId, out var retry) &&
            retry.Identity == EndpointIdentity(server))
        {
            if (due == null || retry.Due > due) due = retry.Due;
        }
        return due;
    }

    internal DateTimeOffset DeferRemount(ServerConfig server, Exception error, int attempt)
    {
        var now = DateTimeOffset.UtcNow;
        var nextAttempt = attempt < int.MaxValue ? attempt + 1 : int.MaxValue;
        var due = MountRetryPolicy.RetryAt(error, now, nextAttempt, healthDeadline: null, includeSupervisor: false);
        _remountRetryAfter[server.Id] = (EndpointIdentity(server), due);
        // Health is a live supervisor setting, not part of the independent local
        // retry record: disabling it must also remove its longer outage deadline.
        return RemountRetryNotBefore(server.Id) ?? due;
    }

    /// <summary>
    /// Retries failed mounts on a capped exponential backoff until they succeed or
    /// stop being eligible. Never gives up permanently — the conditions that cause a
    /// startup mount failure (no network yet after resume, DNS not ready, site down)
    /// are precisely the ones that resolve on their own.
    /// </summary>
    private async Task RemountLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && !_pendingRemounts.IsEmpty)
            {
                // Wait for the shortest delay any pending server is due.
                var nextDelay = _config.Health.Enabled
                    ? _pendingRemounts.Keys.Select(id => RemountRetryNotBefore(id) is { } due
                        ? TimeSpan.FromSeconds(Math.Clamp((due - DateTimeOffset.UtcNow).TotalSeconds, 1, 30))
                        : TimeSpan.FromSeconds(30)).DefaultIfEmpty(TimeSpan.FromSeconds(30)).Min()
                    : _pendingRemounts.Values.Select(attempts => MountRetryPolicy.DelayFor(attempts < int.MaxValue ? attempts + 1 : int.MaxValue))
                        .DefaultIfEmpty(MountRetryPolicy.InitialDelay).Min();

                await Task.Delay(nextDelay, ct);

                foreach (var serverId in _pendingRemounts.Keys.ToList())
                {
                    if (ct.IsCancellationRequested) return;

                    var cfg = _config.Servers.FirstOrDefault(s => s.Id == serverId);
                    if (!MountRetryPolicy.ShouldRetry(
                            alreadyMounted: _servers.ContainsKey(serverId),
                            existsInConfig: cfg != null,
                            enabled: cfg?.Enabled ?? false,
                            autoMountOnStart: cfg?.Mount.AutoMountOnStart ?? false))
                    {
                        _pendingRemounts.TryRemove(serverId, out _);
                        _remountRetryAfter.TryRemove(serverId, out _);
                        _lastRemountError.TryRemove(serverId, out _);
                        Log.Information(
                            "Remount for {ServerId} no longer needed (mounted={Mounted}, inConfig={InConfig}) — dropped from retry queue",
                            serverId, _servers.ContainsKey(serverId), cfg != null);
                        continue;
                    }

                    if (RemountRetryNotBefore(serverId) > DateTimeOffset.UtcNow) continue;

                    var attempt = _pendingRemounts.TryGetValue(serverId, out var a) ? (a < int.MaxValue ? a + 1 : int.MaxValue) : 1;
                    _pendingRemounts[serverId] = attempt;

                    try
                    {
                        Log.Information("Remount attempt {Attempt} for {Name}...", attempt, cfg!.Name);
                        await MountServer(serverId, ct);
                        _pendingRemounts.TryRemove(serverId, out _);
                        _remountRetryAfter.TryRemove(serverId, out _);
                        _lastRemountError.TryRemove(serverId, out _);
                        Log.Information("Remount succeeded for {Name} on attempt {Attempt}", cfg.Name, attempt);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (SiteRetryDeferredException ex) { DeferRemount(cfg!, ex, attempt); }
                    catch (Exception ex)
                    {
                        var due = DeferRemount(cfg!, ex, attempt);
                        var delay = due - DateTimeOffset.UtcNow;
                        _lastRemountError.TryGetValue(serverId, out var previous);
                        if (MountFailureLogPolicy.ShouldLogFullDetail(attempt, previous, ex.Message))
                            Log.Warning(ex, "Remount attempt {Attempt} for {Name} failed — next try in {Delay}",
                                attempt, cfg!.Name, delay);
                        else
                            Log.Information("Remount attempt {Attempt} for {Name} failed ({Error}) — next try in {Delay}",
                                attempt, cfg!.Name, ex.Message, delay);
                        _lastRemountError[serverId] = ex.Message;
                    }
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            // Never let the loop die silently — that would recreate the very bug this fixes.
            Log.Error(ex, "Remount loop terminated unexpectedly; failed servers will not be retried until restart");
        }
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
            try
            {
                await SyncIrcService(server);
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
        await _configSync.WaitAsync();
        try { if (!_disposed) await SyncAfterConfigChangeCore(); }
        finally { _configSync.Release(); }
    }

    private async Task SyncAfterConfigChangeCore()
    {
        QueueWantedSweep();
        lock (_torrentDownloadLock)
            if (!_torrentDownloadsStopping) _torrentDownloads?.RefreshOptions();
        RefreshTorrentDownloadRate();
        _spreadManager?.RefreshPoolHealth();
        GlDrive.Downloads.Segmented.DownloadNetworkServices.Initialize(_config.Downloads);
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
            if (_disposed) return;
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
                var snapshot = CaptureRuntimeConfig(server);
                if (!_runtimeConfigSnapshots.TryGetValue(server.Id, out var activeSnapshot) ||
                    snapshot.Fingerprint != activeSnapshot.Fingerprint)
                {
                    Log.Information("Configuration for {ServerName}: remounting: {Properties}", server.Name,
                        activeSnapshot == null ? "initial configuration" : string.Join(", ", snapshot.Changed(activeSnapshot, RuntimeConfigEffect.Remount)));
                    await UnmountServerAsync(server.Id);
                    try
                    {
                        await MountServer(server.Id);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Failed to remount changed server {ServerName}", server.Name);
                    }
                    continue;
                }

                var changes = snapshot.Changed(activeSnapshot);
                if (changes.Count > 0 && _servers.TryGetValue(server.Id, out var service))
                {
                    await service.ApplyLiveConfigAsync(changes);
                    if (changes.Any(p => p.StartsWith("spreadSite.", StringComparison.Ordinal)))
                        _spreadManager?.RefreshSiteRules();
                    _runtimeConfigSnapshots[server.Id] = snapshot;
                    var live = changes.Where(p => snapshot.Values[p].Effect != RuntimeConfigEffect.NextDriveMount).ToArray();
                    if (live.Length > 0)
                        Log.Information("Configuration for {ServerName}: applied live: {Properties}", server.Name, string.Join(", ", live));
                }

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

    /// <summary>Start IRC independently of an FTP mount, using the normal announce wiring.</summary>
    public Task StartIrc(string serverId)
    {
        var server = _config.Servers.FirstOrDefault(s => s.Id == serverId)
            ?? throw new KeyNotFoundException($"Unknown server: {serverId}");
        if (!server.Enabled || !server.Irc.Enabled || string.IsNullOrWhiteSpace(server.Irc.Host))
            throw new InvalidOperationException("Enable the server and configure IRC before starting it.");
        return StartIrcService(server);
    }

    /// <summary>Stop IRC and dispose announce listeners and encrypted PM persistence.</summary>
    public Task StopIrc(string serverId) => StopIrcService(serverId);

    public IDictionary<string, IrcService> GetIrcServices() => _ircServices;

    private RuntimeConfigPolicy.Snapshot CaptureRuntimeConfig(ServerConfig server)
    {
        var connection = server.Connection;
        var proxy = connection.Proxy;
        var secrets = string.Join('\u001f',
            CredentialStore.GetPassword(connection.Host, connection.Port, connection.Username) ?? "",
            proxy is { Enabled: true }
                ? CredentialStore.GetProxyPassword(proxy.Host, proxy.Port, proxy.Username) ?? ""
                : "");
        var credentialFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secrets)));
        return RuntimeConfigPolicy.Capture(server, _config.Downloads, credentialFingerprint, _config.Spread.SpreadPoolSize);
    }

    private async Task SyncIrcService(ServerConfig server)
    {
        if (_disposed) return;
        var fingerprint = ComputeIrcFingerprint(server);
        if (_ircServices.ContainsKey(server.Id) &&
            (!_ircConfigFingerprints.TryGetValue(server.Id, out var previous) || previous != fingerprint))
        {
            Log.Information("Configuration for {ServerName}: restarting IRC only", server.Name);
            await StopIrcService(server.Id);
        }
        if (!_ircServices.ContainsKey(server.Id)) await StartIrcService(server);
        _ircConfigFingerprints[server.Id] = fingerprint;
    }

    private string ComputeIrcFingerprint(ServerConfig server) => RuntimeConfigPolicy.Hash(new
        {
            server.Irc,
            Password = RuntimeConfigPolicy.Hash(CredentialStore.GetIrcPassword(server.Irc.Host, server.Irc.Port, server.Irc.Nick) ?? ""),
            _config.Spread.AutoRaceOnNotification
        });

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

    private static readonly TimeSpan SiteInviteMainPoolBudget = TimeSpan.FromSeconds(10);

    private async Task StartIrcService(ServerConfig serverConfig)
    {
        if (_disposed) return;
        if (_ircServices.ContainsKey(serverConfig.Id)) return;

        var ircService = new IrcService(serverConfig, _certManager);
        // A connected mount is the whole precondition: SITE INVITE reuses an idle pooled login,
        // and any new login it needs is already deferred at GuardedFtpLogin. Gating on the
        // recorded health state kept invite-only announce channels unjoined after one transient
        // login failure until some later login happened to succeed.
        ircService.SiteInviteAvailable = () => !_disposed && (!_config.Health.Enabled ||
            (_config.Servers.Any(s => s.Id == serverConfig.Id && s.Enabled && s.Irc.Enabled) &&
             GetServer(serverConfig.Id) is { CurrentState: MountState.Connected, Pool: not null }));
        ircService.StateChanged += state =>
            IrcStateChanged?.Invoke(serverConfig.Id, serverConfig.Name, state);

        // Wire up SITE INVITE via the FTP connection pool
        ircService.SiteInviteFunc = async (nick, ct) =>
        {
            if (!_servers.TryGetValue(serverConfig.Id, out var mountService) || mountService.Pool == null)
                return "SITE INVITE skipped: server not mounted";

            // The main pool can be a single connection kept busy by spread scans; an invite
            // queued behind them for its whole budget left the announce channels unjoined.
            var mainPool = mountService.Pool;
            var spreadPool = _spreadManager?.GetLivePool(serverConfig.Id);
            await using var conn = await PoolFallback.BorrowAsync(
                t => mainPool.BorrowFor(FtpBorrowPurpose.IrcInvite, t),
                spreadPool == null ? null : t =>
                {
                    Log.Information("IRC {Server}: main FTP pool busy — sending SITE INVITE on the spread pool",
                        serverConfig.Name);
                    return spreadPool.BorrowFor(FtpBorrowPurpose.IrcInvite, t);
                },
                SiteInviteMainPoolBudget, ct);
            var sanitized = nick.Replace("\r", "").Replace("\n", "").Replace("\0", "");
                if (sanitized.Contains(' ') || sanitized.Length == 0 || sanitized.Length > 30)
                    return "SITE INVITE skipped: invalid nick";
            var reply = await conn.Client.Execute($"SITE INVITE {sanitized}", ct);
            return reply.Message;
        };

        _ircServices[serverConfig.Id] = ircService;
        await ircService.StartAsync();
        _ircConfigFingerprints[serverConfig.Id] = ComputeIrcFingerprint(serverConfig);

        // Start pattern detector for learning announce formats
        var detector = new IrcPatternDetector(ircService, serverConfig.Id);
        _patternDetectors[serverConfig.Id] = detector;

        // Both consumers use already received announces; wishlist matching needs no
        // polling and remains available independently of the spread engine.
        {
            var defaultAutoRace = _config.Spread.AutoRaceOnNotification;
            var listener = new IrcAnnounceListener(serverConfig.Id, ircService,
                serverConfig.Irc.AnnounceRules, defaultAutoRace, serverConfig.Irc.Channels);
            listener.SourceDown = () => _config.Health.Enabled && GetSiteHealth(serverConfig.Id) is
                { FirstFailureUtc: not null, State: SiteHealthState.Down or SiteHealthState.Quarantined or SiteHealthState.AuthRejected };
            listener.ChannelAnnounceObserved += (serverId, channel, observedAt) =>
                ObserveAnnounceFeed(serverId, channel, observedAt);
            listener.SitebotEventReceived += ev =>
            {
                _spreadManager?.ObserveSitebot(ev);
                ObserveNukeSitebot(ev);
                ObserveWantedSitebot(ev);
            };
            listener.ReleaseAnnounced += (serverId, section, release, autoRace) =>
            {
                Log.Information("IRC announce: [{Section}] {Release} (autoRace={AutoRace})",
                    section, release, autoRace);
                if (listener.SourceDown?.Invoke() == true)
                {
                    Log.Information("IRC announce recorded while source {Server} is down: {Release}", serverId, release);
                    return;
                }
                if (_servers.TryGetValue(serverId, out var mount))
                    mount.OnIrcRelease(section, release);
                if (autoRace)
                {
                    // The announcing site usually has the release — pass it as source hint.
                    _spreadManager?.TryAutoRace(section, release, serverId);
                }
            };
            _announceListeners.TryAdd(serverConfig.Id, listener);
        }

        // Wire auto request filler (RaceTrade-style)
        if (serverConfig.Irc.RequestFiller.Enabled && _spreadManager != null)
        {
            var filler = new RequestFiller(serverConfig.Id, ircService,
                serverConfig.Irc.RequestFiller, this, _spreadManager);
            _requestFillers.TryAdd(serverConfig.Id, filler);
        }
    }

    private async Task StopIrcService(string serverId)
    {
        _ircConfigFingerprints.TryRemove(serverId, out _);
        if (_patternDetectors.TryRemove(serverId, out var detector))
            detector.Dispose();
        if (_announceListeners.TryRemove(serverId, out var listener))
            listener.Dispose();
        if (_requestFillers.TryRemove(serverId, out var filler))
            filler.Dispose();

        if (!_ircServices.TryGetValue(serverId, out var ircService)) return;
        // Dispose() must run even if StopAsync throws — it flushes the PM-history store.
        try { await ircService.StopAsync(); }
        catch (Exception ex) { Log.Warning(ex, "IRC StopAsync failed for {Server} during teardown", serverId); }
        ircService.Dispose();
        _ircServices.TryRemove(serverId, out _);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopSiteHealth();
        StopAnnounceFeedHealth();
        StopWanted();
        GuardedFtpLogin.AuthenticationRejected -= OnAuthenticationRejected;
        StopTorrentDownloads();
        try { _remountCts.Cancel(); } catch { }
        try { _remountLoop?.Wait(TimeSpan.FromSeconds(5)); } catch { }
        _remountCts.Dispose();
        _pendingRemounts.Clear();
        _remountRetryAfter.Clear();
        _lastRemountError.Clear();

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
            try { Task.Run(() => irc.StopAsync()).Wait(TimeSpan.FromSeconds(3)); } catch { }
            irc.Dispose();
        }
        _ircServices.Clear();

        var unmounts = _servers.Values.Select(server => Task.Run(server.UnmountAsync)).ToArray();
        try
        {
            if (!Task.WhenAll(unmounts).Wait(TimeSpan.FromSeconds(10)))
                Log.Warning("Server shutdown exceeded 10 seconds; remaining teardown continues in background");
        }
        catch (Exception ex) { Log.Warning(ex, "Server shutdown failed"); }
        GC.SuppressFinalize(this);
    }
}
