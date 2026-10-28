namespace GlDrive.Spread;

public enum RaceStopReason { None, Ceiling, Stall, UserStop, AppShutdown, Deadline, SourceGone }

/// <summary>Durable ownership evidence for one release root, never a section or parent directory.</summary>
public sealed record OwnedRaceDirectory
{
    public string ServerId { get; init; } = "";
    public string RemotePath { get; init; } = "";
    public string AccountIdentity { get; init; } = "";
    public DateTime CreatedAtUtc { get; init; }
    public bool CompletionConfirmed { get; init; }
    public Dictionary<string, long> AttemptedFiles { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, long> DeliveredFiles { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    internal OwnedRaceDirectory Copy() => this with
    {
        AttemptedFiles = new(AttemptedFiles, StringComparer.OrdinalIgnoreCase),
        DeliveredFiles = new(DeliveredFiles, StringComparer.OrdinalIgnoreCase)
    };
}

public sealed record RacePartialState
{
    public DateTime? FirstUploadAtUtc { get; init; }
    public bool HasLandedPayload { get; init; }
    public List<OwnedRaceDirectory> Directories { get; init; } = [];
    public Dictionary<string, long> ExpectedFiles { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public int ExpectedCount { get; init; }
    internal RacePartialState Copy() => this with
    {
        Directories = Directories.Select(d => d.Copy()).ToList(),
        ExpectedFiles = new(ExpectedFiles, StringComparer.OrdinalIgnoreCase)
    };
}

internal enum PartialCleanupVerdict { Wipe, Complete, Shared, Unsafe, Unproven }

/// <summary>A current upload attempt's observations from listings already being made.</summary>
internal sealed class UploadListingProgress
{
    private long? _previousSize;
    internal long ObserveForAttempt(long size, UploadListingProgress? capturedAttempt) =>
        ReferenceEquals(this, capturedAttempt) ? Observe(size) : 0;

    internal long Observe(long size)
    {
        if (size < 0) return 0;
        var delta = _previousSize is { } previous && size > previous ? size - previous : 0;
        _previousSize = size;
        return delta;
    }
}

internal static class RaceLifetimePolicy
{
    internal const int MaxPartialResumes = 3;
    internal const int MaxCleanupAttempts = 6;
    internal static readonly TimeSpan CleanupRetryDelay = TimeSpan.FromMinutes(5);

    internal static DateTime? Deadline(RacePartialState? partial, int hours) =>
        partial?.FirstUploadAtUtc is { } first ? AddHoursSafe(first, Math.Clamp(hours > 0 ? hours : 10, 1, 11)) : null;

    private static DateTime AddHoursSafe(DateTime value, int hours) =>
        value > DateTime.MaxValue.AddHours(-hours) ? DateTime.MaxValue : value.AddHours(hours);

    internal static string AccountIdentity(GlDrive.Config.ConnectionConfig connection) =>
        AccountIdentity(connection.Host, connection.Port, connection.Username);

    internal static string AccountIdentity(string host, int port, string username) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            $"{host.Trim().ToLowerInvariant()}\n{port}\n{username}")));

    internal static bool SameAccount(OwnedRaceDirectory directory, GlDrive.Config.ConnectionConfig connection) =>
        !string.IsNullOrEmpty(directory.AccountIdentity)
        && directory.AccountIdentity == AccountIdentity(connection);

    internal static RaceStopReason TimeoutReason(DateTime started, DateTime lastProgress,
        DateTime? firstUpload, DateTime now, int idleSeconds, int absoluteMinutes, int deadlineHours)
        => TimeoutReason(now - started, now - lastProgress, firstUpload, now, idleSeconds, absoluteMinutes, deadlineHours);

    internal static RaceStopReason TimeoutReason(TimeSpan elapsed, TimeSpan idle,
        DateTime? firstUpload, DateTime now, int idleSeconds, int absoluteMinutes, int deadlineHours)
    {
        if (firstUpload is { } first && now >= AddHoursSafe(first, Math.Clamp(deadlineHours > 0 ? deadlineHours : 10, 1, 11)))
            return RaceStopReason.Deadline;
        if (elapsed >= TimeSpan.FromMinutes(Math.Clamp(absoluteMinutes > 0 ? absoluteMinutes : 90, 1, 1440)))
            return RaceStopReason.Ceiling;
        return idle >= TimeSpan.FromSeconds(idleSeconds > 0 ? idleSeconds : 1200)
            ? RaceStopReason.Stall : RaceStopReason.None;
    }

