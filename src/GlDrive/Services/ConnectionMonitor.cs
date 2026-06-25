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

    // Managed timeout for the keepalive NOOP. Kept under FluentFTP.GnuTLS's 15s
    // CommTimeout floor so the health check yields promptly without cancelling the
    // native recv (see MonitorLoop for why cancelling it is fatal).
    private static readonly TimeSpan NoopTimeout = TimeSpan.FromSeconds(10);

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
        // Don't dispose CTS here — MonitorLoop may still be running
        // It will be disposed in StopAsync or on next Start()
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
                    try
                    {
                        // Managed-timeout NOOP with a NON-cancellable underlying read.
                        // Passing a cancellable token into FluentFTP's read abandons the
                        // native GnuTLS recv() mid-syscall; if the pool then tears the
                        // connection down while that recv drains, GnuTlsInternalStream.Read
                        // faults with an uncatchable AccessViolationException that kills the
                        // whole process (dominant crash signature, Event Log .NET Runtime
                        // id 1026). On timeout we leave the read draining (observing its
                        // exception) and poison the connection so the pool's DEFERRED
                        // teardown reclaims it only after the recv has finished.
                        var noop = conn.Client.Execute("NOOP", CancellationToken.None);
                        var winner = await Task.WhenAny(noop, Task.Delay(NoopTimeout, ct));
                        if (winner == noop)
                        {
                            await noop;          // surface a genuine NOOP failure as unhealthy
                            healthy = true;
                        }
                        else
                        {
                            _ = noop.ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);
                            ct.ThrowIfCancellationRequested(); // propagate a real shutdown cancel
                            conn.Poisoned = true;              // draining recv — defer teardown
                            healthy = false;
                        }
                    }
                    catch
                    {
                        // NOOP failed (dropped / SSL fault). The session may be mid-recv or
                        // corrupt — poison so the pool discards it via the deferred path
                        // rather than returning a possibly-live connection for reuse.
                        conn.Poisoned = true;
                        healthy = false;
                    }
                }
                catch
                {
                    // Borrow itself failed (pool exhausted / cooldown) — nothing to poison.
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
