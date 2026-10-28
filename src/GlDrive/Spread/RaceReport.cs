using System.Globalization;
using GlDrive.AiAgent;

namespace GlDrive.Spread;

public sealed record RaceReportTotals(
    int RaceAttempts, int CompletedAttempts, int PartialAttempts, int AttemptsWithoutDestinationEvidence,
    int DestinationSamples, int ObservedWinnerSamples, int Wins, int Losses,
    int SoloWins, int SoloLosses, int UnknownWinners,
    double? WinRate, double WinnerCoverage,
    long RecordedBytesReceived, long RecordedBytesSent, long UnattributedTransferredBytes,
    int QueueWaitSamples, double? MedianQueueWaitSeconds, double? P90QueueWaitSeconds);

public sealed record RaceReportBucket(string Key, RaceReportTotals Totals);

public sealed record RaceReportCoverage
{
    public string Source { get; init; } = "telemetry";
    public string Note { get; init; } = "Counts are recorded race attempts, including resumes. Unknown winners are excluded from win rates. Byte totals are the counters recorded by the race engine, not a complete traffic ledger.";
    public int FilesRead { get; init; }
    public int FileDaysExpected { get; init; }
    public int FileDaysPresent { get; init; }
    public int ParseSkips { get; init; }
    public int ReadErrors { get; init; }
    public int InvalidRows { get; init; }
    public int DuplicateRows { get; init; }
    public bool Truncated { get; init; }
    public bool IsPartial { get; init; }
    public DateTimeOffset? FirstSampleUtc { get; init; }
    public DateTimeOffset? LastSampleUtc { get; init; }
}

public sealed record RaceReport(
    DateTimeOffset WindowStartUtc, DateTimeOffset WindowEndUtc,
    RaceReportTotals Summary,
    IReadOnlyList<RaceReportBucket> ByDay,
    IReadOnlyList<RaceReportBucket> BySection,
    IReadOnlyList<RaceReportBucket> BySite,
    RaceReportCoverage Coverage);

/// <summary>Read-only aggregation. Only verified, explicit destination winners count as observations.</summary>
public static class RaceReportBuilder
{
    private sealed record Sample(RaceOutcomeEvent Race, DateTimeOffset EndedAt,
        IReadOnlyList<Destination> Destinations);
    private sealed record Destination(string ServerId, RaceDestinationOutcome? Outcome, long ReceivedBytes);

