namespace GlDrive.Services;

/// <summary>
/// One explicit list action, deferred so Undo never has to restore stale snapshots.
/// The owner supplies its UI clock/timer and revalidates captured identities on apply.
/// Disposal cancels pending work; this class never starts a timer or background task.
/// </summary>
public sealed class DeferredBulkAction : IDisposable
{
    public static readonly TimeSpan UndoWindow = TimeSpan.FromSeconds(10);
    private readonly Lock _gate = new();
    private Pending? _pending;
    private bool _disposed;
    private sealed record Pending(string Description, DateTimeOffset DueAt, Action Apply);

    public string? Description { get { lock (_gate) return _pending?.Description; } }
    public DateTimeOffset? DueAt { get { lock (_gate) return _pending?.DueAt; } }

    public bool TrySchedule(string description, DateTimeOffset now, Action apply)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentNullException.ThrowIfNull(apply);
        lock (_gate)
        {
            if (_disposed || _pending != null) return false;
            _pending = new(description, now.Add(UndoWindow), apply);
            return true;
        }
    }

    public bool Undo()
    {
        lock (_gate)
        {
            if (_pending == null) return false;
            _pending = null;
            return true;
        }
    }

    public bool RunDue(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_disposed || _pending is not { } action || now < action.DueAt) return false;
            // Remove before calling user code. A failure must never silently retry a
            // partially applied deletion, and concurrent Undo cannot race execution.
            _pending = null;
            action.Apply();
            return true;
        }
    }

    public void Dispose()
    {
        lock (_gate) { _disposed = true; _pending = null; }
    }
}
