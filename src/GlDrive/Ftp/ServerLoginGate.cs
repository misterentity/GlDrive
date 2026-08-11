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
    /// <summary>
    /// Acquire one login permit, waiting up to <paramref name="timeout"/>. Returns false on timeout.
    /// <paramref name="priority"/> callers (the FXP/spread pool) may draw from the reserved
    /// pool; non-priority callers (the WinFsp main pool) are capped at limit−reserved so a
    /// permit is always available for racing.
    /// </summary>
    Task<bool> TryAcquireAsync(CancellationToken ct, TimeSpan? timeout = null, bool priority = false);
    /// <summary>Release one previously-acquired permit. Over-release is ignored (logged). The
    /// <paramref name="priority"/> flag MUST match the value passed to the matching acquire.</summary>
    void Release(bool priority = false);
    /// <summary>Permits currently held by callers.</summary>
    int Held { get; }
    /// <summary>Current effective limit (shrinks via <see cref="TightenTo"/>).</summary>
    int Limit { get; }
    /// <summary>Permits reserved for priority (FXP) callers — non-priority callers can't take these.</summary>
    int Reserved { get; }
    /// <summary>
    /// Ceiling on live logins a PRIORITY (FXP/spread) caller can ever hold. This — not a
    /// pool's nominal MaxSize — is the number that bounds FXP concurrency, and callers
    /// sizing themselves against pool capacity instead of this silently oversubscribe
    /// (v3.10.54: the scan-yield guard compared against a pool of 10 while the gate
    /// would only ever grant 1, so it never fired in 534 evaluations).
    /// </summary>
    int PriorityLimit { get; }
    /// <summary>Ceiling on live logins a NON-priority (main mount pool) caller can ever hold.</summary>
    int GeneralLimit { get; }
    /// <summary>Shrink-only: lower the effective limit by permanently parking permits.</summary>
    void TightenTo(int newLimit);
}

public sealed class ServerLoginGate : IAccountLoginGate
{
    // _sem governs the TOTAL live-login ceiling (the account cap). Every caller —
    // priority or not — holds one _sem permit per login, and Release always returns
    // one to _sem, so Held/Limit/TightenTo accounting is unchanged from the original.
    private readonly SemaphoreSlim _sem;
    // _general is a SUB-cap that only NON-priority callers (the main pool) must also
    // hold. Its ceiling is limit−reserved, so non-priority callers can never occupy
    // more than that many of _sem's permits — guaranteeing `reserved` permits in _sem
    // always remain obtainable by a priority (FXP/spread) caller. With reserved=0,
    // _general mirrors _sem exactly and behavior is identical to the pre-reservation gate.
    private readonly SemaphoreSlim _general;
    // _priority is the MIRROR sub-cap, and it exists because the original reservation
    // was one-directional (v3.10.50). Priority callers used to wait on _sem alone, so
    // the spread pool could occupy EVERY usable login and leave the main mount pool
    // nothing — the exact inverse of the v3.7.2 bug where the main pool squatted all
    // the permits FXP needed. On 2026-08-07 SYN sat at usable=3 with three live spread
    // logins while its mount retried 226 times. Its ceiling is limit−MainReserved, so
    // priority callers can never take the last login the mount needs to exist at all.
    private readonly SemaphoreSlim _priority;
    private readonly int _maxLimit;
    private readonly int _reserved;
    private readonly int _mainReserved;
    private int _limit;         // current effective TOTAL limit (shrink-only)
    private int _generalLimit;  // current non-priority sub-cap (= _limit − _reserved)
    private int _priorityLimit; // current priority sub-cap (= _limit − _mainReserved)
    private int _held;          // permits acquired by callers (not counting parked ballast)
    private int _parked;        // _sem permits permanently parked by TightenTo
    private int _generalParked; // _general permits permanently parked by TightenTo
    private int _priorityParked;// _priority permits permanently parked by TightenTo
    private readonly object _lock = new();
    public string Key { get; }

    public ServerLoginGate(string key, int limit, int maxLimit, int reserved = 0)
    {
        Key = key;
        _maxLimit = Math.Max(1, maxLimit);
        _limit = Math.Clamp(limit, 1, _maxLimit);
        // Never reserve so much that the main pool can't get a single login.
        _reserved = Math.Clamp(reserved, 0, _limit - 1);
        // Mirror guarantee: one login is always obtainable by the main pool, so a
        // busy spread pool can never make a site unmountable. Only meaningful once
        // there are at least two logins to divide — at limit=1 the main pool is the
        // only caller that matters and priority must still be able to run.
        _mainReserved = _limit >= 2 ? 1 : 0;
        // SemaphoreSlim capacity == maxLimit; start with _limit available, the rest
        // pre-parked so the effective ceiling is _limit until TightenTo lowers it.
        _sem = new SemaphoreSlim(_limit, _maxLimit);
        _parked = _maxLimit - _limit;
        _generalLimit = _limit - _reserved;
        _general = new SemaphoreSlim(_generalLimit, _maxLimit);
        _generalParked = _maxLimit - _generalLimit;
        _priorityLimit = _limit - _mainReserved;
        _priority = new SemaphoreSlim(_priorityLimit, _maxLimit);
        _priorityParked = _maxLimit - _priorityLimit;
    }

