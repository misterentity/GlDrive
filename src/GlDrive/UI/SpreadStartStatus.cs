using GlDrive.Spread;

namespace GlDrive.UI;

internal static class SpreadStartStatus
{
    internal static string Format(RaceStartResult result, int serverCount) => result.Outcome switch
    {
        RaceStartOutcome.Started => $"Race started: {result.ReleaseName} — scanning {serverCount} servers for release...",
        RaceStartOutcome.Queued => $"Race queued: {result.ReleaseName}{Position(result.QueuePosition, result.Reason)}",
        RaceStartOutcome.AlreadyActive => $"Race already active: {result.ReleaseName}{Merged(result)}",
        RaceStartOutcome.Duplicate => $"Race already queued: {result.ReleaseName}{Position(result.QueuePosition, result.Reason)}{Merged(result)}",
        RaceStartOutcome.Suppressed => $"Race suppressed: {result.ReleaseName}" +
            (result.SuppressedFor is { } delay ? $" — retry in {Duration(delay)}" : " — retry time unavailable") +
            (string.IsNullOrWhiteSpace(result.Reason) ? "" : $" ({result.Reason})"),
        _ => $"Race not started: {result.ReleaseName} — {result.Reason ?? "spread engine unavailable"}"
    };

    private static string Merged(RaceStartResult result) => result.HintsMerged ? " — announce hints merged" : "";

    private static string Position(int? position, string? reason) => position is > 0
        ? $" — position {position}" : $" — {reason ?? "waiting for sites or a deferred retry"}";

    private static string Duration(TimeSpan delay)
    {
        var seconds = Math.Max(1, (long)Math.Ceiling(delay.TotalSeconds));
        return seconds < 60 ? $"{seconds} sec" : $"{(long)Math.Ceiling(seconds / 60.0)} min";
    }
}
