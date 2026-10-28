using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using GlDrive.AiAgent;

namespace GlDrive.Spread;

/// <summary>Bounded local telemetry reader. It never creates directories, sends traffic or mutates history.</summary>
public sealed class RaceReportReader
{
    private readonly string _root;
    private readonly RaceReportReadLimits _limits;
    private readonly Func<string, Stream> _open;

    public RaceReportReader(string aiDataRoot) : this(aiDataRoot, new(), path =>
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) { }

    internal RaceReportReader(string aiDataRoot, RaceReportReadLimits limits, Func<string, Stream>? open = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(aiDataRoot);
        if (limits.MaxRows < 1 || limits.MaxCharacters is < 1 or > 256 * 1024 * 1024 || limits.MaxLineCharacters < 1)
            throw new ArgumentOutOfRangeException(nameof(limits));
        _root = Path.GetFullPath(aiDataRoot);
        _limits = limits;
        _open = open ?? (path => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
    }

    public RaceReport Build(int days = 1, IReadOnlyList<RaceHistoryItem>? historyFallback = null,
        DateTimeOffset? nowOverride = null, CancellationToken cancellationToken = default)
    {
        if (days is < 1 or > 90) throw new ArgumentOutOfRangeException(nameof(days), "Choose between 1 and 90 days.");
        var now = (nowOverride ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var from = now.AddDays(-days);
        var state = new ReadState(_limits);
        var events = new List<RaceOutcomeEvent>();
        // Recorder file names use the local calendar; timestamps inside rows use UTC.
        var firstDay = from.LocalDateTime.Date;
        var lastDay = now.LocalDateTime.Date;
        var daysPresent = 0;
        var expectedDays = (lastDay - firstDay).Days + 1;
        for (var day = firstDay; day <= lastDay; day = day.AddDays(1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stem = Path.Combine(_root, $"races-{day:yyyyMMdd}.jsonl");
            var paths = new[] { stem, stem + ".gz" }.Where(File.Exists).ToArray();
            if (paths.Length > 0) daysPresent++;
            foreach (var path in paths)
            {
                if (state.Truncated) break;
                try
                {
                    using var raw = _open(path);
                    using var stream = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
                        ? new GZipStream(raw, CompressionMode.Decompress, leaveOpen: true) : raw;
                    using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
                        bufferSize: 4096, leaveOpen: true);
                    state.FilesRead++;
                    foreach (var line in ReadLines(reader, state, cancellationToken))
                    {
                        try
                        {
                            using var document = JsonDocument.Parse(line);
                            var root = document.RootElement;
                            // TelemetryEnvelope initializes Ts to now. A malformed row
                            // without a stored timestamp must not become today's race.
                            if (root.ValueKind != JsonValueKind.Object ||
                                !(root.TryGetProperty("endedAt", out var timestamp) && timestamp.ValueKind == JsonValueKind.String &&
                                      RaceReportBuilder.TryTimestamp(timestamp.GetString(), out _) ||
                                  root.TryGetProperty("ts", out timestamp) && timestamp.ValueKind == JsonValueKind.String &&
                                      RaceReportBuilder.TryTimestamp(timestamp.GetString(), out _)))
                            { state.ParseSkips++; continue; }
                            var race = root.Deserialize<RaceOutcomeEvent>();
                            if (race == null) state.ParseSkips++;
                            else events.Add(race);
                        }
                        catch (JsonException) { state.ParseSkips++; }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    state.ReadErrors++;
                }
            }
        }
        var coverage = new RaceReportCoverage
        {
            FilesRead = state.FilesRead, FileDaysExpected = expectedDays, FileDaysPresent = daysPresent,
            ParseSkips = state.ParseSkips, ReadErrors = state.ReadErrors, Truncated = state.Truncated,
            IsPartial = state.ParseSkips > 0 || state.ReadErrors > 0 || state.Truncated || daysPresent < expectedDays
        };
        var report = RaceReportBuilder.Build(events, from, now, coverage);
        if (report.Summary.RaceAttempts == 0 && historyFallback is { Count: > 0 })
        {
            // No mixing: otherwise the same race in telemetry and history would count twice.
            // The retained history has no reliable per-site attribution for old byte totals.
            report = RaceReportBuilder.BuildHistory(historyFallback.Take(500).ToArray(), from, now, report.Coverage with
            {
                Source = "history-fallback", IsPartial = true,
                Truncated = report.Coverage.Truncated || historyFallback.Count > 500,
                Note = "Fallback to retained race history (at most 500 rows); this cannot establish full period coverage. Historical bytes without a site are reported separately. Unknown winners are excluded from win rates."
            });
        }
        return report;
    }

    private static IEnumerable<string> ReadLines(StreamReader reader, ReadState state, CancellationToken ct)
    {
        var buffer = new char[4096];
        var line = new StringBuilder();
        var oversized = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var read = reader.Read(buffer, 0, Math.Min(buffer.Length, state.RemainingCharacters + 1));
            if (read == 0) break;
            for (var i = 0; i < read; i++)
            {
                if (--state.RemainingCharacters < 0) { state.Truncated = true; yield break; }
                var character = buffer[i];
                if (character == '\n')
                {
                    if (oversized || line.Length > 0)
                    {
                        if (state.Rows++ >= state.Limits.MaxRows) { state.Truncated = true; yield break; }
                        if (oversized) state.ParseSkips++;
                        else if (!string.IsNullOrWhiteSpace(line.ToString())) yield return line.ToString();
                    }
                    oversized = false;
                    line.Clear();
                }
                else if (!oversized)
                {
                    if (line.Length >= state.Limits.MaxLineCharacters) { oversized = true; line.Clear(); }
                    else line.Append(character);
                }
            }
        }
        if (oversized || line.Length > 0)
        {
            if (state.Rows++ >= state.Limits.MaxRows) { state.Truncated = true; yield break; }
            if (oversized) state.ParseSkips++;
            else if (!string.IsNullOrWhiteSpace(line.ToString())) yield return line.ToString();
        }
    }

    private sealed class ReadState(RaceReportReadLimits limits)
    {
        public RaceReportReadLimits Limits { get; } = limits;
        public int RemainingCharacters { get; set; } = limits.MaxCharacters;
        public int Rows { get; set; }
        public int FilesRead { get; set; }
        public int ParseSkips { get; set; }
        public int ReadErrors { get; set; }
        public bool Truncated { get; set; }
    }
}

internal sealed record RaceReportReadLimits(int MaxRows = 100000, int MaxCharacters = 64 * 1024 * 1024,
    int MaxLineCharacters = TelemetryRecorder.MaxEventBytes);
