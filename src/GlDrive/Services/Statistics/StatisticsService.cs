using System.Globalization;
using System.IO;
using System.Text.Json;
using GlDrive.Spread;

namespace GlDrive.Services.Statistics;

/// <summary>Explicit local reports only. No pool, network, scheduler or configuration writer is reachable here.</summary>
public sealed class StatisticsService
{
    private readonly string _root;
    private readonly Func<IReadOnlyList<RaceHistoryItem>> _history;
    private readonly Func<Func<string, string>> _redactor;
    private readonly Func<DateTimeOffset> _now;
    private readonly TelemetryReadLimits _limits;
    private readonly SemaphoreSlim _readGate = new(1, 1);
    internal const int MaxJoinRows = 10_000;
    internal const int MaxTrafficKeys = 4096;
    internal const int MaxRoutes = 256;

    public StatisticsService(string appDataRoot, Func<IReadOnlyList<RaceHistoryItem>>? history = null,
        Func<Func<string, string>>? redactor = null)
        : this(appDataRoot, history, redactor, () => DateTimeOffset.UtcNow, new()) { }

    internal StatisticsService(string appDataRoot, Func<IReadOnlyList<RaceHistoryItem>>? history,
        Func<Func<string, string>>? redactor, Func<DateTimeOffset> now, TelemetryReadLimits limits)
    {
        _root = Path.Combine(Path.GetFullPath(appDataRoot), "ai-data");
        _history = history ?? (() => []);
        _redactor = redactor ?? (() => value => value);
        _now = now;
        _limits = limits;
    }

