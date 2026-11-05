namespace GlDrive.Downloads.ArchiveExtraction;

/// <summary>
/// Where the engine touches its queue. A bound window needs every <see cref="ArchiveExtractionEngine.Archives"/>
/// mutation on its UI thread, so the host hands in that thread's SynchronizationContext.
/// Without one (tests, headless use) work runs inline, serialized by a lock.
/// </summary>
internal sealed class EngineDispatcher(SynchronizationContext? context)
{
    private readonly object _inlineGate = new();

    /// <summary>Fire-and-forget, like Dispatcher.BeginInvoke.</summary>
    public void Post(Action action)
    {
        if (context == null)
        {
            lock (_inlineGate) action();
            return;
        }
        context.Post(static state => ((Action)state!)(), action);
    }

    /// <summary>Run and wait, like Dispatcher.Invoke (inline when already on the context's thread).</summary>
    public void Send(Action action)
    {
        if (context == null)
        {
            lock (_inlineGate) action();
            return;
        }
        context.Send(static state => ((Action)state!)(), action);
    }

    public Task InvokeAsync(Action action) => InvokeAsync(() =>
    {
        action();
        return true;
    });

    /// <summary>Queue and await, like Dispatcher.InvokeAsync. Exceptions surface at the await.</summary>
    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        if (context == null)
        {
            try
            {
                lock (_inlineGate) return Task.FromResult(func());
            }
            catch (Exception ex)
            {
                return Task.FromException<T>(ex);
            }
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Post(_ =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        }, null);
        return tcs.Task;
    }
}
