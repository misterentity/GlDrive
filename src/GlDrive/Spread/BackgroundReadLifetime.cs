using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GlDrive.Spread;

/// <summary>
/// Bounds a race's fire-and-forget reads (the background scan, the SFV parse) to the
/// race's own lifetime.
///
/// v3.11.9. <c>RunAsync</c> returned on completion with <c>ScanSites</c> still in flight,
/// and the scan's token came from a CTS that is DISPOSED — never cancelled — on exit. The
/// orphan kept waiting on pool borrows for up to ~55 s after "Spread race complete",
/// competing with the NEXT race for the same account's logins (2026-10-14 11:51:29:
/// S18E05's scan poisoned a superbnc login 28 s after that race completed, while S18E06
/// was already dispatching on a 1-usable-login spread pool). Its results then landed in
/// a job whose cleanup and history had already run.
///
/// Stopping cancels only NEW borrows. A LIST or SFV read already on the wire is allowed
/// to finish, because cancelling a read mid-stream poisons the GnuTLS session and costs a
/// reconnect — the very login churn this exists to stop.
/// </summary>
internal sealed class BackgroundReadLifetime
{
    private readonly CancellationTokenSource _stop;
    private int _stopped;

    public BackgroundReadLifetime(CancellationToken jobToken)
    {
        _stop = CancellationTokenSource.CreateLinkedTokenSource(jobToken);
        StopToken = _stop.Token;
    }

    /// <summary>Link every borrow a background read makes to this token.</summary>
    public CancellationToken StopToken { get; }

    public bool IsStopped => StopToken.IsCancellationRequested;

    /// <summary>
    /// Refuses new borrows, then waits (at most <paramref name="cap"/>) for the given reads
    /// to finish so the caller's cleanup never overlaps them. Faults are observed, never
    /// rethrown: a read that failed after the race ended changes nothing. Returns false if
    /// the cap expired with a read still running.
    /// </summary>
    public async Task<bool> StopAndDrainAsync(TimeSpan cap, params Task?[] reads)
    {
        // Once only: a second call would Cancel() a disposed source and throw out of a finally.
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return true;
        _stop.Cancel();

        var running = reads.Where(t => t is { IsCompleted: false }).Cast<Task>().ToArray();
        if (running.Length > 0)
            await Task.WhenAny(Task.WhenAll(running), Task.Delay(cap)).ConfigureAwait(false);
        var drained = running.All(t => t.IsCompleted);

        foreach (var read in reads)
        {
            if (read == null) continue;
            if (read.IsCompleted) _ = read.Exception;
            else _ = read.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }

        // Safe even with a read still running: the source is already cancelled, so its
        // linked borrow CTS has fired and only unregisters (a no-op) when it is disposed.
        _stop.Dispose();
        return drained;
    }
}
