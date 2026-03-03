using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Irc;
using GlDrive.Tls;
using Serilog;

namespace GlDrive.Services;

public class ServerManager : IDisposable
{
    private readonly AppConfig _config;
    private readonly CertificateManager _certManager;
    private readonly NotificationStore _notificationStore;
    private readonly DownloadHistoryStore _historyStore;
    private readonly Dictionary<string, MountService> _servers = new();
    private readonly Dictionary<string, IrcService> _ircServices = new();

    public event Action<string, string, MountState>? ServerStateChanged; // serverId, serverName, state
    public event Action<string, string, string, string, string>? NewReleaseDetected; // serverId, serverName, category, release, remotePath
    public event Action<string, string, IrcServiceState>? IrcStateChanged; // serverId, serverName, state

    public DownloadHistoryStore HistoryStore => _historyStore;

    public ServerManager(AppConfig config, CertificateManager certManager, NotificationStore notificationStore)
    {
        _config = config;
        _certManager = certManager;
        _notificationStore = notificationStore;
        _historyStore = new DownloadHistoryStore();
        _historyStore.Load();
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

        var service = new MountService(serverConfig, _config.Downloads, _certManager, _historyStore);

        service.StateChanged += state =>
            ServerStateChanged?.Invoke(serverId, serverConfig.Name, state);

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
        };

        _servers[serverId] = service;

        await service.Mount(ct);

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

        if (!_servers.TryGetValue(serverId, out var service))
            return;

        await service.UnmountAsync();
        service.Dispose();
        _servers.Remove(serverId);
    }

    public void UnmountServer(string serverId)
    {
        StopIrcService(serverId).GetAwaiter().GetResult();

        if (!_servers.TryGetValue(serverId, out var service))
            return;

        service.Unmount();
        service.Dispose();
        _servers.Remove(serverId);
    }

    public async Task MountAll(CancellationToken ct = default)
    {
        foreach (var server in _config.Servers.Where(s => s.Enabled && s.Mount.AutoMountOnStart))
        {
            try
            {
                await MountServer(server.Id, ct);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to mount server {ServerName}", server.Name);
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

    public MountService? GetServer(string serverId)
    {
        _servers.TryGetValue(serverId, out var service);
        return service;
    }

    public IReadOnlyList<MountService> GetMountedServers() => _servers.Values.ToList();

    public IrcService? GetIrcService(string serverId)
    {
        _ircServices.TryGetValue(serverId, out var service);
        return service;
    }

    public IReadOnlyDictionary<string, IrcService> GetIrcServices() => _ircServices;

    private async Task StartIrcService(ServerConfig serverConfig)
    {
        if (_ircServices.ContainsKey(serverConfig.Id)) return;

        var ircService = new IrcService(serverConfig, _certManager);
        ircService.StateChanged += state =>
            IrcStateChanged?.Invoke(serverConfig.Id, serverConfig.Name, state);

        _ircServices[serverConfig.Id] = ircService;
        await ircService.StartAsync();
    }

    private async Task StopIrcService(string serverId)
    {
        if (!_ircServices.TryGetValue(serverId, out var ircService)) return;
        await ircService.StopAsync();
        ircService.Dispose();
        _ircServices.Remove(serverId);
    }

    public void Dispose()
    {
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
