namespace GlDrive.Spread;

public partial class SpreadJob
{
    private int _resetRequested;
    internal Func<string, string, bool>? IsReleaseNuked { get; set; }

    internal bool KnownNuked(string serverId)
    {
        try { return IsReleaseNuked?.Invoke(serverId, ReleaseName) ?? false; }
        catch { return true; }
    }

    internal bool RequestReset()
    {
        if (_cleanupOnly || State is not (SpreadJobState.Running or SpreadJobState.Paused)) return false;
        Interlocked.Exchange(ref _resetRequested, 1);
        return true;
    }

    internal bool ApplyPendingReset()
    {
        if (_backgroundScan is { IsCompleted: false } || Interlocked.Exchange(ref _resetRequested, 0) == 0) return false;
        lock (_failureLock) _failureCounts.Clear();
        lock (_ownershipLock)
        {
            _destFailureCount.Clear();
            _destRetryAt.Clear();
        }
        // Keep proven ownership, MKD policy denials, source integrity evidence,
        // native reads and all pool/login backoff intact. This is a rescan reset.
        _forceScan = true;
        _lastSourceScanTime = DateTime.MinValue;
        return true;
    }
}
