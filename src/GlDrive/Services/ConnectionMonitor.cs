using FluentFTP.Exceptions;
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
    private int _healthCheckCount;

    public event Action? ConnectionLost;
    public event Action? ConnectionRestored;
    public event Action<string>? BncRateLimitDetected;
    public Action? PeriodicMetricsCallback { get; set; }

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

    public async Task StopAsync(TimeSpan? timeout = null)
    {
        _cts?.Cancel();
        if (_monitorTask != null)
        {
            try
            {
                await _monitorTask.WaitAsync(timeout ?? TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                Log.Warning("ConnectionMonitor stop timed out — abandoning background task");
            }
            catch { }
        }
        _cts?.Dispose();
        _cts = null;
    }

    public void Stop()
    {
        _cts?.Cancel();
        // Fire-and-forget — don't block the calling thread
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

                // Log periodic metrics every ~5 minutes (10 cycles at 30s interval)
                if (++_healthCheckCount % 10 == 0)
                    PeriodicMetricsCallback?.Invoke();

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
                // Detect BNC rate limiting (421 = service not available, 450 = too many connections)
                if (ex is FtpCommandException ftpEx &&
                    (ftpEx.CompletionCode is "421" or "450"))
                {
                    var bncCooldown = 7200; // 2 hours in seconds
                    Log.Warning("BNC rate-limit detected ({Code}: {Message}) — backing off for {Cooldown}s",
                        ftpEx.CompletionCode, ftpEx.Message, bncCooldown);
                    BncRateLimitDetected?.Invoke(
                        $"BNC rate-limit ({ftpEx.CompletionCode}) — cooldown ~2 hours");
                    delay = bncCooldown;
                }
                else
                {
                    Log.Warning(ex, "Reconnect attempt failed");
                    delay = Math.Min(delay * 2, maxDelay);
                }
            }
        }
    }
}
