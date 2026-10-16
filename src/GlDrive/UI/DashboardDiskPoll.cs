using Serilog;

namespace GlDrive.UI;

/// <summary>Owns the dashboard's single disk poll until its actual operation has drained.</summary>
internal sealed class DashboardDiskPoll
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private Task _running = Task.CompletedTask;
    private TaskCompletionSource? _shutdown;

    internal bool TryStart(Func<CancellationToken, Task> poll)
    {
        TaskCompletionSource completion;
        CancellationToken token;
        lock (_gate)
        {
            if (_shutdown != null || !_running.IsCompleted) return false;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _running = completion.Task;
            token = _stop.Token;
        }
        _ = RunAsync(poll, token, completion);
        return true;
    }

    private static async Task RunAsync(Func<CancellationToken, Task> poll,
        CancellationToken token, TaskCompletionSource completion)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            await poll(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { Log.Debug(ex, "Dashboard disk poll failed"); }
        finally { completion.TrySetResult(); }
    }

    internal Task StopAsync()
    {
        Task running;
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_shutdown != null) return _shutdown.Task;
            completion = _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
            running = _running;
        }
        _ = DrainAsync(running, completion);
        return completion.Task;
    }

    private async Task DrainAsync(Task running, TaskCompletionSource completion)
    {
        try
        {
            try { _stop.Cancel(); }
            catch (Exception ex) { Log.Debug(ex, "Dashboard disk poll cancellation failed"); }
            // Do not abandon a native FTP operation with WaitAsync or dispose its pool.
            // The operation owns its borrowed connection until its awaited cleanup ends.
            await running.ConfigureAwait(false);
        }
        finally
        {
            _stop.Dispose();
            completion.TrySetResult();
        }
    }
}
