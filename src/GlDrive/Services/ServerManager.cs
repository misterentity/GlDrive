using GlDrive.Config;
using GlDrive.Tls;
using Serilog;

namespace GlDrive.Services;

public class ServerManager : IDisposable
{
    private readonly AppConfig _config;
    private readonly CertificateManager _certManager;
    private readonly Dictionary<string, MountService> _servers = new();

    public event Action<string, string, MountState>? ServerStateChanged; // serverId, serverName, state
    public event Action<string, string, string, string>? NewReleaseDetected; // serverId, serverName, category, release

    public ServerManager(AppConfig config, CertificateManager certManager)
    {
        _config = config;
        _certManager = certManager;
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

        service.NewReleaseDetected += (category, release) =>
            NewReleaseDetected?.Invoke(serverId, serverConfig.Name, category, release);

        _servers[serverId] = service;

        await service.Mount(ct);
    }

    public async Task UnmountServerAsync(string serverId)
    {
        if (!_servers.TryGetValue(serverId, out var service))
            return;

        await service.UnmountAsync();
        service.Dispose();
        _servers.Remove(serverId);
    }

    public void UnmountServer(string serverId)
    {
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

    public void Dispose()
    {
        UnmountAll();
        GC.SuppressFinalize(this);
    }
}
