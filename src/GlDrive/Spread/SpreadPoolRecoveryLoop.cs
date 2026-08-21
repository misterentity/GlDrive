using GlDrive.Services;
using Serilog;

namespace GlDrive.Spread;

/// <summary>
/// Owns the lifetime of background spread-pool recovery attempts. A transient
/// startup failure must not disable FXP racing until process restart, but retrying
/// must also be single-flight and cancellable when a server is unmounted.
/// </summary>
internal sealed class SpreadPoolRecoveryLoop : IDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<string, RecoveryEntry> _entries = new();
    private readonly Func<int, TimeSpan> _delayFor;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private bool _disposed;

    internal SpreadPoolRecoveryLoop(
        Func<int, TimeSpan>? delayFor = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        _delayFor = delayFor ?? MountRetryPolicy.DelayFor;
        _delayAsync = delayAsync ?? Task.Delay;
    }

    /// <summary>
    /// Start one retry loop for <paramref name="serverId"/>. The callback returns
    /// true when recovery succeeded or is no longer desired; false schedules the
    /// next backoff step.
    /// </summary>
    internal bool Schedule(
        string serverId,
        Func<int, CancellationToken, Task<bool>> tryRecover)
    {
        RecoveryEntry entry;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            if (_disposed || _entries.ContainsKey(serverId)) return false;
            entry = new RecoveryEntry(new CancellationTokenSource());
            // The start gate makes Task assignment + dictionary publication atomic
            // from CancelAsync/Dispose's point of view. Without it, an immediate
            // retry could finish before its entry was fully visible.
            entry.Task = Task.Run(async () =>
            {
                await start.Task;
                await Run(serverId, entry, tryRecover);
            });
            _entries.Add(serverId, entry);
        }

        start.SetResult();
        return true;
    }

    internal async Task CancelAsync(string serverId)
    {
        RecoveryEntry? entry;
        lock (_sync) _entries.TryGetValue(serverId, out entry);
        if (entry == null) return;

        TryCancel(entry);
        try { await entry.Task; }
        catch (OperationCanceledException) { }
    }

    private async Task Run(
        string serverId,
        RecoveryEntry entry,
        Func<int, CancellationToken, Task<bool>> tryRecover)
    {
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                await _delayAsync(_delayFor(attempt), entry.Cancellation.Token);
                try
                {
                    if (await tryRecover(attempt, entry.Cancellation.Token)) return;
                }
                catch (OperationCanceledException) when (entry.Cancellation.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // A cleanup/logging failure inside the callback must not kill
                    // the self-healing loop and recreate the until-restart defect.
                    Log.Warning(ex, "Unexpected spread-pool recovery error for {ServerId} on attempt {Attempt}",
                        serverId, attempt);
                }
            }
        }
        catch (OperationCanceledException) when (entry.Cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_sync)
            {
                if (_entries.TryGetValue(serverId, out var current) &&
                    ReferenceEquals(current, entry))
                    _entries.Remove(serverId);
            }
            entry.Cancellation.Dispose();
        }
    }

    public void Dispose()
    {
        List<RecoveryEntry> entries;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            entries = _entries.Values.ToList();
        }

        foreach (var entry in entries) TryCancel(entry);
        try { Task.WaitAll(entries.Select(e => e.Task).ToArray(), TimeSpan.FromSeconds(5)); }
        catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is OperationCanceledException)) { }
    }

    private static void TryCancel(RecoveryEntry entry)
    {
        // Completion removes the entry and disposes its CTS. Cancellation can race
        // that cleanup after a snapshot, which is harmless and must stay no-throw.
        try { entry.Cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private sealed class RecoveryEntry(CancellationTokenSource cancellation)
    {
        internal CancellationTokenSource Cancellation { get; } = cancellation;
        internal Task Task { get; set; } = Task.CompletedTask;
    }
}
