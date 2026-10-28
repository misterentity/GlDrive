namespace GlDrive.AiAgent;

/// <summary>
/// Trusted local evidence for one validation batch. This is supplied by the application,
/// never deserialized from a model proposal. Snapshot counts before evaluating mutations.
/// </summary>
public sealed class ChangeValidationContext
{
    private readonly HashSet<string> _sufficientWinnerCoverage;
    public bool ExplicitUndo { get; }

    public ChangeValidationContext(RacesDigest? races = null, bool explicitUndo = false)
    {
        ExplicitUndo = explicitUndo;
        _sufficientWinnerCoverage = races is { WinnerEvidenceComplete: true }
            ? races.ParticipationByServer.Where(p => HasSufficientCoverage(
                races.KnownWinnerSamplesByServer.GetValueOrDefault(p.Key), p.Value))
                .Select(p => p.Key).ToHashSet(StringComparer.Ordinal)
            : [];
    }

    internal bool AllowsPriorityOrRole(string? serverId) => ExplicitUndo
        || serverId != null && _sufficientWinnerCoverage.Contains(serverId);

    internal static bool HasSufficientCoverage(int known, int total) => total > 0
        && known >= 0 && known <= total && (long)known * 2 >= total;

    internal const string InsufficientCoverage = "insufficient-winner-coverage";
}
