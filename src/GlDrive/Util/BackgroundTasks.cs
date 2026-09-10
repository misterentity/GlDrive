namespace GlDrive.Util;

internal sealed class BackgroundTasks
{
    private readonly object _gate = new();
    private readonly HashSet<Task> _tasks = new();
    private bool _stopped;

    internal bool TryRun(Func<Task> work)
    {
        lock (_gate)
        {
            if (_stopped) return false;
            var task = Task.Run(work);
            _tasks.Add(task);
            _ = task.ContinueWith(done =>
            {
                _ = done.Exception; // Observe failed workers even if they finished before the drain.
                lock (_gate) _tasks.Remove(done);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return true;
        }
    }

    internal Task StopAsync()
    {
        lock (_gate) { _stopped = true; return Task.WhenAll(_tasks); }
    }
}
