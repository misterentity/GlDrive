namespace GlDrive.Services.Statistics;

public sealed record TelemetryReadCoverage(long BytesRead, int FilesRead, int RowsRead, int RowsInWindow,
    int InvalidRows, int OversizedRows, bool Partial, IReadOnlyList<string> Limits)
{
    public IReadOnlyList<TelemetryStreamCoverage> Streams { get; init; } = [];
}

public sealed record TelemetryStreamCoverage(string Stream, long BytesRead, int RowsRead, int RowsInWindow,
    DateTimeOffset? OldestObservationUtc, DateTimeOffset? NewestObservationUtc, bool Partial, IReadOnlyList<string> Limits);

public sealed record StatisticsSummary(DateTimeOffset FromUtc, DateTimeOffset ToUtc, TelemetryReadCoverage Coverage,
    RaceStatistics Races, DownloadStatistics Downloads, IReadOnlyList<DailyTraffic> Traffic,
    IReadOnlyList<RouteStatistics> Routes, IReadOnlyList<SiteObservationStatistics> Health)
{
    public IReadOnlyList<string> Notes { get; } =
    [
        "Payload is a telemetry estimate, not adapter or WAN usage. Historical unmarked duplicate skips can overstate bytes.",
        "Failed partial bytes and protocol overhead are excluded. Unknown historical transport remains in total bytes.",
        "Clean rate uses classified retained history; completion alone is not clean evidence. Sample counts describe coverage.",
        "Site-health samples do not establish uptime; unknown uptime is not zero."
    ];
}

public sealed record RaceStatistics(int Observed, int Completed, int CleanEvidenceSamples, int Clean,
    double? CleanRate, int QueueWaitSamples, double? QueueWaitMedianSeconds, double? QueueWaitP95Seconds,
    int AnnounceLagSamples, double? AnnounceLagMedianSeconds, double? AnnounceLagP95Seconds);

public sealed record DownloadStatistics(int Completed, int Failed, double? SuccessRate)
{
    public bool Partial { get; init; }
}

/// <summary>Endpoint payload totals, not adapter traffic or a WAN allowance measurement.</summary>
public sealed record DailyTraffic(string DayUtc, string ServerId, long SentBytes, long ReceivedBytes,
    long DownloadBytes, long RelaySentBytes, long RelayReceivedBytes, long UnknownTransportSentBytes,
    long UnknownTransportReceivedBytes)
{
    public double SentGB => SentBytes / 1_000_000_000d;
    public double ReceivedGB => ReceivedBytes / 1_000_000_000d;
    public double DownloadGB => DownloadBytes / 1_000_000_000d;
    public double RelaySentGB => RelaySentBytes / 1_000_000_000d;
    public double RelayReceivedGB => RelayReceivedBytes / 1_000_000_000d;
}

public sealed record RouteStatistics(string Source, string Destination, int CompletedTransfers,
    long Bytes, int TimedTransfers, double? MiBPerSecond);

/// <summary>Pool samples do not measure connection uptime between samples.</summary>
public sealed record SiteObservationStatistics(string ServerId, int Samples, double ObservedWindowHours,
    double WindowCoveragePercent, double? UptimePercent, DateTimeOffset? LastObservationUtc);
