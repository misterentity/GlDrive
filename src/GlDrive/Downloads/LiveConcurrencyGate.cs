namespace GlDrive.Downloads;

/// <summary>Resize admission without cancelling work or borrowing extra permits when shrinking.</summary>
internal sealed class LiveConcurrencyGate(int limit) : IDisposable
{
    private readonly Lock _sync = new();
    private int _limit = Math.Max(1, limit);
    private int _active;
    private bool _disposed;
    private TaskCompletionSource _changed = NewSignal();

    internal int CurrentCount { get { lock (_sync) return Math.Max(0, _limit - _active); } }

    internal async Task WaitAsync(CancellationToken ct = default)
    {
        while (true)
        {
            Task changed;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                ct.ThrowIfCancellationRequested();
                if (_active < _limit) { _active++; return; }
                changed = _changed.Task;
            }
            await changed.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    internal void Resize(int limit)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _limit = Math.Max(1, limit);
            Pulse();
        }
    }

    internal void Release()
    {
        lock (_sync)
        {
            if (_active <= 0) throw new InvalidOperationException("No active download slot to release.");
            _active--;
            Pulse();
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void Pulse()
    {
        var previous = _changed;
        _changed = NewSignal();
        previous.TrySetResult();
    }

    public void Dispose()
    {
        lock (_sync) { _disposed = true; Pulse(); }
    }
}
