using GlDrive.Spread;

namespace GlDrive.Downloads;

public sealed record DownloadSourceCandidate(string ServerId, string ReleaseName, string RemotePath,
    bool Available = true);

public sealed record RankedDownloadSource(DownloadSourceCandidate Source, double BytesPerSecond)
{
    public bool HasMeasuredSpeed => BytesPerSecond > 0;
}

/// <summary>Orders copies of the same release using observed local download routes only.</summary>
public static class DownloadSourceRanker
{
    // A distinct destination keeps local-download measurements separate from FXP
    // route speeds. Neither a private address nor a fast upload proves pull speed.
    public const string LocalDestination = "$local-download";

    public static double MeasuredSpeed(SpeedTracker tracker, string serverId)
    {
        var speed = tracker.GetAverageSpeed(serverId, LocalDestination);
        return double.IsFinite(speed) && speed > 0 ? speed : 0;
    }

    public static IReadOnlyList<RankedDownloadSource> Rank(string releaseName,
        IEnumerable<DownloadSourceCandidate> candidates, SpeedTracker tracker)
        => candidates.Where(candidate => candidate.Available && !string.IsNullOrWhiteSpace(candidate.ServerId) &&
                string.Equals(candidate.ReleaseName, releaseName, StringComparison.OrdinalIgnoreCase) &&
                RacePullPolicy.SafeReleasePath(candidate.ReleaseName, candidate.RemotePath))
            .DistinctBy(candidate => (candidate.ServerId, candidate.RemotePath))
            .Select(candidate => new RankedDownloadSource(candidate, MeasuredSpeed(tracker, candidate.ServerId)))
            .OrderByDescending(candidate => candidate.BytesPerSecond)
            .ThenBy(candidate => candidate.Source.ServerId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Source.RemotePath, StringComparer.Ordinal)
            .ToArray();
}
