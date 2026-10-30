using GlDrive.Ftp;
using Serilog;

namespace GlDrive.Spread;

public partial class SpreadJob
{
    // A local admission denial is not another failed LIST. Keep its deadline so
    // the next scan does not ask both empty pools the same question every 2–5s.
    private readonly Dictionary<string, DateTime> _scanAdmissionRetryAt = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (FtpConnectionPool? Main, FtpConnectionPool? Spread)> _scanAdmissionPools = new(StringComparer.Ordinal);

    private bool ScanPoolAdmissionDeferred(string serverId, FtpConnectionPool? pool)
    {
        // Reuse an idle authenticated connection. If every lease is busy, trying
        // Borrow on an undersized pool would attempt another held login first.
        // The next scan can use that existing lease as soon as it is returned.
        if (pool is { IsDisposed: false } && pool.TotalCreated > pool.ActiveCount) return false;
        var main = ResolveMainPool(serverId);
        var spread = ResolveSpreadPool(serverId);
        lock (_ownershipLock)
            return _scanAdmissionPools.TryGetValue(serverId, out var observed)
                && ReferenceEquals(observed.Main, main) && ReferenceEquals(observed.Spread, spread)
                && _scanAdmissionRetryAt.GetValueOrDefault(serverId) > DateTime.UtcNow;
    }

    internal bool IsScanAdmissionDeferred(string serverId)
        => ScanPoolAdmissionDeferred(serverId, ResolveMainPool(serverId))
            && ScanPoolAdmissionDeferred(serverId, ResolveSpreadPool(serverId));

    private void RememberScanAdmissionHold(string serverId, FtpLoginHeldException held)
    {
        var now = DateTime.UtcNow;
        var retryAt = held.RetryAtUtc is { } due && due > now ? due : now.AddSeconds(30);
        var pools = (Main: ResolveMainPool(serverId), Spread: ResolveSpreadPool(serverId));
        lock (_ownershipLock)
        {
            if (_scanAdmissionPools.GetValueOrDefault(serverId) == pools
                && _scanAdmissionRetryAt.GetValueOrDefault(serverId) >= retryAt) return;
            _scanAdmissionPools[serverId] = pools;
            _scanAdmissionRetryAt[serverId] = retryAt;
        }
        Log.Information("Spread scan admission deferred for {ServerId} until {RetryAt}: {Reason}",
            serverId, retryAt, held.Message);
    }
}
