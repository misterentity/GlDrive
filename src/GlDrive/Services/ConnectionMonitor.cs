using GlDrive.Config;
using GlDrive.Ftp;
using Serilog;

namespace GlDrive.Services;

public class ConnectionMonitor
{
    private readonly FtpConnectionPool _pool;
    private readonly FtpClientFactory _factory;
    private readonly PoolConfig _config;
    private CancellationTokenSource? _cts;
    private Task? _monitorTask;
    private bool _wasConnected = true;

    public event Action? ConnectionLost;
    public event Action? ConnectionRestored;

    public ConnectionMonitor(FtpConnectionPool pool, FtpClientFactory factory, PoolConfig config)
    {
        _pool = pool;
        _factory = factory;
        _config = config;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _monitorTask = MonitorLoop(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _monitorTask?.GetAwaiter().GetResult(); } catch { }
        _cts?.Dispose();
        _cts = null;
    }

    private async Task MonitorLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_config.KeepaliveIntervalSeconds), ct);

                // Health check via NOOP
                bool healthy;
                try
                {
                    await using var conn = await _pool.Borrow(ct);
                    await conn.Client.Execute("NOOP", ct);
                    healthy = true;
                }
                catch
                {
                    healthy = false;
                }

                if (healthy && !_wasConnected)
                {
                    _wasConnected = true;
                    Log.Information("Connection restored");
                    ConnectionRestored?.Invoke();
                }
                else if (!healthy && _wasConnected)
                {
                    _wasConnected = false;
                    Log.Warning("Connection lost, attempting reconnect...");
                    ConnectionLost?.Invoke();
                    await AttemptReconnect(ct);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Monitor loop error");
            }
        }
    }

    private async Task AttemptReconnect(CancellationToken ct)
    {
        var delay = _config.ReconnectInitialDelaySeconds;
        var maxDelay = _config.ReconnectMaxDelaySeconds;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                Log.Information("Reconnecting in {Delay}s...", delay);
                await Task.Delay(TimeSpan.FromSeconds(delay), ct);

                await _pool.Initialize(ct);
                _wasConnected = true;
                Log.Information("Reconnected successfully");
                ConnectionRestored?.Invoke();
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Reconnect attempt failed");
                delay = Math.Min(delay * 2, maxDelay);
            }
        }
    }
}