    internal static RaceReport BuildHistory(IReadOnlyList<RaceHistoryItem> history,
        DateTimeOffset fromUtc, DateTimeOffset toUtc, RaceReportCoverage coverage)
    {
        var candidates = history.Where(h => h != null && h.CompletedAt != default)
            .Select(h => (History: h, Ended: new DateTimeOffset(h.CompletedAt.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(h.CompletedAt, DateTimeKind.Utc) : h.CompletedAt).ToUniversalTime()))
            .Select(h => (h.History, h.Ended, Event: new RaceOutcomeEvent
            {
                RaceId = h.History.Id, Section = h.History.Section, Release = h.History.ReleaseName,
                StartedAt = h.History.StartedAt.ToString("O"), EndedAt = h.Ended.ToString("O"),
                Result = h.History.Result == SpreadJobState.Completed ? "complete" : "aborted",
                DestinationOutcomes = h.History.DestinationOutcomes, QueueWaitSeconds = h.History.QueueWaitSeconds
            })).ToArray();
        var invalid = history.Count - candidates.Length + candidates.Count(c => !Valid(c.Event, out _) || c.History.BytesTransferred < 0);
        var rows = candidates.Where(c => Valid(c.Event, out _) && c.History.BytesTransferred >= 0)
            .Where(h => h.Ended >= fromUtc && h.Ended <= toUtc)
            .DistinctBy(h => (h.History.Id, h.History.StartedAt, h.History.CompletedAt)).ToArray();
        var report = Build(rows.Select(h => h.Event), fromUtc, toUtc, coverage with
        {
            InvalidRows = coverage.InvalidRows + invalid, IsPartial = coverage.IsPartial || invalid > 0
        });
        RaceReportTotals Supplement(RaceReportTotals totals, IEnumerable<RaceHistoryItem> subset)
        {
            var items = subset.ToArray();
            return totals with
            {
                UnattributedTransferredBytes = SaturatingSum(items.Select(h => Math.Max(0, h.BytesTransferred))),
                PartialAttempts = items.Count(h => !h.CleanComplete && h.BytesTransferred > 0)
            };
        }
        return report with
        {
            Summary = Supplement(report.Summary, rows.Select(r => r.History)),
            ByDay = report.ByDay.Select(b => b with { Totals = Supplement(b.Totals,
                rows.Where(r => r.Ended.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) == b.Key).Select(r => r.History)) }).ToArray(),
            BySection = report.BySection.Select(b => b with { Totals = Supplement(b.Totals,
                rows.Where(r => string.Equals(r.History.Section, b.Key, StringComparison.OrdinalIgnoreCase)).Select(r => r.History)) }).ToArray()
        };
    }

    public static RaceReport Build(IEnumerable<RaceOutcomeEvent> events,
        DateTimeOffset fromUtc, DateTimeOffset toUtc, RaceReportCoverage? coverage = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (toUtc < fromUtc) throw new ArgumentException("Report end must not precede its start.");
        var samples = new List<Sample>();
        var seen = new HashSet<(string Id, string Started, DateTimeOffset Ended)>();
        var invalid = 0;
        var duplicates = 0;
        foreach (var race in events)
        {
            if (!Valid(race, out var ended)) { invalid++; continue; }
            if (ended < fromUtc || ended > toUtc) continue;
            if (!seen.Add((race.RaceId, race.StartedAt, ended))) { duplicates++; continue; }
            var participants = race.Participants.Where(p => p.Role == "dst").ToArray();
            var ids = participants.Select(p => p.ServerId).Concat(race.DestinationOutcomes.Select(d => d.ServerId))
                .Distinct(StringComparer.Ordinal);
            samples.Add(new(race, ended, ids.Select(id => new Destination(id,
                race.DestinationOutcomes.SingleOrDefault(d => d.ServerId == id),
                SaturatingSum(participants.Where(p => p.ServerId == id).Select(p => p.Bytes)))).ToArray()));
        }

        coverage ??= new();
        coverage = coverage with
        {
            InvalidRows = coverage.InvalidRows + invalid,
            DuplicateRows = coverage.DuplicateRows + duplicates,
            IsPartial = coverage.IsPartial || invalid > 0,
            FirstSampleUtc = samples.Count == 0 ? null : samples.Min(s => s.EndedAt),
            LastSampleUtc = samples.Count == 0 ? null : samples.Max(s => s.EndedAt)
        };
        var sites = samples.SelectMany(s => s.Race.Participants.Select(p => p.ServerId)
                .Concat(s.Destinations.Select(d => d.ServerId))).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        return new(fromUtc.ToUniversalTime(), toUtc.ToUniversalTime(), Summarize(samples),
            samples.GroupBy(s => s.EndedAt.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => new RaceReportBucket(g.Key, Summarize(g))).ToArray(),
            samples.GroupBy(s => s.Race.Section, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase).Select(g => new RaceReportBucket(g.Key, Summarize(g))).ToArray(),
            sites.Select(site => new RaceReportBucket(site, Summarize(samples.Where(s =>
                s.Race.Participants.Any(p => p.ServerId == site) || s.Destinations.Any(d => d.ServerId == site)), site))).ToArray(),
            coverage);
    }

    private static RaceReportTotals Summarize(IEnumerable<Sample> source, string? site = null)
    {
        var samples = source.ToArray();
        var destinations = samples.SelectMany(s => s.Destinations).Where(d => site == null || d.ServerId == site).ToArray();
        var observed = destinations.Where(d => d.Outcome != null && SitebotOutcomePolicy.HasObservedWinner(d.Outcome)).ToArray();
        var wins = observed.Count(d => SitebotOutcomePolicy.IsOwnWin(d.Outcome!));
        var waits = samples.Select(s => s.Race.QueueWaitSeconds).Where(w => w.HasValue && double.IsFinite(w.Value) && w.Value >= 0)
            .Select(w => w!.Value).Order().ToArray();
        return new(samples.Length, samples.Count(s => s.Race.Result == "complete"),
            samples.Count(s => s.Race.Result != "complete" && s.Destinations.Any(d =>
                (site == null || d.ServerId == site) && d.ReceivedBytes > 0 && d.Outcome?.CompletionVerified != true)),
            samples.Count(s => s.Destinations.Count == 0),
            destinations.Length, observed.Length, wins, observed.Length - wins,
            observed.Count(d => d.Outcome!.Solo && SitebotOutcomePolicy.IsOwnWin(d.Outcome)),
            observed.Count(d => d.Outcome!.Solo && !SitebotOutcomePolicy.IsOwnWin(d.Outcome)),
            destinations.Length - observed.Length,
            observed.Length == 0 ? null : (double)wins / observed.Length,
            destinations.Length == 0 ? 0 : (double)observed.Length / destinations.Length,
            SaturatingSum(destinations.Select(d => d.ReceivedBytes)),
            SaturatingSum(samples.SelectMany(s => s.Race.Participants).Where(p => p.Role == "src" &&
                (site == null || p.ServerId == site)).Select(p => p.Bytes)),
            0,
            waits.Length, Percentile(waits, 0.5), Percentile(waits, 0.9));
    }

    internal static bool Valid(RaceOutcomeEvent? race, out DateTimeOffset ended)
    {
        ended = default;
        return race != null && !string.IsNullOrWhiteSpace(race.RaceId) && race.RaceId.Length <= 128 &&
            race.Section != null && race.Section.Length <= 512 &&
            TryTimestamp(string.IsNullOrEmpty(race.EndedAt) ? race.Ts : race.EndedAt, out ended) &&
            race.Participants != null && race.Participants.Count <= 1000 &&
            race.Participants.All(p => p != null && !string.IsNullOrWhiteSpace(p.ServerId) && p.ServerId.Length <= 128 &&
                p.Role is "src" or "dst" && p.Bytes >= 0 && p.Files >= 0) &&
            race.DestinationOutcomes != null && race.DestinationOutcomes.Count <= 1000 &&
            race.DestinationOutcomes.All(d => d != null && !string.IsNullOrWhiteSpace(d.ServerId) && d.ServerId.Length <= 128) &&
            race.DestinationOutcomes.Select(d => d.ServerId).Distinct(StringComparer.Ordinal).Count() == race.DestinationOutcomes.Count;
    }

    internal static bool TryTimestamp(string? value, out DateTimeOffset timestamp) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out timestamp);

    private static double? Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return null;
        var index = (sorted.Length - 1) * p;
        var lower = (int)Math.Floor(index);
        return sorted[lower] + (sorted[(int)Math.Ceiling(index)] - sorted[lower]) * (index - lower);
    }

    private static long SaturatingSum(IEnumerable<long> values)
    {
        long total = 0;
        foreach (var value in values) total = value >= long.MaxValue - total ? long.MaxValue : total + value;
        return total;
    }
}
