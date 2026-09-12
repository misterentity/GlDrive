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
    // native recv (see ProbeConnectionAsync for why cancelling it is fatal).
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
                Log.Warning("ConnectionMonitor[{Server}] stop timed out — abandoning background task", _factory.Host);
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
                string? failure = null;
                try
                {
                    await CheckHealthAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failure = ex.Message;
                }
                ct.ThrowIfCancellationRequested();

                // Log periodic metrics every ~5 minutes (10 cycles at 30s interval)
                if (++_healthCheckCount % 10 == 0)
                    PeriodicMetricsCallback?.Invoke();

                if (failure == null && !_wasConnected)
                {
                    _wasConnected = true;
                    Log.Information("ConnectionMonitor[{Server}]: connection restored", _factory.Host);
                    ConnectionRestored?.Invoke();
                }
                else if (failure != null && _wasConnected)
                {
                    _wasConnected = false;
                    Log.Warning("ConnectionMonitor[{Server}]: connection lost ({Reason}), attempting reconnect...",
                        _factory.Host, failure);
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
                Log.Debug(ex, "ConnectionMonitor[{Server}]: monitor loop error", _factory.Host);
            }
        }
    }

    private async Task CheckHealthAsync(CancellationToken ct)
    {
        await using var conn = await _pool.Borrow(ct);
        await ProbeConnectionAsync(conn, token => conn.Client.Execute("NOOP", token), ct);
    }

    internal static async Task ProbeConnectionAsync(
        PooledConnection conn, Func<CancellationToken, Task<FluentFTP.FtpReply>> executeNoop,
        CancellationToken ct, TimeSpan? timeout = null)
    {
        ct.ThrowIfCancellationRequested();
        Task<FluentFTP.FtpReply>? noop = null;
        try
        {
            // Cancel only the managed wait. Cancelling the native GnuTLS recv can race
            // session teardown and crash the process. Failed/abandoned reads must use
            // the pool's deferred quarantine, including cancellation during shutdown.
            noop = executeNoop(CancellationToken.None);
            var reply = await noop.WaitAsync(timeout ?? NoopTimeout, ct);
            ct.ThrowIfCancellationRequested();
            // Execute returns negative FTP replies normally; awaiting it is not proof
            // of health. NOOP requires a positive completion (not a 1xx/3xx reply).
            if (reply.Code?.StartsWith("2", StringComparison.Ordinal) != true)
                throw new FtpCommandException(reply);
        }
        catch
        {
            conn.Poison("NOOP probe failed");
            if (noop != null)
                _ = noop.ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);
            throw;
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
                Log.Information("ConnectionMonitor[{Server}]: reconnecting in {Delay}s...", _factory.Host, delay);
                await Task.Delay(TimeSpan.FromSeconds(delay), ct);

                await _pool.Initialize(ct);
                // Initialize is a no-op when another connection already exists. Verify
                // a real reply before announcing recovery, even in a nonempty pool.
                await CheckHealthAsync(ct);
                ct.ThrowIfCancellationRequested();
                _wasConnected = true;
                Log.Information("ConnectionMonitor[{Server}]: reconnected successfully", _factory.Host);
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
                    Log.Warning("ConnectionMonitor[{Server}]: BNC rate-limit detected ({Code}: {Message}) — backing off for {Cooldown}s",
                        _factory.Host, ftpEx.CompletionCode, ftpEx.Message, bncCooldown);
                    BncRateLimitDetected?.Invoke(
                        $"BNC rate-limit ({ftpEx.CompletionCode}) — cooldown ~2 hours");
                    delay = bncCooldown;
                }
                else
                {
                    Log.Warning(ex, "ConnectionMonitor[{Server}]: reconnect attempt failed", _factory.Host);
                    delay = Math.Min(delay * 2, maxDelay);
                }
            }
        }
    }
}
