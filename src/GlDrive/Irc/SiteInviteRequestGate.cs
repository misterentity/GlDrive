namespace GlDrive.Irc;

/// <summary>
/// Coalesces concurrent requests for the same site's <c>SITE INVITE</c> operation.
/// One SITE command invites the nick to every configured channel; letting each
/// channel's retry timer issue its own command creates a burst that can trip bot
/// flood protection and does not add any useful work.
/// </summary>
internal sealed class SiteInviteRequestGate
{
    private readonly object _sync = new();
    private Task? _inFlight;

    internal Task RunAsync(Func<Task> request)
    {
        lock (_sync)
        {
            if (_inFlight is { IsCompleted: false }) return _inFlight;

            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var task = completion.Task;
            _inFlight = task;
            _ = ExecuteAsync(request, completion);
            // ExecuteAsync may complete synchronously and clear _inFlight before this
            // method returns; keep the published task in a local for that case.
            return task;
        }
    }

    private async Task ExecuteAsync(Func<Task> request, TaskCompletionSource completion)
    {
        try
        {
            await request();
            completion.TrySetResult();
        }
        catch (OperationCanceledException ex)
        {
            completion.TrySetCanceled(ex.CancellationToken);
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_inFlight, completion.Task))
                    _inFlight = null;
            }
        }
    }
}
