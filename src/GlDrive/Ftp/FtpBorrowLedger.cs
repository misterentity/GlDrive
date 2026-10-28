namespace GlDrive.Ftp;

/// <summary>Coarse, non-secret work labels for active pooled connection leases.</summary>
public enum FtpBorrowPurpose
{
    Unspecified, Filesystem, Download, Search, Race, Cleanup, NukePoll, SiteStats,
    ConnectionMonitor, IrcInvite, Automation, Browse, Player
}

public sealed record FtpBorrowSnapshot(long LeaseId, FtpBorrowPurpose Purpose, DateTimeOffset BorrowedAtUtc);

internal sealed class FtpBorrowLedger(Func<DateTimeOffset>? clock = null)
{
    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly Dictionary<long, FtpBorrowSnapshot> _leases = [];
    private long _nextId;

    internal long Begin(FtpBorrowPurpose purpose)
    {
        lock (_gate)
        {
            var id = ++_nextId;
            _leases[id] = new(id, Enum.IsDefined(purpose) ? purpose : FtpBorrowPurpose.Unspecified, _clock());
            return id;
        }
    }

    internal void End(long id) { lock (_gate) _leases.Remove(id); }
    internal IReadOnlyList<FtpBorrowSnapshot> Snapshot()
    {
        lock (_gate) return _leases.Values.OrderBy(l => l.LeaseId).ToArray();
    }
}
