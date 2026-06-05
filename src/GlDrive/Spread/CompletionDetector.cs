namespace GlDrive.Spread;

/// <summary>Per-destination completion lifecycle state.</summary>
public enum DestState { Transferring, AwaitingCompletion, Complete, TimedOut }

/// <summary>
/// Pure (FTP-free, side-effect-free) logic for deciding whether a destination is
/// zipscript-complete, extracted from SpreadJob so it can be unit-tested. The
/// engine feeds it counts + scan-derived signals; it returns a DestState.
/// </summary>
public static class CompletionDetector
{
    /// <summary>True if a listing entry name contains any configured completion marker
    /// (case-insensitive). Empty marker list never matches.</summary>
    public static bool IsCompletionMarker(string name, IReadOnlyList<string> markers)
    {
        if (string.IsNullOrEmpty(name) || markers == null) return false;
        foreach (var m in markers)
        {
            if (string.IsNullOrEmpty(m)) continue;
            if (name.Contains(m, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>True for glftpd -MISSING- placeholder stubs (the INVERSE signal — the
    /// site LACKS the real file). Mirrors the subset of SpreadJob.IsZipscriptArtifact
    /// that specifically means "file absent".</summary>
    public static bool IsMissingStub(string name, long size)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (name.StartsWith("-missing-", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.EndsWith(".missing", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.EndsWith("-missing", StringComparison.OrdinalIgnoreCase)) return true;
        if (size == 0 && name.StartsWith('-')) return true;
        return false;
    }

    /// <summary>
    /// Decide a destination's completion state from delivered-file count, the expected
    /// total, and the two scan-derived signals. Marker wins outright; otherwise the
    /// heuristic requires the full file set present AND no -MISSING- stubs remaining.
    /// Returns AwaitingCompletion when all files are present but completion isn't yet
    /// confirmed (waiting on a marker / stub still present); Transferring otherwise.
    /// Never returns TimedOut — that is a time-based decision the caller layers on.
    /// </summary>
    public static DestState Evaluate(int owned, int expectedTotal, bool sawMarker, bool hasMissingStub)
    {
        if (sawMarker) return DestState.Complete;
        var haveAllFiles = expectedTotal > 0 && owned >= expectedTotal;
        if (haveAllFiles && !hasMissingStub) return DestState.Complete;
        if (haveAllFiles) return DestState.AwaitingCompletion;
        return DestState.Transferring;
    }

    /// <summary>Race is done only when there is at least one dest and every dest is in
    /// a terminal state (Complete or TimedOut).</summary>
    public static bool AllTerminal(IReadOnlyList<DestState> states)
    {
        if (states == null || states.Count == 0) return false;
        foreach (var s in states)
            if (s != DestState.Complete && s != DestState.TimedOut) return false;
        return true;
    }

    /// <summary>True when a dest has been awaiting completion past its minute budget.</summary>
    public static bool IsAwaitExpired(DateTime allFilesAt, DateTime now, int waitMinutes)
        => (now - allFilesAt) >= TimeSpan.FromMinutes(waitMinutes);
}
