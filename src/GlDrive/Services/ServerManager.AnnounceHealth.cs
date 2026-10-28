using System.IO;
using GlDrive.Config;
using GlDrive.Ftp;
using GlDrive.Irc;
using Serilog;

namespace GlDrive.Services;

public partial class ServerManager
{
    private AnnounceFeedHealthMonitor _announceHealth = null!;
    private System.Threading.Timer? _announceHealthTimer;
    private int _announceHealthTickRunning;
    private readonly CancellationTokenSource _announceHealthStop = new();
    private CancellationToken _announceHealthCancellation;
    public event Action<AnnounceFeedNotice>? AnnounceFeedNotice;

    private void InitializeAnnounceFeedHealth()
    {
        _announceHealth = new(Path.Combine(ConfigManager.AppDataPath, "announce-feed-health.json"));
        _announceHealthCancellation = _announceHealthStop.Token;
        _announceHealthTimer = new System.Threading.Timer(_ => _ = TickAnnounceHealthAsync(), null,
            TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(1));
    }

    private async Task TickAnnounceHealthAsync()
    {
        if (_disposed || Interlocked.Exchange(ref _announceHealthTickRunning, 1) != 0) return;
        try
        {
            if (!_config.Health.Enabled)
            {
                _announceHealth.Tick([]); // Pauses exposure for previously monitored channels.
                return;
            }
            var observations = new List<AnnounceFeedObservation>();
            foreach (var server in _config.Servers.Where(s => s.Enabled && s.Irc.Enabled))
            {
                var irc = GetIrcService(server.Id);
                var channels = irc?.GetChannelSnapshot();
                foreach (var configured in server.Irc.Channels.Where(c => c.AutoJoin))
                {
                    var channel = channels?.FirstOrDefault(c => c.Name.Equals(configured.Name, StringComparison.OrdinalIgnoreCase));
                    observations.Add(AnnounceFeedHealthIntegration.Observation(server.Id, configured,
                        irc?.State == IrcServiceState.Connected, channel, irc?.GetDecryptFailureStreak(configured.Name) ?? 0));
                }
            }
            foreach (var notice in _announceHealth.Tick(observations)) PublishAnnounceNotice(notice);
            _announceHealth.Save();
            if (_config.Health.AnnounceRecoveryEnabled)
                foreach (var feed in _announceHealth.Snapshot().Where(f => f.State == "Down" && f.Connected && !f.Joined))
                {
                    if (_disposed || !_config.Health.Enabled || !_config.Health.AnnounceRecoveryEnabled) return;
                    // Invitations borrow existing pools; never awaken a down FTP site for a quiet channel.
                    bool StillAllowed() => !_disposed && _config.Health.AnnounceRecoveryEnabled &&
                        AnnounceFeedHealthIntegration.IsMonitored(_config, feed.ServerId, feed.Channel) &&
                        GetServer(feed.ServerId)?.CurrentState == MountState.Connected;
                    if (!StillAllowed()) continue;
                    var irc = GetIrcService(feed.ServerId);
                    if (irc != null && _announceHealth.TryBeginRecovery(feed.ServerId, feed.Channel))
                        await irc.TryRecoverAnnounceChannelAsync(feed.Channel, _announceHealthCancellation, StillAllowed).ConfigureAwait(false);
                }
        }
        catch (Exception ex) { Log.Warning(ex, "Announce feed supervision failed"); }
        finally { Volatile.Write(ref _announceHealthTickRunning, 0); }
    }

    private void ObserveAnnounceFeed(string serverId, string channel, DateTime observedAtUtc)
    {
        if (_disposed || !AnnounceFeedHealthIntegration.IsMonitored(_config, serverId, channel)) return;
        if (_announceHealth.ObserveAnnounce(serverId, channel, observedAtUtc) is { } notice) PublishAnnounceNotice(notice);
    }

    private void PublishAnnounceNotice(AnnounceFeedNotice notice)
    {
        if (!_config.Health.Enabled || !_config.Health.AnnounceSilenceAlertsEnabled || _disposed ||
            !AnnounceFeedHealthIntegration.IsMonitored(_config, notice.ServerId, notice.Channel)) return;
        Log.Information("Announce feed {Server}/{Channel}: {Status}; {Diagnosis}", notice.ServerId, notice.Channel,
            notice.Recovered ? "recovered" : "silent", notice.Diagnosis);
        if (AnnounceFeedNotice is not { } handlers) return;
        foreach (Action<AnnounceFeedNotice> handler in handlers.GetInvocationList())
            try { handler(notice); } catch (Exception ex) { Log.Warning(ex, "Announce health notice failed"); }
    }

    public IReadOnlyList<AnnounceFeedSnapshot> AnnounceHealth(string serverId) => _announceHealth.Snapshot()
        .Where(f => f.ServerId.Equals(serverId, StringComparison.Ordinal) &&
            AnnounceFeedHealthIntegration.IsMonitored(_config, f.ServerId, f.Channel)).ToArray();
    public bool AnnounceHealthPersistenceHealthy => _announceHealth.PersistenceHealthy;

    public sealed record LoginUsage(string Pool, long LeaseId, FtpBorrowPurpose Purpose, DateTimeOffset BorrowedAtUtc);
    public IReadOnlyList<LoginUsage> GetLoginUsage(string serverId)
    {
        var main = GetServer(serverId)?.Pool?.ActiveBorrows ?? [];
        var spread = _spreadManager?.GetLivePool(serverId)?.ActiveBorrows ?? [];
        return main.Select(b => new LoginUsage("Main", b.LeaseId, b.Purpose, b.BorrowedAtUtc))
            .Concat(spread.Select(b => new LoginUsage("Race", b.LeaseId, b.Purpose, b.BorrowedAtUtc))).ToArray();
    }

    private void StopAnnounceFeedHealth()
    {
        _announceHealthTimer?.Dispose();
        _announceHealthTimer = null;
        _announceHealthStop.Cancel();
        _announceHealthStop.Dispose();
        _announceHealth.Save();
    }
}

internal static class AnnounceFeedHealthIntegration
{
    internal static bool IsMonitored(AppConfig config, string serverId, string channel) => config.Health.Enabled &&
        config.Servers.Any(s => s.Id == serverId && s.Enabled && s.Irc.Enabled &&
            s.Irc.Channels.Any(c => c.AutoJoin && c.Name.Equals(channel, StringComparison.OrdinalIgnoreCase)));

    internal static AnnounceFeedObservation Observation(string serverId, IrcChannelConfig configured, bool connected,
        IrcChannel? channel, int failures)
    {
        bool? botPresent = !configured.Sitebot.Enabled || string.IsNullOrWhiteSpace(configured.Sitebot.BotNick) || channel == null
            ? null : channel.Nicks.Any(n => n.TrimStart('~', '&', '@', '%', '+')
                .Equals(configured.Sitebot.BotNick, StringComparison.OrdinalIgnoreCase));
        return new(serverId, configured.Name, connected, channel != null, botPresent, Math.Max(0, failures));
    }
}