    public int Held => Volatile.Read(ref _held);
    public int Limit { get { lock (_lock) return _limit; } }
    public int Reserved => _reserved;
    /// <summary>Permits reserved for the main mount pool — priority callers can't take these.</summary>
    public int MainReserved => _mainReserved;
    public int PriorityLimit { get { lock (_lock) return _priorityLimit; } }
    public int GeneralLimit { get { lock (_lock) return _generalLimit; } }

    public async Task<bool> TryAcquireAsync(CancellationToken ct, TimeSpan? timeout = null, bool priority = false)
    {
        var wait = timeout ?? Timeout.InfiniteTimeSpan;
        try
        {
            // Both roles hold their own sub-cap permit AND a total permit. The two
            // sub-caps sum to the total limit, so each role is guaranteed its
            // reservation and neither can starve the other.
            var sub = priority ? _priority : _general;
            if (!await sub.WaitAsync(wait, ct)) return false;
            bool gotSem;
            try { gotSem = await _sem.WaitAsync(wait, ct); }
            catch (OperationCanceledException) { SafeRelease(sub); throw; }
            if (!gotSem) { SafeRelease(sub); return false; }
            Interlocked.Increment(ref _held);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public void Release(bool priority = false)
    {
        // Guard against over-release (a permit-accounting bug must never push the
        // semaphore above its real ceiling or free a login we don't hold).
        if (Interlocked.Decrement(ref _held) < 0)
        {
            Interlocked.Increment(ref _held); // undo
            Log.Warning("ServerLoginGate[{Key}]: over-release ignored (held would go negative)", Key);
            return;
        }
        SafeRelease(_sem);
        SafeRelease(priority ? _priority : _general);
    }

    private static void SafeRelease(SemaphoreSlim sem)
    {
        try { sem.Release(); }
        catch (SemaphoreFullException) { /* already at ceiling — nothing to free */ }
    }

    /// <summary>
    /// Shrink-only. Lower the effective limit by permanently acquiring (parking)
    /// the difference — those permits never return, so live logins can't exceed
    /// the new limit. No-op if <paramref name="newLimit"/> ≥ current. Called when a
    /// BNC's 530 reveals its real cap is lower than configured. The reserved FXP
    /// permits are preserved: the non-priority sub-cap shrinks alongside the total.
    /// </summary>
    public void TightenTo(int newLimit)
    {
        int toPark, generalToPark, priorityToPark;
        lock (_lock)
        {
            newLimit = Math.Clamp(newLimit, 1, _maxLimit);
            if (newLimit >= _limit) return;
            toPark = _limit - newLimit;
            _limit = newLimit;
            _parked += toPark;

            // Both reservations shrink with the total, but a reservation may never
            // consume the whole limit: at limit=1 the old code computed a general
            // sub-cap of max(0, 1−2)=0, which silently deadlocked the main pool —
            // a tighten meant to protect the account instead closed it. Each side
            // keeps at least one obtainable permit, so a tightened gate degrades to
            // "both roles contend for the last login" rather than "nobody may log in".
            var newGeneralLimit = Math.Max(1, _limit - _reserved);
            generalToPark = _generalLimit - newGeneralLimit;
            _generalLimit = newGeneralLimit;
            _generalParked += generalToPark;

            var newPriorityLimit = Math.Max(1, _limit - _mainReserved);
            priorityToPark = _priorityLimit - newPriorityLimit;
            _priorityLimit = newPriorityLimit;
            _priorityParked += priorityToPark;
        }
        // Park permits off-thread: WaitAsync may block if all are currently held,
        // but as connections close and release, the parking absorbs the permits and
        // the effective ceiling settles at the new limit. Parked permits are never released.
        for (var i = 0; i < toPark; i++)
            _ = _sem.WaitAsync();
        for (var i = 0; i < generalToPark; i++)
            _ = _general.WaitAsync();
        for (var i = 0; i < priorityToPark; i++)
            _ = _priority.WaitAsync();
        Log.Information("ServerLoginGate[{Key}]: tightened to {Limit} (parked {Parked} total, fxpSubCap {Fxp}, mainSubCap {Main})",
            Key, newLimit, _parked, _priorityLimit, _generalLimit);
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
            // Keep one login for the main pool and reserve up to two for FXP. A
            // transfer holds one priority login per participating account, and the
            // race engine allows two concurrent races in production. The old single
            // reservation let the main pool retain 2 of 3 usable logins, so every
            // second FXP borrow deterministically timed out at the gate. With only
            // 1 usable login there is nothing to reserve (main needs it).
            var reserved = Math.Min(2, usable - 1);
            Log.Information("ServerLoginGate[{Key}]: created (cap={Cap}, headroom={Head}, usable={Usable}, fxpReserved={Reserved})",
                k, cap, headroom, usable, reserved);
            return new ServerLoginGate(k, usable, usable, reserved);
        });
    }

    /// <summary>Test/diagnostic hook — look up an existing gate without creating one.</summary>
    public static ServerLoginGate? TryGet(string host, int port, string user) =>
        Gates.TryGetValue(KeyFor(host, port, user), out var g) ? g : null;
}
