using Serilog;

namespace GlDrive.Spread;

/// <summary>
/// Global per-server concurrency gate for FXP transfers. Each transfer must
/// hold BOTH the source-gate and the destination-gate before issuing the
/// RETR/STOR/PASV sequence. Per-job slot accounting (the previous design)
/// allowed N concurrent races each independently grabbing M slots on a
/// shared BNC source, so a 4-login BNC saw N*M attempts and rejected
/// N*M-3 of them — observed as "530 restricted to 4 simultaneous logins"
/// + "forcibly closed" + GnuTLS native crashes through 2026-05-15.
///
/// Acquire ordering is deterministic (sorted serverId) to avoid the
/// classic A->B / B->A deadlock when two transfers each hold one gate.
///
/// <see cref="TightenTo"/> permanently absorbs permits when we observe a
/// lower BNC login cap than the configured default — implements Option B
/// (auto-tune) of the v2.4 architecture plan.
/// </summary>
public sealed class ServerGate : IDisposable
{
    private readonly SemaphoreSlim _semaphore;
    private int _allowed;
    private bool _disposed;

    public string ServerId { get; }
    public int CurrentLimit { get { lock (this) return _allowed; } }

    public ServerGate(string serverId, int initialLimit)
    {
        ServerId = serverId;
        _allowed = Math.Max(1, initialLimit);
        // MaxCount sized generously so TightenTo never throws SemaphoreFullException
        // on absorbing permits.
        _semaphore = new SemaphoreSlim(_allowed, Math.Max(_allowed * 4, 32));
    }

    /// <summary>
    /// Acquire one permit. Throws TimeoutException if not acquired within
    /// <paramref name="timeout"/>; OperationCanceledException on token cancel.
    /// Disposing the returned handle returns the permit.
    /// </summary>
    public async Task<IAsyncDisposable> AcquireAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ServerGate));
        if (!await _semaphore.WaitAsync(timeout, ct))
            throw new TimeoutException(
                $"ServerGate '{ServerId}' acquire timed out after {timeout.TotalSeconds:F0}s (limit={CurrentLimit})");
        return new Releaser(_semaphore);
    }

    /// <summary>
    /// Lower the concurrency limit. Shrink-only; idempotent if <paramref name="newLimit"/>
    /// is greater-or-equal to the current limit. Permanently absorbs (oldLimit - newLimit)
    /// permits from the semaphore via background WaitAsync — those wait tasks complete
    /// as in-flight transfers release their permits and never release them back.
    /// </summary>
    public void TightenTo(int newLimit)
    {
        if (newLimit < 1) newLimit = 1;
        int diff;
        lock (this)
        {
            if (newLimit >= _allowed) return;
            diff = _allowed - newLimit;
            _allowed = newLimit;
        }
        for (int i = 0; i < diff; i++)
        {
            _ = _semaphore.WaitAsync(); // permanent absorb — never released
        }
        Log.Information("ServerGate: {Server} concurrency tightened to {Limit} (absorbed {Diff} permits)",
            ServerId, newLimit, diff);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _semaphore.Dispose(); } catch { }
    }

    private sealed class Releaser : IAsyncDisposable
    {
        private readonly SemaphoreSlim _sem;
        private int _released;
        public Releaser(SemaphoreSlim sem) { _sem = sem; }
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                try { _sem.Release(); } catch (ObjectDisposedException) { }
            }
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// Combines two per-server gate handles into one — released in reverse order
/// (second-then-first) so deadlock-safe ordering is preserved on dispose.
/// </summary>
public sealed class CombinedGateHandle : IAsyncDisposable
{
    private readonly IAsyncDisposable _first;
    private readonly IAsyncDisposable _second;
    private int _disposed;

    public CombinedGateHandle(IAsyncDisposable first, IAsyncDisposable second)
    {
        _first = first;
        _second = second;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _second.DisposeAsync();
        await _first.DisposeAsync();
    }
}
