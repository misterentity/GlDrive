using Serilog;

namespace GlDrive.AiAgent;

public sealed class HealthRollup : IDisposable
{
    private readonly TelemetryRecorder _recorder;
    private readonly Services.ServerManager _servers;
    private readonly Timer _timer;
    private DateTime _windowStart = DateTime.UtcNow;

    public HealthRollup(TelemetryRecorder recorder, Services.ServerManager servers)
    {
        _recorder = recorder;
        _servers = servers;
        _timer = new Timer(_ => RollUp(), null, TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(60));
    }

    public void RollUp()
    {
        try
        {
            var now = DateTime.UtcNow;
            foreach (var ms in _servers.GetAllMountServices())
            {
                var pool = ms.Pool;
                if (pool is null) continue;

                // Flush first to snapshot counters into the public properties
                pool.FlushHealthCounters();

                _recorder.Record(TelemetryStream.SiteHealth, new SiteHealthEvent
                {
                    ServerId = ms.ServerId,
                    WindowStart = _windowStart.ToString("O"),
                    WindowEnd = now.ToString("O"),
                    AvgConnectMs = pool.AvgConnectMs,
                    P99ConnectMs = pool.P99ConnectMs,
                    Disconnects = pool.DisconnectsSinceFlush,
                    TlsHandshakeMs = pool.AvgTlsHandshakeMs,
                    PoolExhaustCount = pool.ExhaustCountSinceFlush,
                    GhostKills = pool.GhostKillsSinceFlush,
                    Errors5xx = pool.Errors5xxSinceFlush,
                    ReinitCount = pool.ReinitCountSinceFlush
                });
            }
            _windowStart = now;
        }
        catch (Exception ex) { Log.Warning(ex, "HealthRollup failed"); }
    }

    public void Dispose() => _timer.Dispose();
}
