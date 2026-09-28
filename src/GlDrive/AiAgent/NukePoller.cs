using System.IO;
using System.Text.Json;
using FluentFTP;
using FluentFTP.Exceptions;
using GlDrive.Ftp;
using GlDrive.Services;
using Serilog;

namespace GlDrive.AiAgent;

public sealed class NukePoller : IDisposable
{
    private readonly TelemetryRecorder _recorder;
    private readonly Func<IEnumerable<PollTarget>> _getTargets;
    private readonly NukeCursorStore _cursors;
    private readonly string _aiDataRoot;
    private readonly int _intervalHours;
    private readonly Timer _timer;
    private readonly Dictionary<string, int> _failCount = new();
    private readonly Dictionary<string, DateTime> _retryAfter = new();
    private readonly Func<DateTime> _utcNow;
    private readonly TimeSpan _pollTimeout;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _lifecycleLock = new();
    private bool _polling;
    private bool _disposed;
    private const int BreakerThreshold = 3;

    internal sealed record PollTarget(string ServerId, Func<bool> IsAvailable,
        Func<CancellationToken, Task<FtpReply>> ReadNukes);

    public NukePoller(TelemetryRecorder recorder, Services.ServerManager servers,
                      NukeCursorStore cursors, string aiDataRoot, int intervalHours)
        : this(recorder, () => servers.GetAllMountServices().Select(CreateTarget),
            cursors, aiDataRoot, intervalHours)
    {
    }

    internal static PollTarget CreateTarget(MountService ms) => new(ms.ServerId,
        // A pool being initialized is not "exhausted", but borrowing it would
        // start another login alongside the mount's own recovery attempt.
        () => ms.CurrentState == MountState.Connected && ms.Pool is { IsExhausted: false },
        ct => ReadNukesAsync(ms.Pool!, ct));

    internal NukePoller(TelemetryRecorder recorder, Func<IEnumerable<PollTarget>> getTargets,
        NukeCursorStore cursors, string aiDataRoot, int intervalHours,
        Func<DateTime>? utcNow = null, TimeSpan? pollTimeout = null, bool startTimer = true)
    {
        _recorder = recorder;
        _getTargets = getTargets;
        _cursors = cursors;
        _aiDataRoot = aiDataRoot;
        _intervalHours = Math.Max(1, intervalHours);
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _pollTimeout = pollTimeout ?? TimeSpan.FromSeconds(30);

        // First fire 5 minutes after startup; subsequent every interval hours.
        _timer = new Timer(async _ =>
        {
            try { await PollAllAsync(); }
            catch (Exception ex) { Log.Error(ex, "NukePoller.PollAllAsync unhandled"); }
        }, null, startTimer ? TimeSpan.FromMinutes(5) : Timeout.InfiniteTimeSpan,
            TimeSpan.FromHours(_intervalHours));
    }

