using System.Net.Sockets;
using System.Reflection;
using System.Threading.Channels;
using FluentFTP;
using FluentFTP.Client.BaseClient;
using Serilog;

namespace GlDrive.Ftp;

public class FtpConnectionPool : IAsyncDisposable
{
    private readonly FtpClientFactory _factory;
    private readonly Channel<AsyncFtpClient> _pool;
    private int _maxSize;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private int _created;
    private int _active;
    private bool _disposed;
    // Ghost-kill is fired AT MOST ONCE per pressure episode. 0 = available,
    // 1 = already used since the last successful connect. Reset by RecordConnect.
    // Rationale: the first login-limit error gets one !entity ghost-kill to clear
    // genuinely-stale BNC sessions. If the limit persists, the sessions aren't
    // ghosts — they're our own live connections — so repeating the kill just
    // spams !entity logins, which the BNC reads as a reconnect storm and answers
    // with a multi-minute cooldown ("actively refused"). Observed 2026-05-20:
    // 6 kills in 4 minutes tripped the cooldown and stalled an active race.
    private int _ghostKilledSinceSuccess;

    // PRD H2 — BNC cooldown. When the server starts refusing connections at the
    // TCP level ("actively refused") or keeps returning 530 AFTER we already
    // spent our one ghost-kill this episode, the BNC has tripped its own
    // reconnect cooldown. Hammering it with more connection attempts only
    // prolongs the refusal (observed 2026-05-20). Park new-connection creation
    // for CooldownWindow; existing pooled connections are still served.
    private long _refusedUntilTicks;
    private static readonly TimeSpan CooldownWindow = TimeSpan.FromSeconds(90);
    // Shorter backoff for a login-GATE timeout (the account is at its simultaneous-login
    // cap and no permit freed within PermitAcquireTimeout). This is the dominant storm
    // failure and is transient (a slot frees when an active transfer ends), so the
    // cooldown is short — long enough to stop Borrow re-spinning a doomed 30s gate wait
    // every cycle (the v3.8.9 churn that pinned the quarantine teardown queue), short
    // enough to resume racing promptly. RecordConnect clears it on the next success.
    private static readonly TimeSpan LoginGateCooldown = TimeSpan.FromSeconds(20);

    // Health counters — flushed hourly by HealthRollup
    public double AvgConnectMs { get; private set; }
    public double P99ConnectMs { get; private set; }
    public int DisconnectsSinceFlush { get; private set; }
    public double AvgTlsHandshakeMs { get; private set; }  // always 0 — TLS buried inside FluentFTP Connect
    public int ExhaustCountSinceFlush { get; private set; }
    public int GhostKillsSinceFlush { get; private set; }
    public int Errors5xxSinceFlush { get; private set; }   // TODO: wire IncrementError5xx when 5xx signal is plumbed
    public int ReinitCountSinceFlush { get; private set; }

    private const int MaxHealthSamples = 1000;
    private readonly List<double> _connectMsSamples = new();
    private readonly List<double> _tlsMsSamples = new();   // reserved for future TLS timing

    private int _disconnects, _exhaustCount, _ghostKills, _errors5xx, _reinitCount;

    // Poisoned-connection quarantine. After mid-transfer SSL errors ("forcibly
    // closed by the remote host"), or any teardown of a connection whose native
    // GnuTLS recv may still be in flight, calling client.Dispose() runs
    // gnutls_deinit() — and if that frees the session UNDER a live recv inside
    // GnuTlsInternalStream.Read it SEGVs the process, bypassing every managed
    // handler (AppDomain.UnhandledException fires zero times; no crashdump is
    // written — the 2026-05/06 watchdog-restart storm, escalating to 7/day at
    // v3.8.9). The ONLY safe teardown is time-based: never touch the socket or
    // session while a recv might be live; instead hold the one managed reference
    // in a deferred task and dispose AFTER the native recv has provably drained
    // (> GnuTLS CommTimeout, 15s). See Quarantine() below.
    //
    // _quarantineLive counts the in-flight deferred-teardown tasks (each holds one
    // client for AbandonedReclaimSeconds then disposes it). Self-bounded at
    // arrival-rate x window — NOT a permanent leak — and surfaced for health/
    // observability (SpreadManager) so a runaway is visible.
    private int _quarantineLive;
    public int QuarantineSize => Volatile.Read(ref _quarantineLive);

    /// <summary>PRD O2 — true while a BNC cooldown is active (no new connections).</summary>
    public bool IsInCooldown => Interlocked.Read(ref _refusedUntilTicks) > DateTime.UtcNow.Ticks;

    /// <summary>PRD O2 — last observed BNC login cap (parsed from "restricted to N simultaneous logins"), or null.</summary>
    public int? ObservedLoginCap { get; private set; }

    /// <summary>
    /// Fires when the BNC's 530 reply explicitly states a simultaneous-login
    /// limit (e.g. "restricted to 4 simultaneous logins"). Subscribers can
    /// use this to tighten downstream concurrency caps (SpreadManager's
    /// per-server ServerGate). The reported integer is the BNC's hard cap,
    /// not a recommendation — callers should reserve their own headroom.
    /// </summary>
    public event Action<int>? LoginLimitObserved;