    public async Task<StatisticsSummary> ReadAsync(int days, CancellationToken cancellationToken = default)
    {
        if (days is < 1 or > 90) throw new ArgumentOutOfRangeException(nameof(days), "Choose 1 to 90 days");
        await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await Task.Run(() => Build(days, cancellationToken), cancellationToken).ConfigureAwait(false); }
        finally { _readGate.Release(); }
    }

    private StatisticsSummary Build(int days, CancellationToken ct)
    {
        var to = _now().ToUniversalTime();
        var from = to.AddDays(-days);
        var reader = new TelemetryReportReader(_root, _limits, 5);
        Func<string, string> redact;
        try { redact = _redactor(); } catch { redact = _ => "[redaction unavailable]"; }
        var races = new Dictionary<string, RaceRow>(StringComparer.Ordinal);
        var downloads = new Dictionary<string, DownloadRow>(StringComparer.Ordinal);
        var traffic = new Dictionary<(string Day, string Server), TrafficAccumulator>();
        var routes = new Dictionary<(string Source, string Destination), RouteAccumulator>();
        var health = new Dictionary<string, HealthAccumulator>(StringComparer.Ordinal);
        var healthWindowCount = 0;

        reader.ReadStream("downloads", from, to, row =>
        {
            var result = TelemetryValue.Text(row, "result");
            if (result is not ("complete" or "failed")) return;
            var id = TelemetryValue.Text(row, "downloadId");
            var server = TelemetryValue.Text(row, "serverId");
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(server)) { reader.MarkPartial("invalid-download-id"); return; }
            var key = server + "\0" + id;
            var ts = TelemetryValue.Timestamp(row, "ts")!.Value;
            if (downloads.TryGetValue(key, out var previous) && previous.Timestamp >= ts) return;
            if (!downloads.ContainsKey(key) && downloads.Count >= MaxJoinRows) { reader.MarkPartial("download-join-limit"); return; }
            var bytes = TelemetryValue.Integer(row, "bytes");
            if (result == "complete" && bytes is not >= 0) reader.MarkPartial("unknown-download-bytes");
            downloads[key] = new(ts, server, result == "complete", bytes is >= 0 ? bytes.Value : 0);
        }, ct);

        reader.ReadStream("site-health", from, to, row =>
        {
            var server = TelemetryValue.Text(row, "serverId");
            if (string.IsNullOrEmpty(server)) return;
            if (!health.TryGetValue(server, out var site))
            {
                if (health.Count >= MaxRoutes) { reader.MarkPartial("health-site-limit"); return; }
                health[server] = site = new(Label(server));
            }
            site.Samples++;
            var ts = TelemetryValue.Timestamp(row, "ts")!.Value;
            if (site.Last == null || site.Last < ts) site.Last = ts;
            var start = TelemetryValue.Timestamp(row, "windowStart");
            var end = TelemetryValue.Timestamp(row, "windowEnd");
            if (start == null || end == null || start > end) return;
            if (healthWindowCount >= MaxJoinRows) { reader.MarkPartial("health-window-limit"); return; }
            site.Windows.Add((start.Value < from ? from : start.Value, end.Value > to ? to : end.Value));
            healthWindowCount++;
        }, ct);

        reader.ReadStream("races", from, to, row =>
        {
            var id = TelemetryValue.Text(row, "raceId");
            if (string.IsNullOrEmpty(id)) { reader.MarkPartial("invalid-race-id"); return; }
            var ts = TelemetryValue.Timestamp(row, "ts")!.Value;
            if (races.TryGetValue(id, out var previous) && previous.Timestamp >= ts) return;
            if (!races.ContainsKey(id) && races.Count >= MaxJoinRows) { reader.MarkPartial("race-join-limit"); return; }
            var wait = TelemetryValue.Number(row, "queueWaitSeconds");
            races[id] = new(ts, TelemetryValue.Timestamp(row, "startedAt"), TelemetryValue.Text(row, "release"),
                TelemetryValue.Text(row, "section"), TelemetryValue.Text(row, "result"), wait >= 0 ? wait : null);
        }, ct);

        // Index only races already in this bounded report. Unrelated announce text is never retained.
        var releases = new Dictionary<(string Release, string Section), List<RaceRow>>();
        foreach (var race in races.Values)
        {
            ct.ThrowIfCancellationRequested();
            if (race.Release == null || race.Section == null || race.Started == null) continue;
            var key = (race.Release.ToUpperInvariant(), race.Section.ToUpperInvariant());
            if (!releases.TryGetValue(key, out var list)) releases[key] = list = [];
            if (list.Count < 32) list.Add(race); else reader.MarkPartial("release-join-limit");
        }
        reader.ReadStream("transfers", from, to, row =>
        {
            if (TelemetryValue.TryProperty(row, "duplicateSkipped", out var duplicate) && duplicate.ValueKind != JsonValueKind.False)
            {
                if (duplicate.ValueKind != JsonValueKind.True && duplicate.ValueKind != JsonValueKind.Null)
                    reader.MarkPartial("invalid-transfer-duplicate");
                if (duplicate.ValueKind != JsonValueKind.Null) return;
            }
            // Legacy failures can carry the entire EXPECTED file size. Only an absent/null/empty
            // abort reason proves a completed telemetry row; malformed optional data is unknown.
            if (TelemetryValue.TryProperty(row, "abortReason", out var abort) &&
                abort.ValueKind != JsonValueKind.Null && !(abort.ValueKind == JsonValueKind.String && abort.GetString() == ""))
            {
                if (abort.ValueKind != JsonValueKind.String) reader.MarkPartial("invalid-transfer-outcome");
                return;
            }
            var bytes = TelemetryValue.Integer(row, "bytes");
            if (bytes is not >= 0) { reader.MarkPartial("invalid-transfer-bytes"); return; }
            var source = TelemetryValue.Text(row, "srcServer");
            var destination = TelemetryValue.Text(row, "dstServer");
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(destination)) { reader.MarkPartial("invalid-transfer-route"); return; }
            var day = TelemetryValue.Timestamp(row, "ts")!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var mode = TelemetryValue.Text(row, "transferMode");
            var relay = mode == "Relay";
            var unknown = mode is not ("Relay" or "PasvPasv" or "PasvCpsv" or "CpsvPasv");
            var sent = Traffic(day, source); var received = Traffic(day, destination);
            if (sent != null)
            {
                sent.Sent = Add(sent.Sent, bytes.Value);
                if (relay) sent.RelaySent = Add(sent.RelaySent, bytes.Value);
                if (unknown) sent.UnknownSent = Add(sent.UnknownSent, bytes.Value);
            }
            if (received != null)
            {
                received.Received = Add(received.Received, bytes.Value);
                if (relay) received.RelayReceived = Add(received.RelayReceived, bytes.Value);
                if (unknown) received.UnknownReceived = Add(received.UnknownReceived, bytes.Value);
            }
            var key = (source, destination);
            if (!routes.TryGetValue(key, out var route))
            {
                if (routes.Count >= MaxRoutes) { reader.MarkPartial("route-limit"); return; }
                routes[key] = route = new(Label(source), Label(destination));
            }
            route.Count++; route.Bytes = Add(route.Bytes, bytes.Value);
            if (TelemetryValue.Integer(row, "elapsedMs") is > 0 and var milliseconds)
            { route.Timed++; route.TimedBytes += bytes.Value; route.Milliseconds += milliseconds; }
        }, ct);

        reader.ReadStream("matched-announces", from, to, row =>
        {
            var release = TelemetryValue.Text(row, "release");
            var section = TelemetryValue.Text(row, "section");
            if (release == null || section == null || !releases.TryGetValue((release.ToUpperInvariant(), section.ToUpperInvariant()), out var list)) return;
            var ts = TelemetryValue.Timestamp(row, "ts")!.Value;
            foreach (var race in list)
                if (ts <= race.Started && (race.Announced == null || ts > race.Announced)) race.Announced = ts;
        }, ct);

        var completeDownloads = downloads.Values.Count(d => d.Complete);
        foreach (var download in downloads.Values)
        {
            ct.ThrowIfCancellationRequested();
            if (download.Complete && Traffic(download.Timestamp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), download.Server) is { } bucket)
            {
                bucket.Downloads = Add(bucket.Downloads, download.Bytes);
                bucket.Sent = Add(bucket.Sent, download.Bytes);
            }
        }
        var cleanSamples = 0; var clean = 0;
        try
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var history = _history();
            if (history.Count > MaxJoinRows) reader.MarkPartial("history-limit");
            foreach (var item in history.Take(MaxJoinRows))
            {
                ct.ThrowIfCancellationRequested();
                var ended = new DateTimeOffset(DateTime.SpecifyKind(item.CompletedAt, DateTimeKind.Utc));
                if (ended < from || ended > to || item.Result is not (SpreadJobState.Completed or SpreadJobState.Failed or SpreadJobState.Stopped)
                    || string.IsNullOrEmpty(item.Id) || !seen.Add(item.Id)) continue;
                // Older history without inventory fields defaults CleanComplete to false.
                // An unmeasured completion is unknown, not evidence of an unclean race.
                if (!item.CleanComplete && item.Result == SpreadJobState.Completed && item.FilesTotal <= 0) continue;
                cleanSamples++;
                if (item.CleanComplete) clean++;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { reader.MarkPartial("history-unavailable"); }

        var waits = races.Values.Where(r => r.Wait != null).Select(r => r.Wait!.Value).Order().ToArray();
        var lags = races.Values.Where(r => r.Announced != null && r.Started != null)
            .Select(r => (r.Started!.Value - r.Announced!.Value).TotalSeconds).Order().ToArray();
        var healthRows = new List<SiteObservationStatistics>();
        foreach (var site in health.Values)
        {
            ct.ThrowIfCancellationRequested();
            var seconds = UnionSeconds(site.Windows);
            healthRows.Add(new(site.Label, site.Samples, seconds / 3600,
                seconds / (to - from).TotalSeconds * 100, null, site.Last));
        }
        ct.ThrowIfCancellationRequested();
        var downloadPartial = reader.Coverage.Streams.Any(stream => stream.Stream == "downloads" && stream.Partial);
        return new(from, to, reader.Coverage,
            new(races.Count, races.Values.Count(r => r.Result == "complete"), cleanSamples, clean,
                cleanSamples == 0 ? null : (double)clean / cleanSamples,
                waits.Length, Percentile(waits, .5), Percentile(waits, .95),
                lags.Length, Percentile(lags, .5), Percentile(lags, .95)),
            new(completeDownloads, downloads.Count - completeDownloads,
                downloadPartial || downloads.Count == 0 ? null : (double)completeDownloads / downloads.Count) { Partial = downloadPartial },
            traffic.Values.Select(t => new DailyTraffic(t.Day, t.Label, t.Sent, t.Received, t.Downloads,
                t.RelaySent, t.RelayReceived, t.UnknownSent, t.UnknownReceived)).OrderByDescending(t => t.DayUtc).ThenBy(t => t.ServerId, StringComparer.Ordinal).ToArray(),
            routes.Values.Select(r => new RouteStatistics(r.Source, r.Destination, r.Count, r.Bytes, r.Timed,
                r.Milliseconds > 0 ? r.TimedBytes / r.Milliseconds * 1000 / (1024 * 1024) : null))
                .OrderBy(r => r.Source, StringComparer.Ordinal).ThenBy(r => r.Destination, StringComparer.Ordinal).ToArray(),
            healthRows.OrderBy(h => h.ServerId, StringComparer.Ordinal).ToArray());

        TrafficAccumulator? Traffic(string day, string server)
        {
            var key = (day, server);
            if (traffic.TryGetValue(key, out var found)) return found;
            if (traffic.Count >= MaxTrafficKeys) { reader.MarkPartial("traffic-key-limit"); return null; }
            return traffic[key] = new(day, Label(server));
        }
        string Label(string value)
        {
            string safe;
            try { safe = redact(value); } catch { return "[redaction unavailable]"; }
            // Bound only after full redaction so a clipped credential is never emitted.
            return safe.Length <= 512 ? safe : safe[..512];
        }
        long Add(long a, long b)
        {
            if (a <= long.MaxValue - b) return a + b;
            reader.MarkPartial("numeric-overflow"); return long.MaxValue;
        }
    }

    internal static double? Percentile(double[] sorted, double fraction)
    {
        if (sorted.Length == 0) return null;
        var position = (sorted.Length - 1) * fraction;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }
    private static double UnionSeconds(List<(DateTimeOffset Start, DateTimeOffset End)> windows)
    {
        DateTimeOffset? start = null, end = null;
        double total = 0;
        foreach (var window in windows.Where(w => w.End >= w.Start).OrderBy(w => w.Start))
        {
            if (start == null) { start = window.Start; end = window.End; }
            else if (window.Start <= end) { if (window.End > end) end = window.End; }
            else { total += (end!.Value - start.Value).TotalSeconds; start = window.Start; end = window.End; }
        }
        return total + (start == null ? 0 : (end!.Value - start.Value).TotalSeconds);
    }
    private sealed class RaceRow(DateTimeOffset timestamp, DateTimeOffset? started, string? release, string? section, string? result, double? wait)
    {
        public DateTimeOffset Timestamp = timestamp;
        public DateTimeOffset? Started = started, Announced;
        public string? Release = release, Section = section, Result = result;
        public double? Wait = wait;
    }
    private sealed record DownloadRow(DateTimeOffset Timestamp, string Server, bool Complete, long Bytes);
    private sealed class TrafficAccumulator(string day, string label)
    {
        public string Day = day, Label = label;
        public long Sent, Received, Downloads, RelaySent, RelayReceived, UnknownSent, UnknownReceived;
    }
    private sealed class RouteAccumulator(string source, string destination)
    {
        public string Source = source, Destination = destination;
        public int Count, Timed;
        public long Bytes;
        public double TimedBytes, Milliseconds;
    }
    private sealed class HealthAccumulator(string label)
    {
        public string Label = label;
        public int Samples;
        public DateTimeOffset? Last;
        public List<(DateTimeOffset Start, DateTimeOffset End)> Windows = [];
    }
}
