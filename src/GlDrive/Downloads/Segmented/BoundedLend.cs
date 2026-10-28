using Serilog;

namespace GlDrive.Downloads.Segmented;

/// <summary>
/// Bounds how long a lender's caller blocks without cancelling the borrow itself. A fresh race
/// login takes ~2 s at a 150 ms round trip; cancelling it at the lane's 2 s wait threw the
/// finished login away and the 5 s lane poll dialled again (58 source-site logins in five
/// minutes on 2026-10-27). A borrow still running when the wait ends now finishes within its
/// own budget and is released straight back to its pool, where the next poll finds it idle.
/// </summary>
internal static class BoundedLend
{
    internal static async Task<T?> WaitOrReleaseAsync<T>(
        Func<CancellationToken, Task<T>> borrow, Func<T, ValueTask> release,
        TimeSpan wait, TimeSpan budget, CancellationToken ct) where T : class
    {
        ct.ThrowIfCancellationRequested();
        var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budgetCts.CancelAfter(budget);
        Task<T> pending;
        try { pending = borrow(budgetCts.Token); }
        catch
        {
            budgetCts.Dispose();
            throw;
        }

        using (var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            var winner = await Task.WhenAny(pending, Task.Delay(wait, waitCts.Token)).ConfigureAwait(false);
            waitCts.Cancel();
            if (winner == pending)
            {
                budgetCts.Dispose();
                return await pending.ConfigureAwait(false);
            }
        }

        _ = ReleaseWhenDoneAsync(pending, release, budgetCts);
        ct.ThrowIfCancellationRequested();
        return null;
    }

    private static async Task ReleaseWhenDoneAsync<T>(Task<T> pending, Func<T, ValueTask> release,
        CancellationTokenSource budgetCts)
    {
        try
        {
            var item = await pending.ConfigureAwait(false);
            await release(item).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // The pool already classified and logged a failed login; this only keeps the
            // abandoned task observed.
            Log.Debug(ex, "Lent connection finished after its caller stopped waiting and could not be released");
        }
        finally { budgetCts.Dispose(); }
    }
}

/// <summary>
/// Spaces out failed lend attempts: 5 s, 10 s, 20 s ... up to 5 minutes. A successful borrow resets it.
/// </summary>
internal sealed class LendBackoff
{
    internal static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan MaxRetry = TimeSpan.FromMinutes(5);
    private int _failures;
    private long _nextAttemptMs;

    internal bool Ready(long nowMs) => nowMs >= _nextAttemptMs;

    internal TimeSpan Failed(long nowMs)
    {
        _failures = Math.Min(_failures + 1, 30);
        var delayMs = Math.Min(FirstRetry.TotalMilliseconds * Math.Pow(2, _failures - 1), MaxRetry.TotalMilliseconds);
        _nextAttemptMs = nowMs + (long)delayMs;
        return TimeSpan.FromMilliseconds(delayMs);
    }

    internal void Succeeded()
    {
        _failures = 0;
        _nextAttemptMs = 0;
    }
}
