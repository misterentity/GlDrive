using GlDrive.Ftp;

namespace GlDrive.Services;

public partial class ServerManager
{
    public WaitingState GetWaitingState(string serverId)
    {
        var server = _config.Servers.First(s => s.Id == serverId);
        var gate = ServerLoginGateRegistry.TryGet(server.Connection.Host, server.Connection.Port, server.Connection.Username);
        var capacity = gate == null ? "No login gate allocated" :
            $"Login slots {gate.Held}/{gate.Limit}; transfer ceiling {gate.PriorityLimit}; general ceiling {gate.GeneralLimit}";
        var live = GetServer(serverId);
        return WaitingState.Site(server.Enabled, live?.CurrentState == MountState.Connected && live.Pool is { IsConnected: true, IsExhausted: false },
            IsAuthenticationHeld(serverId), AuthenticationRetryAtUtc(serverId), GetSiteHealth(serverId), _config.Health.Enabled,
            GuardedFtpLogin.GetLoginBudget(FtpLoginAccount.From(server.Connection)), RemountRetryNotBefore(serverId), DateTimeOffset.UtcNow, capacity);
    }
}