    internal static bool CanResume(RaceStopReason reason, int resumes, DateTime? deadline, DateTime now) =>
        (reason is RaceStopReason.Ceiling or RaceStopReason.Stall) && resumes < MaxPartialResumes
        && (deadline == null || now < deadline);

    internal static bool SafeRoot(string path, string release) =>
        !string.IsNullOrWhiteSpace(release) && !release.StartsWith('.') && SafeRelative(release) && !release.Contains('/')
        && !string.IsNullOrWhiteSpace(path) && path.Trim('/').Length > 0 && path.StartsWith('/')
        && !path.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || "*?[]{}\"'`;|".Contains(c))
        && !path.Contains('\\') && path.Split('/').All(p => p is not ("." or ".."))
        && path.TrimEnd('/').Split('/').Last().Equals(release, StringComparison.Ordinal);

    internal static bool SafeRelative(string path) => !string.IsNullOrWhiteSpace(path)
        && !path.StartsWith('/') && !path.Contains('\\') && !path.Any(char.IsControl)
        && path.Split('/').All(p => p.Length > 0 && p is not ("." or ".."));

    internal static int QueuePriority(PersistedRace request) =>
        request.CleanupOnly ? 0 : request.ResumeCount == 0 && !request.HasOwnedPartial ? 1 : 2;

    internal static bool IsWholeReleaseCompletionMarker(string relativeDirectory, string name,
        IReadOnlyList<string> markers) => relativeDirectory.Length == 0
        && CompletionDetector.IsCompletionMarker(name, markers);

    internal static bool IsIncompleteReleaseMarker(string name, long size) =>
        CompletionDetector.IsMissingStub(name, size) || GlDrive.Downloads.ReleaseSourceState.IsIncompleteMarker(name);

    internal static PartialCleanupVerdict CleanupVerdict(string release, OwnedRaceDirectory owned,
        IReadOnlyDictionary<string, long> files, bool completeMarker, bool missingMarker,
        RacePartialState state, bool unknownDirectory)
    {
        if (!SafeRoot(owned.RemotePath, release) || files.Keys.Any(p => !SafeRelative(p)))
            return PartialCleanupVerdict.Unsafe;
        if (completeMarker || owned.CompletionConfirmed) return PartialCleanupVerdict.Complete;
        if (unknownDirectory || files.Any(f => !owned.AttemptedFiles.TryGetValue(f.Key, out var size)
                || size < 0 || f.Value > size)) return PartialCleanupVerdict.Shared;
        // Prefer preserving a possibly complete release to deleting it. Missing stubs
        // disqualify the count/size heuristic; an authoritative completion marker wins.
        if (!missingMarker && state.ExpectedCount > 0 && files.Count >= state.ExpectedCount
            && state.ExpectedFiles.Count > 0 && state.ExpectedFiles.All(f =>
                files.TryGetValue(f.Key, out var size) && (f.Value < 0 || size == f.Value)))
            return PartialCleanupVerdict.Complete;
        // Without an SFV inventory or a missing-part stub nothing proves the release is
        // incomplete (its completion marker may simply be unrecognised). SITE WIPE is
        // irreversible, so only positive evidence of incompleteness may delete it.
        if (!missingMarker && state.ExpectedFiles.Count == 0) return PartialCleanupVerdict.Unproven;
        return PartialCleanupVerdict.Wipe;
    }

    internal static PersistedRace CleanupRequest(PersistedRace request, DateTime now, string reason,
        DateTime? at = null) => request with
    {
        WasActive = false, CleanupOnly = true, KeepUntilAvailable = true,
        NextProbeAtUtc = at ?? now, WaitReason = reason
    };

    internal static PersistedRace RetryCleanup(PersistedRace request, DateTime now) => request with
    {
        WasActive = false, CleanupOnly = true, KeepUntilAvailable = true,
        CleanupAttempts = request.CleanupAttempts + 1,
        NextProbeAtUtc = request.CleanupAttempts + 1 >= MaxCleanupAttempts ? null : now + CleanupRetryDelay,
        WaitReason = request.CleanupAttempts + 1 >= MaxCleanupAttempts
            ? "Partial cleanup exhausted its retry budget; manual attention required (ownership retained)"
            : "Partial cleanup unavailable; retrying in 5 minutes"
    };
}
