using System.Collections.Concurrent;
using Serilog;

namespace GlDrive.Ftp;

/// <summary>
/// Account-wide live-login cap (v3.6 Phase 1). A single glftpd account has a hard
/// simultaneous-login limit (commonly 3-4). GlDrive opens logins to that same
/// account from MANY independent pools — the WinFsp main pool, the spread FXP
/// pool, downloads, search, IRC SITE INVITE — and before this gate nothing tracked
/// the ACCOUNT-WIDE total, so the subsystems collectively blew the cap, glftpd
/// returned 530, connections were poisoned, and the per-pool BNC cooldown tripped.
/// Every pool to the same account now shares ONE gate (via
/// <see cref="ServerLoginGateRegistry"/>) and must hold a permit per live login.
///
/// A permit == one live TCP login. Acquire BEFORE opening a connection; release
/// when the connection is actually closed (quarantined/disposed) — NOT when an
/// idle connection is returned to its pool, because an idle pooled connection
/// still occupies a login at the BNC.
/// </summary>
public interface IAccountLoginGate
{
    /// <summary>Acquire one login permit, waiting up to <paramref name="timeout"/>. Returns false on timeout.</summary>
    Task<bool> TryAcquireAsync(CancellationToken ct, TimeSpan? timeout = null);
    /// <summary>Release one previously-acquired permit. Over-release is ignored (logged).</summary>
    void Release();
    /// <summary>Permits currently held by callers.</summary>
    int Held { get; }
    /// <summary>Current effective limit (shrinks via <see cref="TightenTo"/>).</summary>
    int Limit { get; }
    /// <summary>Shrink-only: lower the effective limit by permanently parking permits.</summary>
    void TightenTo(int newLimit);
}

public sealed class ServerLoginGate : IAccountLoginGate
{
    private readonly SemaphoreSlim _sem;
    private readonly int _maxLimit;
    private int _limit;     // current effective limit (shrink-only)
    private int _held;      // permits acquired by callers (not counting parked ballast)
    private int _parked;    // permits permanently parked by TightenTo
    private readonly object _lock = new();
    public string Key { get; }

    public ServerLoginGate(string key, int limit, int maxLimit)
    {
        Key = key;
        _maxLimit = Math.Max(1, maxLimit);
        _limit = Math.Clamp(limit, 1, _maxLimit);
        // SemaphoreSlim capacity == maxLimit; start with _limit available, the rest
        // pre-parked so the effective ceiling is _limit until TightenTo lowers it.
        _sem = new SemaphoreSlim(_limit, _maxLimit);
        _parked = _maxLimit - _limit;
    }

    public int Held => Volatile.Read(ref _held);
    public int Limit { get { lock (_lock) return _limit; } }

    public async Task<bool> TryAcquireAsync(CancellationToken ct, TimeSpan? timeout = null)
    {
        bool ok;
        try
        {
            ok = await _sem.WaitAsync(timeout ?? Timeout.InfiniteTimeSpan, ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        if (ok) Interlocked.Increment(ref _held);
        return ok;
    }

    public void Release()
    {
        // Guard against over-release (a permit-accounting bug must never push the
        // semaphore above its real ceiling or free a login we don't hold).
        if (Interlocked.Decrement(ref _held) < 0)
        {
            Interlocked.Increment(ref _held); // undo
            Log.Warning("ServerLoginGate[{Key}]: over-release ignored (held would go negative)", Key);
            return;
        }
        try { _sem.Release(); }
        catch (SemaphoreFullException) { /* already at ceiling — nothing to free */ }
    }

    /// <summary>
    /// Shrink-only. Lower the effective limit by permanently acquiring (parking)
    /// the difference — those permits never return, so live logins can't exceed
    /// the new limit. No-op if <paramref name="newLimit"/> ≥ current. Called when a
    /// BNC's 530 reveals its real cap is lower than configured.
    /// </summary>
    public void TightenTo(int newLimit)
    {
        int toPark;
        lock (_lock)
        {
            newLimit = Math.Clamp(newLimit, 1, _maxLimit);
            if (newLimit >= _limit) return;
            toPark = _limit - newLimit;
            _limit = newLimit;
            _parked += toPark;
        }
        // Park permits off-thread: WaitAsync may block if all are currently held,
        // but as connections close and release, the parking absorbs the permits and
        // the effective ceiling settles at the new limit. Parked permits are never
        // released.
        for (var i = 0; i < toPark; i++)
            _ = _sem.WaitAsync();
        Log.Information("ServerLoginGate[{Key}]: tightened to {Limit} (parked {Parked} total)", Key, newLimit, _parked);
    }
}

/// <summary>
/// Process-wide registry of one <see cref="ServerLoginGate"/> per glftpd ACCOUNT
/// (keyed host:port:username, case-insensitive host/user). Every pool that logs
/// into the same account resolves the SAME gate here — that shared instance is
/// what makes the cap account-wide instead of per-pool.
/// </summary>
public static class ServerLoginGateRegistry
{
    private static readonly ConcurrentDictionary<string, ServerLoginGate> Gates = new();

    public static string KeyFor(string host, int port, string user) =>
        $"{host.ToLowerInvariant()}:{port}:{user.ToLowerInvariant()}";

    /// <summary>
    /// Get (or create) the shared gate for an account. The usable limit is
    /// <paramref name="cap"/> − <paramref name="headroom"/> (min 1), leaving headroom
    /// for transient ungated logins (e.g. ghost-kill). The first caller's cap/headroom
    /// win; later callers for the same account reuse the existing gate.
    /// </summary>
    public static ServerLoginGate GetOrCreate(string host, int port, string user, int cap, int headroom)
    {
        var key = KeyFor(host, port, user);
        return Gates.GetOrAdd(key, k =>
        {
            var usable = Math.Max(1, cap - Math.Max(0, headroom));
            Log.Information("ServerLoginGate[{Key}]: created (cap={Cap}, headroom={Head}, usable={Usable})",
                k, cap, headroom, usable);
            return new ServerLoginGate(k, usable, usable);
        });
    }

    /// <summary>Test/diagnostic hook — look up an existing gate without creating one.</summary>
    public static ServerLoginGate? TryGet(string host, int port, string user) =>
        Gates.TryGetValue(KeyFor(host, port, user), out var g) ? g : null;
}
