namespace GlDrive.Spread;

public enum RaceStartOutcome { Started, Queued, AlreadyActive, Duplicate, Suppressed, Unavailable }

/// <summary>The admission decision, captured before another request can change the queue.</summary>
public sealed record RaceStartResult(
    RaceStartOutcome Outcome,
    string Section,
    string ReleaseName,
    SpreadJob? Job = null,
    int? QueuePosition = null,
    TimeSpan? SuppressedFor = null,
    string? Reason = null);