    internal void RecordConnect(double ms)
    {
        // A successful connect ends the current pressure episode — re-arm the
        // single ghost-kill and clear any BNC cooldown.
        Interlocked.Exchange(ref _ghostKilledSinceSuccess, 0);
        Interlocked.Exchange(ref _refusedUntilTicks, 0);
        lock (_connectMsSamples)
        {
            if (_connectMsSamples.Count < MaxHealthSamples) _connectMsSamples.Add(ms);
        }
    }
    internal void RecordTlsHandshake(double ms)
    {
        lock (_tlsMsSamples)
        {
            if (_tlsMsSamples.Count < MaxHealthSamples) _tlsMsSamples.Add(ms);
        }
    }
    internal void IncrementDisconnect() => Interlocked.Increment(ref _disconnects);
    internal void IncrementExhaust() => Interlocked.Increment(ref _exhaustCount);
    internal void IncrementGhostKill() => Interlocked.Increment(ref _ghostKills);
    internal void IncrementError5xx() => Interlocked.Increment(ref _errors5xx);
    internal void IncrementReinit() => Interlocked.Increment(ref _reinitCount);

    /// <summary>
    /// Called by HealthRollup only. Snapshots + zeros all health counters. Calling this
    /// outside the hourly rollup path will silently drop the in-progress window's data.
    /// </summary>
    internal void FlushHealthCounters()
    {
        lock (_connectMsSamples)
        {
            AvgConnectMs = _connectMsSamples.Count == 0 ? 0 : _connectMsSamples.Average();
            P99ConnectMs = _connectMsSamples.Count == 0 ? 0 : Percentile(_connectMsSamples, 0.99);
            _connectMsSamples.Clear();
        }
        lock (_tlsMsSamples)
        {
            AvgTlsHandshakeMs = _tlsMsSamples.Count == 0 ? 0 : _tlsMsSamples.Average();
            _tlsMsSamples.Clear();
        }
        DisconnectsSinceFlush = Interlocked.Exchange(ref _disconnects, 0);
        ExhaustCountSinceFlush = Interlocked.Exchange(ref _exhaustCount, 0);
        GhostKillsSinceFlush = Interlocked.Exchange(ref _ghostKills, 0);
        Errors5xxSinceFlush = Interlocked.Exchange(ref _errors5xx, 0);
        ReinitCountSinceFlush = Interlocked.Exchange(ref _reinitCount, 0);
    }

    private static double Percentile(List<double> samples, double p)
    {
        if (samples.Count == 0) return 0;
        var copy = new List<double>(samples); copy.Sort();
        var idx = (int)Math.Clamp(Math.Round(p * (copy.Count - 1)), 0, copy.Count - 1);
        return copy[idx];
    }

    /// <summary>
    /// Walk the exception chain looking for a "simultaneous logins" message — the BNC's
    /// explicit signal that the account has hit its login cap and ghosts should be killed.
    /// </summary>
    private static bool HasLoginLimitError(Exception? ex)
    {
        while (ex is not null)
        {
            if (ex.Message?.Contains("simultaneous logins", StringComparison.OrdinalIgnoreCase) == true)
                return true;
            ex = ex.InnerException;
        }
        return false;
    }

    // Account-wide login gate (v3.6 Phase 1). When non-null, every connection this
    // pool opens first acquires a permit, and releases it when the connection is
    // closed (quarantine/dispose) — NOT on idle return. Shared across all pools to
    // the same account so total live logins never exceed the account cap. Null in
    // the 2-arg ctor (tests + any legacy path) = no gating, exact prior behavior.
    private readonly IAccountLoginGate? _loginGate;
    // Priority pools (the FXP/spread pool) draw login permits from the gate's reserved
    // pool so they're never starved by the main pool. See ServerLoginGate.
    private readonly bool _priorityLogins;
    private int _permitsHeld;
    private static readonly TimeSpan PermitAcquireTimeout = TimeSpan.FromSeconds(30);

    public FtpConnectionPool(FtpClientFactory factory, int maxSize = 3)
        : this(factory, maxSize, null) { }

    public FtpConnectionPool(FtpClientFactory factory, int maxSize, IAccountLoginGate? loginGate, bool priorityLogins = false)
    {
        _factory = factory;
        _maxSize = maxSize;
        _loginGate = loginGate;
        _priorityLogins = priorityLogins;
        _pool = Channel.CreateBounded<AsyncFtpClient>(maxSize);
    }

    /// <summary>
    /// Create a connection under the account login gate. Acquires a permit before
    /// opening the TCP login (bounded wait); on any failure releases the permit and
    /// rethrows so the caller's _created bookkeeping backs out symmetrically. On
    /// success the permit is held until the connection is quarantined/disposed.
    /// </summary>
    private async Task<AsyncFtpClient> AcquireAndConnect(CancellationToken ct)
    {
        if (_loginGate != null)
        {
            var got = await _loginGate.TryAcquireAsync(ct, PermitAcquireTimeout, _priorityLogins);
            if (!got)
                throw new InvalidOperationException(
                    "Account login cap reached — no login permit available");
        }
        try
        {
            var client = await _factory.CreateAndConnect(ct);
            Interlocked.Increment(ref _permitsHeld);
            return client;
        }
        catch
        {
            _loginGate?.Release(_priorityLogins);
            throw;
        }
    }

    /// <summary>
    /// Release one login permit when a connection permanently leaves the live set
    /// (quarantine or dispose). Clamped — never releases more than held.
    /// </summary>
    private void ReleasePermit()
    {
        if (_loginGate == null) return;
        if (Interlocked.Decrement(ref _permitsHeld) < 0)
        {
            Interlocked.Increment(ref _permitsHeld); // undo
            Log.Warning("Pool: login permit under-release detected (clamped)");
            return;
        }
        _loginGate.Release(_priorityLogins);
    }