    public async Task PollAllAsync()
    {
        CancellationToken shutdown;
        lock (_lifecycleLock)
        {
            if (_disposed || _polling) return;
            _polling = true;
            shutdown = _shutdown.Token;
        }
        try
        {
            foreach (var target in _getTargets())
            {
                if (shutdown.IsCancellationRequested) break;
                var serverId = target.ServerId;
                if (_retryAfter.TryGetValue(serverId, out var retryAfter) && _utcNow() < retryAfter)
                {
                    Log.Debug("NukePoller circuit open for {Server}", serverId);
                    continue;
                }
                try
                {
                    if (!target.IsAvailable()) continue;

                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
                    cts.CancelAfter(_pollTimeout);
                    var reply = await target.ReadNukes(cts.Token);
                    cts.Token.ThrowIfCancellationRequested();
                    if (!reply.Success) { BumpFail(serverId); continue; }

                    // Parse. glftpd returns multi-line output in InfoMessages.
                    var raw = reply.InfoMessages ?? reply.Message ?? "";
                    var nukes = NukeParser.Parse(raw).ToList();

                    var cursor = _cursors.Get(serverId);
                    var newCursor = cursor;

                    foreach (var n in nukes)
                    {
                        if (n.NukedAt <= cursor) continue;
                        var ourRef = TryCorrelateRace(n.Release);
                        _recorder.Record(TelemetryStream.Nukes, new NukeDetectedEvent
                        {
                            ServerId = serverId,
                            Section = n.Section,
                            Release = n.Release,
                            NukedAt = n.NukedAt.ToString("O"),
                            Nuker = n.Nuker,
                            Reason = n.Reason,
                            Multiplier = n.Multiplier,
                            OurRaceRef = ourRef
                        });
                        if (n.NukedAt > newCursor) newCursor = n.NukedAt;
                    }
                    if (newCursor > cursor) _cursors.Set(serverId, newCursor);
                    _failCount[serverId] = 0;  // reset breaker on success
                    _retryAfter.Remove(serverId);
                }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "NukePoller failed for {Server}", serverId);
                    BumpFail(serverId);
                }
            }
        }
        finally
        {
            lock (_lifecycleLock)
            {
                _polling = false;
                if (_disposed) _shutdown.Dispose();
            }
        }
    }

    private static async Task<FtpReply> ReadNukesAsync(FtpConnectionPool pool, CancellationToken ct)
    {
        await using var lease = await pool.Borrow(ct);
        return await ExecuteNukesAsync(lease, token => lease.Client.Execute("SITE NUKES", token), ct);
    }

    internal static async Task<FtpReply> ExecuteNukesAsync(PooledConnection lease,
        Func<CancellationToken, Task<FtpReply>> execute, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Task<FtpReply>? command = null;
        try
        {
            // Bound the managed wait, never the native GnuTLS read. If abandoned,
            // the pool's deferred quarantine owns teardown of the pending session.
            command = execute(CancellationToken.None);
            var reply = await command.WaitAsync(ct);
            ct.ThrowIfCancellationRequested();
            if (reply.Code == "421" || string.IsNullOrEmpty(reply.Code) ||
                reply.Code[0] is not ('2' or '4' or '5'))
                throw new FtpCommandException(reply);
            return reply; // Clean final denials (e.g. 500/550) keep a reusable session.
        }
        catch
        {
            lease.Poison("SITE NUKES failed or interrupted");
            if (command != null)
                _ = command.ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);
            throw;
        }
    }

    private void BumpFail(string serverId)
    {
        var newCount = _failCount[serverId] = Math.Min(BreakerThreshold,
            _failCount.GetValueOrDefault(serverId) + 1);
        if (newCount >= BreakerThreshold)
        {
            // Failure is not permanent evidence that SITE NUKES is unavailable.
            // Permit another probe once one polling interval has elapsed, including
            // after remote recovery, while suppressing manual retries during cooldown.
            _retryAfter[serverId] = _utcNow().AddHours(_intervalHours);
            Log.Warning("NukePoller breaker opened for {Server} after {N} failures; retry after {RetryAfter}",
                serverId, newCount, _retryAfter[serverId]);
        }
    }

    /// <summary>Scan today's races jsonl for a matching release name; return the raceId if found.</summary>
    private string? TryCorrelateRace(string release)
    {
        try
        {
            var racesFile = Path.Combine(_aiDataRoot, $"races-{DateTime.Now:yyyyMMdd}.jsonl");
            if (!File.Exists(racesFile)) return null;
            foreach (var line in File.ReadLines(racesFile))
            {
                if (!line.Contains($"\"release\":\"{release}\"", StringComparison.Ordinal)) continue;
                RaceOutcomeEvent? r;
                try { r = JsonSerializer.Deserialize<RaceOutcomeEvent>(line); }
                catch (Exception ex)
                {
                    // Correlation is best-effort, but a row that fails to parse here also
                    // fails everywhere else that reads races-*.jsonl — say so once rather
                    // than dropping it silently.
                    Log.Warning(ex, "NukePoller race-correlation parse skip in {File}", racesFile);
                    continue;
                }
                if (r?.Release == release) return r.RaceId;
            }
        }
        catch { /* best-effort */ }
        return null;
    }

    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Dispose();
            _shutdown.Cancel();
            if (!_polling) _shutdown.Dispose();
        }
    }
}
