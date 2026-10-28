using Serilog;

namespace GlDrive.Downloads.Segmented;

/// <summary>
/// Offers the site's race login as an extra download lane while the site is not racing.
/// One lane at a time; the lane yields the moment a race for the site becomes active, starting
/// or queued, and the next poll after the race ends can borrow it again. Failed borrows back
/// off (see <see cref="LendBackoff"/>) so a site that cannot lend is not re-dialled every poll.
/// </summary>
internal sealed class RaceLoginLaneSource(
    IRaceLoginLender lender, string serverId, NetworkPath network, Func<NetworkPath, bool>? overAllowance,
    Func<long>? clockMs = null) : ILaneSource
{
    private static readonly TimeSpan BorrowWait = TimeSpan.FromSeconds(2);
    private readonly Func<long> _clockMs = clockMs ?? (() => Environment.TickCount64);
    private readonly LendBackoff _backoff = new();
    private int _lent;

    public async Task<ISegmentLane?> TryAcquireAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _lent) != 0 || !lender.IsRaceIdle(serverId)) return null;
        if (overAllowance?.Invoke(network) == true) return null;
        if (!_backoff.Ready(_clockMs())) return null;

        var conn = await lender.TryBorrowAsync(serverId, BorrowWait, ct);
        if (conn == null)
        {
            var delay = _backoff.Failed(_clockMs());
            Log.Debug("Download: race login on {Server} not lendable; next try in {Delay}s", serverId, (int)delay.TotalSeconds);
            return null;
        }
        _backoff.Succeeded();

        Interlocked.Exchange(ref _lent, 1);
        Log.Information("Download: borrowed the idle race login on {Server} as a second stream", serverId);
        return new FtpLane(conn, reborrowPool: null, useCpsv: true, network, "race login",
            yieldWhen: () => !lender.IsRaceIdle(serverId),
            overAllowance: overAllowance,
            onDisposed: () => Interlocked.Exchange(ref _lent, 0));
    }
}