    // Health options (v2.6.1). Spread pools have no ConnectionMonitor keepalive
    // like the main pool, so idle connections die silently and the next FXP
    // borrow fails "No connection to the server exists". Two mitigations:
    //  - _validateOnBorrow: NOOP-probe a pooled connection before handing it out.
    //  - keepalive timer: periodic NOOP on one idle connection per tick.
    private volatile bool _validateOnBorrow;
    private int _keepaliveSeconds;
    private Timer? _keepaliveTimer;

    /// <summary>
    /// Enable borrow-time liveness validation and/or a keepalive timer. Called
    /// by SpreadManager after pool creation; the main filesystem pool leaves
    /// these off (it has ConnectionMonitor). Idempotent — safe to call on reinit.
    /// </summary>
    public void ConfigureHealth(bool validateOnBorrow, int keepaliveSeconds)
    {
        _validateOnBorrow = validateOnBorrow;
        _keepaliveSeconds = keepaliveSeconds;

        _keepaliveTimer?.Dispose();
        _keepaliveTimer = null;
        if (keepaliveSeconds > 0 && !_disposed)
        {
            var period = TimeSpan.FromSeconds(keepaliveSeconds);
            _keepaliveTimer = new Timer(_ => KeepaliveTick(), null, period, period);
        }
    }

    /// <summary>Outcome of a liveness probe — distinguishes a read that RETURNED
    /// (safe to neutralize now) from one we ABANDONED at the managed timeout while the
    /// native GnuTLS recv keeps running (must NOT touch the session/socket yet).</summary>
    private enum LiveProbe { Healthy, DeadCompleted, TimedOut }

    // Managed deadline for the NOOP liveness probe. Kept short so a dead idle/borrow
    // connection is detected fast.
    private static readonly TimeSpan ProbeDeadline = TimeSpan.FromSeconds(3);

    // How long to wait before tearing down a connection whose probe TIMED OUT. The
    // FluentFTP.GnuTLS native recv runs until GnuConfig.CommTimeout (15000ms, the
    // package minimum) regardless of our managed CancellationToken — so the socket
    // close + session mutation in NeutralizeGnuTls MUST be deferred past that, or it
    // races the in-flight native read and SEGVs the process (5 watchdog restarts
    // observed 2026-06-06, every one ~3s after a quarantine). 15s CommTimeout + grace.
    private const int AbandonedReclaimSeconds = 20;

