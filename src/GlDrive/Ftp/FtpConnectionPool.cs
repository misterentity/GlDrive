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
    // closed by the remote host"), the affected GnuTLS session is in a
    // corrupted native state. Calling client.Dispose() ends up invoking
    // gnutls_deinit() which SEGVs the process — observed 6 times on
    // 2026-05-16 with AppDomain.UnhandledException firing zero times because
    // native crashes bypass every managed handler. Solution: never Dispose
    // poisoned connections; hold a permanent managed reference so the GC
    // finalizer thread never runs SafeHandle.ReleaseHandle on the corrupt
    // native session. Native memory (~few KB per quarantined session) leaks
    // intentionally — bounded by the number of failures per process lifetime
    // (dozens, not thousands), well worth the alternative of a dead process.
    private readonly List<AsyncFtpClient> _quarantine = new();
    private readonly Lock _quarantineLock = new();
    public int QuarantineSize { get { lock (_quarantineLock) return _quarantine.Count; } }

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

    public FtpConnectionPool(FtpClientFactory factory, int maxSize = 3)
    {
        _factory = factory;
        _maxSize = maxSize;
        _pool = Channel.CreateBounded<AsyncFtpClient>(maxSize);
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

    /// <summary>
    /// NOOP-probe a connection with a short timeout. Returns false on any error
    /// — caller should quarantine and replace. Never throws.
    /// </summary>
    private static async Task<bool> IsLive(AsyncFtpClient client, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            var reply = await client.Execute("NOOP", cts.Token);
            return reply.Success;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Keepalive: probe ONE idle connection per tick. Pulling a single
    /// connection keeps the window tiny — a borrower that arrives mid-probe
    /// either finds the remaining connections or waits on ReadAsync (the
    /// _created count is unchanged, so the cap is respected) until this
    /// returns the probed connection. Dead connections are quarantined.
    /// </summary>
    private async void KeepaliveTick()
    {
        if (_disposed || _keepaliveSeconds <= 0) return;
        if (!_pool.Reader.TryRead(out var client)) return;
        try
        {
            if (await IsLive(client, CancellationToken.None))
            {
                if (!_pool.Writer.TryWrite(client))
                    QuarantineDecCreated(client);
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
            var client = await _factory.CreateAndConnect(ct);
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
            var client = await _factory.CreateAndConnect(ct);
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
            if (client.IsConnected && IsGnuTlsHealthy(client)
                && (!_validateOnBorrow || await IsLive(client, ct)))
            {
                Interlocked.Increment(ref _active);
                return new PooledConnection(client, this);
            }

            // Stale, corrupt, or NOOP-dead connection — quarantine instead of
            // dispose. A connection that failed IsGnuTlsHealthy or the NOOP
            // probe is exactly the kind we most fear running gnutls_deinit() on.
            IncrementDisconnect();
            QuarantineDecCreated(client);
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
            if (client.IsConnected && IsGnuTlsHealthy(client)
                && (!_validateOnBorrow || await IsLive(client, ct)))
            {
                Interlocked.Increment(ref _active);
                return new PooledConnection(client, this);
            }
            IncrementDisconnect();
            QuarantineDecCreated(client);
            throw new InvalidOperationException(
                "Server in BNC cooldown — pooled connection was dead");
        }

        // Pool empty — create new if under limit
        if (Interlocked.Increment(ref _created) <= _maxSize)
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                client = await _factory.CreateAndConnect(ct);
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
                if (refused)
                {
                    Interlocked.Exchange(ref _refusedUntilTicks, DateTime.UtcNow.Add(CooldownWindow).Ticks);
                    Log.Information("Pool: server entering {Sec}s BNC cooldown (refusal detected) — pausing new connections",
                        (int)CooldownWindow.TotalSeconds);
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
                                client = await _factory.CreateAndConnect(ct);
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
        if (client.IsConnected && IsGnuTlsHealthy(client)
            && (!_validateOnBorrow || await IsLive(client, ct)))
        {
            Interlocked.Increment(ref _active);
            return new PooledConnection(client, this);
        }

        // Stale or NOOP-dead — quarantine (decrements _created internally).
        IncrementDisconnect();
        QuarantineDecCreated(client);
        if (Interlocked.Increment(ref _created) <= _maxSize)
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                client = await _factory.CreateAndConnect(ct);
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
            // (the GnuTLS session may be in a corrupted state we just haven't
            // proven yet). Quarantine it instead of Disposing. _active already
            // decremented above, so call QuarantineDecCreated which only
            // decrements _created.
            IncrementDisconnect();
            QuarantineDecCreated(client);
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
    /// Discard a poisoned connection — DO NOT dispose. Calling Dispose on a
    /// connection whose GnuTLS session is corrupted (e.g. after a mid-transfer
    /// "forcibly closed" SSL error) ends up running gnutls_deinit() on bad
    /// native state, which SEGVs the process bypassing every managed handler.
    /// Instead we close the socket so the network resource is freed, then
    /// keep a permanent reference in <see cref="_quarantine"/> so the GC
    /// finalizer never runs and the SafeHandle.ReleaseHandle native call
    /// never fires. The managed AsyncFtpClient is ~a few KB; the native
    /// session leaks ~similar; bounded by error rate. Better than dying.
    /// </summary>
    internal void Discard(AsyncFtpClient client)
    {
        Interlocked.Decrement(ref _active);
        IncrementDisconnect();
        QuarantineDecCreated(client);
    }

    // Quarantine cap. v2.5.0 introduced unbounded quarantine to dodge the
    // gnutls_deinit() SEGV — that fixed crashes but observed in production
    // 6.7h after upgrade: 575 threads, 2.1GB working set because each
    // quarantined AsyncFtpClient still runs its FluentFTP NoopDaemon +
    // keepalive timer against the closed socket. v2.5.1 caps the live
    // quarantine at MaxQuarantineSize and force-disposes the oldest entry
    // when full — by the time an entry hits 50-deep in the FIFO, several
    // minutes have passed and the native corruption window is much smaller
    // (still nonzero, but a calculated trade vs runaway resource leak).
    // Lowered 50 -> 30 (PRD H1): combined with stopping the NOOP daemon on
    // quarantine, keeps thread count bounded under sustained race churn.
    private const int MaxQuarantineSize = 30;

    /// <summary>
    /// Quarantine helper: neutralize the GnuTLS session (close socket, mark
    /// session unusable), add the client to the bounded retain list, and
    /// decrement <see cref="_created"/>. When quarantine is full, evicts the
    /// oldest entry via best-effort force-dispose in a fire-and-forget task.
    /// Caller is responsible for decrementing <see cref="_active"/> first.
    /// </summary>
    private void QuarantineDecCreated(AsyncFtpClient client)
    {
        try { NeutralizeGnuTls(client); }
        catch (Exception ex) { Log.Debug(ex, "Pool: NeutralizeGnuTls during quarantine failed"); }

        AsyncFtpClient? evicted = null;
        int qSize;
        lock (_quarantineLock)
        {
            _quarantine.Add(client);
            if (_quarantine.Count > MaxQuarantineSize)
            {
                evicted = _quarantine[0];
                _quarantine.RemoveAt(0);
            }
            qSize = _quarantine.Count;
        }
        Interlocked.Decrement(ref _created);

        // Evict the oldest entry off-thread so the FluentFTP NoopDaemon +
        // keepalive timer don't keep the managed object alive forever. By
        // the time an entry reaches the head of the FIFO, several minutes
        // have passed since its socket close — most native corruption has
        // settled or torn down with the OS socket cleanup. We still wrap
        // Dispose() in try/catch (managed exceptions) but accept that a
        // native SEGV on the evicted entry is possible. Better than 2GB
        // of leaked threads.
        if (evicted != null)
        {
            var victim = evicted;
            _ = Task.Run(() =>
            {
                try { victim.Dispose(); }
                catch (Exception ex) { Log.Debug(ex, "Pool: quarantine eviction dispose threw (managed)"); }
            });
        }

        // Bumped from Debug to Information so the user can see quarantine
        // pressure without enabling Debug logging — useful for the next
        // round of memory/thread tuning.
        Log.Information("Pool: quarantined connection (quarantine={Q}/{Max}, created={Created}{Evict})",
            qSize, MaxQuarantineSize, _created, evicted != null ? ", evicted oldest" : "");
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

            var baseStreamProp = customStream.GetType().GetProperty("BaseStream");
            var gnuTlsInternal = baseStreamProp?.GetValue(customStream);
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

            // Check that the native session handle is non-zero (not freed/corrupted)
            var sessionField = gnuTlsInternal.GetType().GetField("_session",
                BindingFlags.NonPublic | BindingFlags.Instance)
                ?? gnuTlsInternal.GetType().GetField("session",
                    BindingFlags.NonPublic | BindingFlags.Instance);
            if (sessionField != null)
            {
                var sessionValue = sessionField.GetValue(gnuTlsInternal);
                if (sessionValue is IntPtr ptr && ptr == IntPtr.Zero)
                {
                    Log.Warning("Pool: GnuTLS native session handle is null — discarding connection");
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
                // The custom stream wraps GnuTlsInternalStream as its BaseStream.
                // Set IsSessionUsable = false to prevent gnutls_bye() in Dispose.
                var baseStreamProp = customStream.GetType().GetProperty("BaseStream");
                var gnuTlsInternal = baseStreamProp?.GetValue(customStream);
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
            await SafeDisconnectAndDispose(client);

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