    /// <summary>
    /// NOOP-probe a connection with a short managed deadline. Reports whether the
    /// probe came back healthy, came back with a definite failure (read returned —
    /// safe to tear down immediately), or hit our managed deadline (read ABANDONED —
    /// the native recv is still in flight; the caller must defer any teardown).
    /// Never throws.
    /// </summary>
    private static async Task<LiveProbe> ProbeLive(AsyncFtpClient client, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ProbeDeadline);
        try
        {
            var reply = await client.Execute("NOOP", cts.Token);
            return reply.Success ? LiveProbe.Healthy : LiveProbe.DeadCompleted;
        }
        catch (OperationCanceledException)
        {
            // We stopped awaiting because a token fired — the underlying native
            // GnuTLS recv was NOT cancelled and keeps running until CommTimeout.
            return LiveProbe.TimedOut;
        }
        catch
        {
            // The read completed with an error (connection reset, protocol error,
            // etc.). No native read is in flight — safe to neutralize immediately.
            return LiveProbe.DeadCompleted;
        }
    }

    /// <summary>Result of validating a pooled connection for hand-out.</summary>
    private enum BorrowCheck { Usable, Dead }

    /// <summary>
    /// Validate a pooled connection before handing it to a borrower. When the
    /// connection is dead this routes it to the correct teardown path — immediate
    /// neutralize for a completed-failure probe, deferred reclaim for an abandoned
    /// (timed-out) probe — and returns <see cref="BorrowCheck.Dead"/>. Callers should
    /// still <see cref="IncrementDisconnect"/> for stats on the Dead result.
    /// </summary>
    private async Task<BorrowCheck> CheckPooledForBorrow(AsyncFtpClient client, CancellationToken ct)
    {
        if (client.IsConnected && IsGnuTlsHealthy(client))
        {
            if (!_validateOnBorrow) return BorrowCheck.Usable;
            var probe = await ProbeLive(client, ct);
            if (probe == LiveProbe.Healthy) return BorrowCheck.Usable;
            if (probe == LiveProbe.TimedOut)
            {
                QuarantineDeferred(client, "borrow-probe-timeout"); // native read may be live — defer teardown
                return BorrowCheck.Dead;
            }
            // DeadCompleted — fall through to immediate neutralize.
        }
        // Not connected / unhealthy session / completed-failure probe: no native read
        // is in flight, so neutralizing now is safe.
        QuarantineDecCreated(client);
        return BorrowCheck.Dead;
    }

    /// <summary>
    /// Keepalive: warm EVERY currently-idle connection this tick. Replaces
    /// FluentFTP's background NoopDaemon (disabled in FtpClientFactory) which
    /// kept all pool connections alive but raced its reads against disposal.
    /// This is owner-exclusive: each connection is read OUT of the channel
    /// before its NOOP, so the probe can never run concurrently with a borrow,
    /// quarantine, or dispose of the same connection — the GnuTLS use-after-free
    /// race that crashed the process is structurally impossible here.
    ///
    /// We snapshot the current idle count and read at most that many, NOOP each,
    /// and write survivors back to the tail. Bounding by the snapshot prevents
    /// re-probing a connection we just returned (FIFO channel). The window where
    /// a connection is out of the channel is one NOOP round-trip (sub-second);
    /// a borrower arriving mid-probe finds the remaining idle connections or
    /// waits on ReadAsync (the _created count is unchanged, cap respected).
    /// Dead connections are quarantined.
    /// </summary>
    private async void KeepaliveTick()
    {
        if (_disposed || _keepaliveSeconds <= 0) return;

        // Snapshot the live connection count so we make at most one pass over the
        // currently-idle set, even though we write survivors straight back.
        var budget = Interlocked.CompareExchange(ref _created, 0, 0);
        for (var i = 0; i < budget; i++)
        {
            if (_disposed) return;
            if (!_pool.Reader.TryRead(out var client)) return; // no idle connections left this tick
            try
            {
                var probe = await ProbeLive(client, CancellationToken.None);
                if (probe == LiveProbe.Healthy)
                {
                    if (!_pool.Writer.TryWrite(client))
                        QuarantineDecCreated(client); // healthy but no room — read done, safe
                }
                else if (probe == LiveProbe.TimedOut)
                {
                    // Native recv still in flight — defer teardown (see Quarantine).
                    QuarantineDeferred(client, "keepalive-probe-timeout");
                }
                else
                {
                    QuarantineDecCreated(client);
                    Log.Debug("Pool keepalive: dropped dead idle connection (created={Created})", _created);
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Pool keepalive tick failed");
            }
        }
    }

    public int ActiveCount => _active;
    public int TotalCreated => _created;
    public int MaxSize => _maxSize;

    /// <summary>
    /// Lower the pool's maximum connection count. Shrink-only — if
    /// <paramref name="newMax"/> is greater than or equal to the current
    /// max, this is a no-op. Existing connections beyond the new max
    /// stay alive until they're naturally rotated out by failure or
    /// disposal; future Borrows above the new max will queue rather than
    /// create+fail. Used by SpreadManager when a BNC's observed login
    /// cap is lower than the configured pool size.
    /// </summary>
    public void ShrinkMaxSize(int newMax)
    {
        if (newMax < 1) newMax = 1;
        var old = Interlocked.CompareExchange(ref _maxSize, 0, 0);
        if (newMax >= old) return;
        Interlocked.Exchange(ref _maxSize, newMax);
        Log.Information("Pool: max size shrunk {Old} -> {New} (BNC login cap discovered)", old, newMax);
    }
    public bool IsConnected { get; private set; }
    /// <summary>True when all connections have been discarded and the pool can't serve requests.</summary>
    public bool IsExhausted => IsConnected && _created <= 0 && _active <= 0;
    public bool UseCpsv { get; private set; }
    public string ControlHost { get; private set; } = "";

    public async Task Initialize(CancellationToken ct = default)
    {
        await _initLock.WaitAsync(ct);
        try
        {
            if (_created > 0) return;

            // Create initial connection to verify connectivity
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var client = await AcquireAndConnect(ct);
            RecordConnect(sw.ElapsedMilliseconds);

            // Detect CPSV support (glftpd BNC)
            ControlHost = _factory.Host;
            if (client.Capabilities.Contains(FtpCapability.CPSV))
            {
                UseCpsv = true;
                Log.Information("Server supports CPSV — using BNC-compatible data connections");
            }

            await _pool.Writer.WriteAsync(client, ct);
            _created = 1;
            IsConnected = true;
            Log.Information("Connection pool initialized with 1 connection (max {Max})", _maxSize);
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Re-initialize a pool that has been fully exhausted (all connections poisoned/discarded).
    /// Creates a fresh connection and puts it back in the pool.
    /// </summary>
    public async Task Reinitialize(CancellationToken ct = default)
    {
        if (_disposed) return;
        if (_created > 0 || _active > 0) return; // Pool still has connections

        await _initLock.WaitAsync(ct);
        try
        {
            if (_created > 0 || _active > 0) return; // Double-check under lock

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var client = await AcquireAndConnect(ct);
            RecordConnect(sw.ElapsedMilliseconds);
            ControlHost = _factory.Host;
            if (client.Capabilities.Contains(FtpCapability.CPSV))
                UseCpsv = true;

            await _pool.Writer.WriteAsync(client, ct);
            _created = 1;
            IsConnected = true;
            IncrementReinit();
            Log.Information("Connection pool reinitialized with 1 connection (max {Max})", _maxSize);
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<PooledConnection> Borrow(CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(FtpConnectionPool));

        // Try to get from pool first
        if (_pool.Reader.TryRead(out var client))
        {
            // Stale, corrupt, or NOOP-dead connections are routed to quarantine inside
            // CheckPooledForBorrow (immediate neutralize when safe, deferred reclaim
            // when the probe was abandoned) — exactly the kind we most fear running
            // gnutls_deinit() on.
            if (await CheckPooledForBorrow(client, ct) == BorrowCheck.Usable)
            {
                Interlocked.Increment(ref _active);
                return new PooledConnection(client, this);
            }
            IncrementDisconnect();
        }

        // PRD H2: if the server is in a BNC cooldown, don't attempt a new
        // connection — it'll just be refused and deepen the cooldown. Fall
        // through to wait on the channel (an in-flight transfer may return a
        // usable connection) or fail fast if the pool is truly empty.
        var refusedUntil = Interlocked.Read(ref _refusedUntilTicks);
        if (refusedUntil > 0 && DateTime.UtcNow.Ticks < refusedUntil)
        {
            if (_created <= 0)
            {
                IncrementExhaust();
                throw new InvalidOperationException(
                    "Server in BNC cooldown — not attempting new connection");
            }
            client = await _pool.Reader.ReadAsync(ct);
            if (await CheckPooledForBorrow(client, ct) == BorrowCheck.Usable)
            {
                Interlocked.Increment(ref _active);
                return new PooledConnection(client, this);
            }
            IncrementDisconnect();
            throw new InvalidOperationException(
                "Server in BNC cooldown — pooled connection was dead");
        }

        // Pool empty — create new if under limit
        if (Interlocked.Increment(ref _created) <= _maxSize)
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                client = await AcquireAndConnect(ct);
                RecordConnect(sw.ElapsedMilliseconds);
                Interlocked.Increment(ref _active);
                return new PooledConnection(client, this);
            }
            catch (Exception ex)
            {
                // BNC cooldown detection: TCP-level refusal, or a 530 that
                // persists after we already used our one ghost-kill this episode.
                var refused = ex.Message?.Contains("actively refused", StringComparison.OrdinalIgnoreCase) == true
                    || ex.Message?.Contains("target machine actively refused", StringComparison.OrdinalIgnoreCase) == true
                    || (HasLoginLimitError(ex) && Interlocked.CompareExchange(ref _ghostKilledSinceSuccess, 1, 1) == 1);
                // A login-GATE timeout ("Account login cap reached — no login permit
                // available", thrown by AcquireAndConnect) is the dominant storm failure
                // and had NO cooldown before v3.9.0 — so Borrow re-attempted a doomed 30s
                // gate wait every cycle, generating the relentless quarantine churn. Arm a
                // SHORT backoff so subsequent Borrows fall through to wait-on-channel /
                // fail-fast instead of re-spinning the gate. (Mutually exclusive with the
                // 90s refusal cooldown above; refusal wins.)
                var gateCapped = !refused && ex is InvalidOperationException
                    && ex.Message?.Contains("login cap reached", StringComparison.OrdinalIgnoreCase) == true;
                if (refused)
                {
                    Interlocked.Exchange(ref _refusedUntilTicks, DateTime.UtcNow.Add(CooldownWindow).Ticks);
                    Log.Information("Pool: server entering {Sec}s BNC cooldown (refusal detected) — pausing new connections",
                        (int)CooldownWindow.TotalSeconds);
                }
                else if (gateCapped)
                {
                    Interlocked.Exchange(ref _refusedUntilTicks, DateTime.UtcNow.Add(LoginGateCooldown).Ticks);
                    Log.Information("Pool: server entering {Sec}s login-cap backoff (no permit available) — pausing new connections",
                        (int)LoginGateCooldown.TotalSeconds);
                }
                Interlocked.Decrement(ref _created);
                // PRD O4 — demote to Information. The underlying cause (login limit,
                // BNC cooldown, ghost-kill triggered) is already logged once per
                // episode by dedicated paths; repeating it as WRN for every retry
                // floods the log. The failure-taxonomy metrics surface the pattern.
                if (_created >= _maxSize)
                    Log.Debug(ex, "Pool: new connection failed (at capacity, created={Created}, max={Max})", _created, _maxSize);
                else
                    Log.Information(ex, "Pool: new connection failed (created={Created}, max={Max})", _created, _maxSize);

                // BNC explicitly said we're out of logins — kill ghosts, but throttle to
                // once per 5s. Without the throttle, multiple concurrent Borrow() callers
                // each fired KillGhosts() in the same second; two stacked TLS handshakes
                // against a hostile BNC crashed GnuTLS natively (no managed handler),
                // observed as silent process termination on 2026-05-13 and 2026-05-14.
                // CompareExchange ensures only one thread wins the throttle window.
                if (HasLoginLimitError(ex))
                {
                    // Option B (v2.4): parse the observed BNC cap and broadcast it
                    // so SpreadManager can tighten its per-server gate. Pattern
                    // "Sorry, your account is restricted to N simultaneous logins."
                    try
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(
                            ex.Message ?? "",
                            @"restricted to (\d+) simultaneous logins",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (m.Success && int.TryParse(m.Groups[1].Value, out var observedLimit)
                            && observedLimit >= 1 && observedLimit <= 64)
                        {
                            ObservedLoginCap = observedLimit; // PRD O2 surfacing
                            LoginLimitObserved?.Invoke(observedLimit);
                            // The BNC just told us its real cap. Tighten the shared
                            // account gate (shrink-only) so EVERY pool to this account
                            // immediately stops over-subscribing. Reserve 1 login of
                            // headroom for transient ungated work (ghost-kill).
                            _loginGate?.TightenTo(Math.Max(1, observedLimit - 1));
                        }
                    }
                    catch (Exception parseEx)
                    {
                        Log.Debug(parseEx, "Pool: BNC limit parse failed (non-fatal)");
                    }

                    // Ghost-kill ONCE per pressure episode. CompareExchange(1,0)
                    // succeeds only for the first caller since the last successful
                    // connect; everyone after that falls through to plain
                    // fail-and-backoff (the slot will free when an active transfer
                    // finishes). RecordConnect re-arms it on the next success.
                    if (Interlocked.CompareExchange(ref _ghostKilledSinceSuccess, 1, 0) == 0)
                    {
                        try
                        {
                            await _factory.KillGhosts(ct);
                            IncrementGhostKill();
                            Log.Information("Pool: ghost kill (once per episode) triggered by login-limit from BNC");
                        }
                        catch (Exception ghostEx)
                        {
                            Log.Debug(ghostEx, "Pool: forced ghost kill (login-limit) failed");
                        }
                    }
                    else
                    {
                        Log.Debug("Pool: login-limit persists after ghost kill — NOT re-killing (avoids BNC reconnect-storm cooldown); waiting for a slot to free");
                    }
                }
                else
                {
                // Generic (non-login-limit) connection failure. Try a single
                // ghost-kill per episode, then retry once. Same once-per-episode
                // rule prevents !entity reconnect storms.
                if (Interlocked.CompareExchange(ref _ghostKilledSinceSuccess, 1, 0) == 0)
                {
                    try
                    {
                        await _factory.KillGhosts(ct);
                        IncrementGhostKill();
                        // Retry connection after ghost kill
                        if (Interlocked.Increment(ref _created) <= _maxSize)
                        {
                            try
                            {
                                var sw2 = System.Diagnostics.Stopwatch.StartNew();
                                client = await AcquireAndConnect(ct);
                                RecordConnect(sw2.ElapsedMilliseconds);
                                Interlocked.Increment(ref _active);
                                return new PooledConnection(client, this);
                            }
                            catch
                            {
                                Interlocked.Decrement(ref _created);
                            }
                        }
                        else
                        {
                            Interlocked.Decrement(ref _created);
                        }
                    }
                    catch (Exception ghostEx)
                    {
                        Log.Debug(ghostEx, "Pool: ghost kill failed");
                    }
                }
                }
            }
        }
        else
        {
            Interlocked.Decrement(ref _created);
        }

        // If no connections exist at all (all discarded), don't wait — nothing will be returned
        if (_created <= 0)
        {
            IncrementExhaust();
            throw new InvalidOperationException("Pool exhausted: all connections discarded and new connections failed");
        }

        // At capacity — wait for one to be returned
        client = await _pool.Reader.ReadAsync(ct);
        if (await CheckPooledForBorrow(client, ct) == BorrowCheck.Usable)
        {
            Interlocked.Increment(ref _active);
            return new PooledConnection(client, this);
        }

        // Stale or NOOP-dead — already quarantined inside CheckPooledForBorrow
        // (decrements _created). Count the disconnect and replace.
        IncrementDisconnect();
        if (Interlocked.Increment(ref _created) <= _maxSize)
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                client = await AcquireAndConnect(ct);
                RecordConnect(sw.ElapsedMilliseconds);
                Interlocked.Increment(ref _active);
                return new PooledConnection(client, this);
            }
            catch
            {
                Interlocked.Decrement(ref _created);
                throw;
            }
        }
        Interlocked.Decrement(ref _created);
        IncrementExhaust();
        throw new InvalidOperationException("Pool exhausted after stale connection replacement");
    }

    internal void Return(AsyncFtpClient client)
    {
        Interlocked.Decrement(ref _active);
        if (_disposed || !client.IsConnected)
        {
            // !IsConnected on the return path means the connection died during
            // the borrowed operation — same SEGV risk as a Poisoned discard
            // (the GnuTLS session may be in a corrupted state, and a native recv
            // may still be draining). Defer teardown past CommTimeout instead of
            // touching the socket now. _active already decremented above.
            IncrementDisconnect();
            QuarantineDeferred(client, "dead-return");
            return;
        }

        if (!_pool.Writer.TryWrite(client))
        {
            // Channel write failed — pool is being torn down or oversubscribed.
            // Same quarantine treatment.
            QuarantineDecCreated(client);
        }
    }

    /// <summary>
    /// Discard a poisoned connection (e.g. after a mid-transfer "forcibly closed"
    /// SSL error or an FXP borrow timeout). Its GnuTLS session is corrupt AND its
    /// native control-channel recv may still be draining (the managed await unwound
    /// but the native recv runs to CommTimeout). Touching the socket/session now —
    /// socket.Close, gnutls_deinit — races that live read and SEGVs the process
    /// bypassing every managed handler. Route through the DEFERRED teardown so the
    /// socket close + dispose only fire once the recv has provably returned. This was
    /// the dominant crash funnel pre-v3.9.0 (poisoned Discard -> inline socket.Close).
    /// </summary>
    internal void Discard(AsyncFtpClient client)
    {
        Interlocked.Decrement(ref _active);
        IncrementDisconnect();
        QuarantineDeferred(client, "poisoned-discard");
    }

    /// <summary>
    /// Quarantine a connection whose native recv has PROVABLY returned (NOOP probe
    /// came back, the client is not connected, or a healthy connection had no room
    /// in the channel). Closing the socket now is safe and frees the BNC login
    /// promptly, so we neutralize inline. The session's gnutls_deinit is still
    /// deferred (see <see cref="Quarantine"/>). Caller decrements _active first.
    /// </summary>
    private void QuarantineDecCreated(AsyncFtpClient client)
        => Quarantine(client, "recv-returned", recvQuiescent: true);

    /// <summary>
    /// Quarantine a connection whose native GnuTLS recv MIGHT still be in flight —
    /// a poisoned mid-transfer Discard, an abandoned liveness probe (managed cancel
    /// fired but the native recv runs to CommTimeout), or a borrow that died unproven.
    /// We must NOT touch the socket/session now: doing so races the live recv and
    /// SEGVs the process inside GnuTlsInternalStream.Read. The neutralize is deferred
    /// past CommTimeout (see <see cref="Quarantine"/>). Caller decrements _active first.
    /// </summary>
    private void QuarantineDeferred(AsyncFtpClient client, string reason)
        => Quarantine(client, reason, recvQuiescent: false);

    /// <summary>
    /// Single teardown funnel. Drops the pool's _created accounting, releases the login
    /// permit immediately, and disposes the client on a deferred task once its native
    /// recv has provably drained (> GnuTLS CommTimeout).
    ///
    /// recvQuiescent=true  : the recv has returned — close the socket NOW (frees the BNC
    ///                       login promptly), defer only the gnutls_deinit.
    /// recvQuiescent=false : the recv MIGHT be live — defer the socket close + neutralize
    ///                       too, or it races the read and SEGVs the process.
    ///
    /// The login permit is released SYNCHRONOUSLY (pure accounting; it does not touch the
    /// socket). Deferring it would pin the scarce FXP login permits for the whole 20s
    /// window and, under storm churn against a 1-2 permit account, LIVELOCK the gate
    /// (AcquireAndConnect 30s-timeouts -> re-quarantine -> re-defer). The BNC login is
    /// physically freed when the (possibly deferred) NeutralizeGnuTls closes the socket;
    /// the brief over-subscription skew is tolerable (at worst a handled 530) — a permit
    /// deadlock is not. There is no count-based eviction: the live set self-bounds at
    /// (arrival-rate x AbandonedReclaimSeconds); a fixed cap with force-dispose was the
    /// v2.5.1 ring-eviction SEGV (fixed v3.9.0). Caller decrements _active first.
    /// </summary>
    private void Quarantine(AsyncFtpClient client, string reason, bool recvQuiescent)
    {
        if (recvQuiescent)
        {
            try { NeutralizeGnuTls(client); }
            catch (Exception ex) { Log.Debug(ex, "Pool: NeutralizeGnuTls during quarantine failed"); }
        }

        // Clamp at 0: Reinitialize() resets _created=1 while pre-reinit connections
        // are still borrowed — their eventual discard lands here and drove the count
        // to -1 (77 quarantine lines on 2026-06-30/07-01), skewing the fail-fast and
        // capacity checks by one until the next successful create.
        if (Interlocked.Decrement(ref _created) < 0) Interlocked.Increment(ref _created);
        ReleasePermit();

        var live = Interlocked.Increment(ref _quarantineLive);
        Log.Information("Pool: quarantined connection ({Reason}, deferred teardown {Delay}s, live={Live}, created={Created})",
            reason, AbandonedReclaimSeconds, live, _created);
        if (live > 200)
            Log.Warning("Pool: deferred-teardown backlog high (live={Live}) — quarantine arrival outrunning the {Delay}s drain window",
                live, AbandonedReclaimSeconds);

        _ = Task.Run(async () =>
        {
            // Hold `client` reachable across the delay so the GC finalizer can't run
            // gnutls_deinit on a not-yet-drained session. After AbandonedReclaimSeconds
            // (> CommTimeout 15s) the native recv has provably returned — socket close
            // + session free are now safe.
            try { await Task.Delay(TimeSpan.FromSeconds(AbandonedReclaimSeconds)).ConfigureAwait(false); }
            catch { }
            if (!recvQuiescent)
            {
                try { NeutralizeGnuTls(client); }
                catch (Exception ex) { Log.Debug(ex, "Pool: deferred neutralize ({Reason}) threw", reason); }
            }
            try { client.Dispose(); }
            catch (Exception ex) { Log.Debug(ex, "Pool: deferred dispose ({Reason}) threw", reason); }
            finally { Interlocked.Decrement(ref _quarantineLive); }
        });
    }

    /// <summary>
    /// Check if the GnuTLS native session is still healthy before using a connection.
    /// A corrupt session will crash the process on the next Read/Write with an
    /// AccessViolationException that can't be caught in .NET. This pre-flight check
    /// inspects the native session state via reflection to prevent that.
    /// </summary>
    private static bool IsGnuTlsHealthy(AsyncFtpClient client)
    {
        try
        {
            var stream = ((IInternalFtpClient)client).GetBaseStream();
            if (stream == null) return true; // No stream = not TLS, skip check

            var customStreamField = stream.GetType().GetField("m_customStream",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var customStream = customStreamField?.GetValue(stream);
            if (customStream == null) return true; // Not using GnuTLS

            // BaseStream is a FIELD on GnuTlsStream (not a property — the pre-v3.6
            // GetProperty lookup silently returned null, making this whole health
            // check a no-op). GnuTlsReflectionGuard verifies this member kind at
            // startup so a package rename fails loud.
            var baseStreamField = customStream.GetType().GetField("BaseStream",
                BindingFlags.NonPublic | BindingFlags.Instance)
                ?? customStream.GetType().GetField("BaseStream",
                    BindingFlags.Public | BindingFlags.Instance);
            var gnuTlsInternal = baseStreamField?.GetValue(customStream);
            if (gnuTlsInternal == null) return true;

            // Check IsSessionUsable flag
            var usableProp = gnuTlsInternal.GetType().GetProperty("IsSessionUsable");
            if (usableProp != null)
            {
                var usable = (bool?)usableProp.GetValue(gnuTlsInternal);
                if (usable == false)
                {
                    Log.Warning("Pool: GnuTLS session marked unusable — discarding connection");
                    return false;
                }
            }

            // Check the managed session object 'sess' (ClientSession) is present —
            // a null sess means the GnuTLS session is gone/torn down and the next
            // Read would fault. (Pre-v3.6 looked for a non-existent IntPtr
            // '_session'; corrected to the real 'sess' field.)
            var sessionField = gnuTlsInternal.GetType().GetField("sess",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (sessionField != null)
            {
                var sessionValue = sessionField.GetValue(gnuTlsInternal);
                if (sessionValue == null)
                {
                    Log.Warning("Pool: GnuTLS session object is null — discarding connection");
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Pool: GnuTLS health check failed — discarding connection");
            return false;
        }
    }

    /// <summary>
    /// Neutralize GnuTLS before disposal to prevent native crashes.
    /// GnuTLS calls gnutls_bye() during stream Dispose, which can crash the process
    /// with a native access violation if the underlying socket is dead or the TLS
    /// session is corrupt. This method:
    /// 1. Sets IsSessionUsable=false on GnuTlsInternalStream to skip gnutls_bye()
    /// 2. Closes the raw socket so any remaining native I/O fails cleanly
    /// </summary>
    private static void NeutralizeGnuTls(AsyncFtpClient client)
    {
        try
        {
            // PRD H1: stop the built-in NOOP daemon so a quarantined (never-disposed)
            // client doesn't keep a background NOOP task alive forever. The daemon
            // loop checks Config.Noop each tick and exits when false. Without this,
            // every quarantined connection retained a live daemon and thread count
            // climbed with quarantine churn.
            try { client.Config.Noop = false; } catch { }

            // Get the FtpSocketStream via the public IInternalFtpClient interface
            var stream = ((IInternalFtpClient)client).GetBaseStream();
            if (stream == null) return;

            // Get m_customStream (the GnuTLS wrapper) via reflection
            var customStreamField = stream.GetType().GetField("m_customStream",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var customStream = customStreamField?.GetValue(stream);
            if (customStream != null)
            {
                // The custom stream wraps GnuTlsInternalStream as its BaseStream
                // FIELD (not a property — the pre-v3.6 GetProperty lookup returned
                // null, so IsSessionUsable was NEVER set and gnutls_bye was not
                // actually being skipped here; corrected to GetField).
                var baseStreamField = customStream.GetType().GetField("BaseStream",
                    BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? customStream.GetType().GetField("BaseStream",
                        BindingFlags.Public | BindingFlags.Instance);
                var gnuTlsInternal = baseStreamField?.GetValue(customStream);
                if (gnuTlsInternal != null)
                {
                    var usableProp = gnuTlsInternal.GetType().GetProperty("IsSessionUsable");
                    if (usableProp?.CanWrite == true)
                    {
                        usableProp.SetValue(gnuTlsInternal, false);
                    }
                    else
                    {
                        // Fallback: use backing field if setter is private
                        var backingField = gnuTlsInternal.GetType().GetField("<IsSessionUsable>k__BackingField",
                            BindingFlags.NonPublic | BindingFlags.Instance);
                        backingField?.SetValue(gnuTlsInternal, false);
                    }
                }

                // Belt-and-braces (PRD v3.5.1): NULL the m_customStream pointer on
                // the FtpSocketStream so that if a later Dispose() chain runs
                // (e.g. quarantine FIFO eviction), it has no GnuTLS wrapper to
                // tear down. gnutls_deinit() on a corrupted session is what
                // SEGVs the process — observed 2026-05-25 10:17 after an
                // evicted-oldest force-dispose. Native session memory leaks
                // (small + bounded by eviction rate) instead of crashing.
                try { customStreamField?.SetValue(stream, null); } catch { }
            }

            // Also close the raw socket to prevent any stray native I/O
            var socketField = stream.GetType().GetField("m_socket",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var socket = socketField?.GetValue(stream) as Socket;
            if (socket != null)
            {
                try { socket.Close(0); } catch { }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "NeutralizeGnuTls: failed (non-fatal)");
        }
    }

    /// <summary>
    /// Disposes an FTP client safely by first neutralizing GnuTLS to prevent
    /// native crashes, then disposing in a fire-and-forget task with timeout.
    /// </summary>
    private static void DisconnectAndDispose(AsyncFtpClient client)
    {
        NeutralizeGnuTls(client);
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await client.Disconnect(cts.Token).ConfigureAwait(false);
            }
            catch { }
            try { client.Dispose(); }
            catch { }
        });
    }

    /// <summary>
    /// Safe disposal for DisposeAsync — neutralizes GnuTLS then disconnects.
    /// </summary>
    private static async Task SafeDisconnectAndDispose(AsyncFtpClient client)
    {
        NeutralizeGnuTls(client);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await client.Disconnect(cts.Token).ConfigureAwait(false);
        }
        catch { }
        try { client.Dispose(); }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _keepaliveTimer?.Dispose();
        _keepaliveTimer = null;
        _pool.Writer.Complete();
        IsConnected = false;

        while (_pool.Reader.TryRead(out var client))
        {
            await SafeDisconnectAndDispose(client);
            // Each idle pooled connection held a login permit — release it as we
            // close it. Connections currently BORROWED release their own permit
            // when they Return (routes to QuarantineDecCreated since _disposed=true),
            // so we only account for the idle ones drained here.
            ReleasePermit();
        }

        _initLock.Dispose();
        GC.SuppressFinalize(this);
    }
}

public class PooledConnection : IAsyncDisposable
{
    private readonly FtpConnectionPool _pool;
    private AsyncFtpClient? _client;

    internal PooledConnection(AsyncFtpClient client, FtpConnectionPool pool)
    {
        _client = client;
        _pool = pool;
    }

    public AsyncFtpClient Client => _client ?? throw new ObjectDisposedException(nameof(PooledConnection));

    /// <summary>
    /// Mark this connection as poisoned so it will be discarded instead of returned
    /// to the pool. Call this after a cancellation or error that may have left the
    /// GnuTLS stream in a corrupt state.
    /// </summary>
    public bool Poisoned { get; set; }

    public ValueTask DisposeAsync()
    {
        var client = Interlocked.Exchange(ref _client, null);
        if (client != null)
        {
            if (Poisoned)
                _pool.Discard(client);
            else
                _pool.Return(client);
        }
        return ValueTask.CompletedTask;
    }
}
