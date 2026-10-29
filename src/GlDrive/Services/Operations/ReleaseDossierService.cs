using System.IO;
using System.Text.Json;
using GlDrive.Config;
using GlDrive.Services.Statistics;

namespace GlDrive.Services.Operations;

/// <summary>A bounded local evidence join. No operational action, transport, matcher or config writer is accepted.</summary>
public sealed class ReleaseDossierService
{
    private readonly string _root;
    private readonly Func<AppConfig?> _config;
    private readonly Func<string, DossierLocalSnapshot> _local;
    private readonly Func<Func<string, string>> _redactor;
    private readonly Func<string, IReadOnlyList<OpsEvent>> _journal;
    private readonly Func<DateTimeOffset> _now;
    private readonly SemaphoreSlim _reads = new(1, 1);
    private readonly TelemetryReadLimits _limits;
    internal const int MaxEvents = 500;

    internal ReleaseDossierService(string appDataRoot, Func<AppConfig?> config,
        Func<string, DossierLocalSnapshot>? local = null, Func<string, IReadOnlyList<OpsEvent>>? journal = null,
        Func<Func<string, string>>? redactor = null, Func<DateTimeOffset>? now = null, TelemetryReadLimits? limits = null)
    {
        _root = Path.Combine(Path.GetFullPath(appDataRoot), "ai-data"); _config = config;
        _local = local ?? (_ => new([], [], true)); _journal = journal ?? (_ => []);
        _redactor = redactor ?? (() => value => value); _now = now ?? (() => DateTimeOffset.UtcNow);
        _limits = limits ?? new();
    }

    public async Task<ReleaseDossier> ReadAsync(string release, string? section = null, int days = 30,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(release) || release.Length > 1024 || release.Any(char.IsControl))
            throw new ArgumentException("Supply a release name of 1 to 1024 characters", nameof(release));
        if (section?.Length > 256 || section?.Any(char.IsControl) == true) throw new ArgumentException("Section is too long or contains control characters", nameof(section));
        if (days is < 1 or > 90) throw new ArgumentOutOfRangeException(nameof(days));
        await _reads.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await Task.Run(() => Build(release, section, days, cancellationToken), cancellationToken).ConfigureAwait(false); }
        finally { _reads.Release(); }
    }

    private ReleaseDossier Build(string release, string? section, int days, CancellationToken ct)
    {
        var to = _now().ToUniversalTime(); var from = to.AddDays(-days);
        var reader = new TelemetryReportReader(_root, _limits, 6);
        var sources = new Dictionary<string, PriorityQueue<DossierEvent, DateTimeOffset>>();
        var source = "";
        var raceIds = new HashSet<string>(StringComparer.Ordinal);
        Func<string, string> redact;
        try { redact = _redactor(); } catch { redact = _ => "[redaction unavailable]"; }
        string Safe(string? value, int maximum = 512)
        {
            ct.ThrowIfCancellationRequested();
            try { var safe = redact(value is { Length: <= 4096 } ? value : "[oversized detail omitted]"); return safe.Length <= maximum ? safe : "[oversized redacted detail omitted]"; }
            catch { return "[redaction unavailable]"; }
        }
        bool Same(JsonElement row, string field = "release") => string.Equals(TelemetryValue.Text(row, field, 1024), release, StringComparison.OrdinalIgnoreCase);
        void Add(DateTimeOffset time, string stage, string? server, string detail)
        {
            ct.ThrowIfCancellationRequested();
            if (time < from || time > to) return;
            if (!sources.TryGetValue(source, out var events)) sources[source] = events = new();
            events.Enqueue(new(time, stage, Safe(server ?? "", 128), Safe(detail)), time);
            if (events.Count > MaxEvents) { events.Dequeue(); reader.MarkPartial("dossier-result-limit"); }
        }
        void Observe(string prefix, Action<JsonElement> action)
        {
            source = prefix;
            reader.ReadStream(prefix, from, to, action, ct, prefix == "transfers" ? null : release);
        }
        Observe("races", row =>
        {
            if (!Same(row)) return;
            var id = TelemetryValue.Text(row, "raceId"); if (id != null) raceIds.Add(id);
            var wait = TelemetryValue.Number(row, "queueWaitSeconds");
            Add(TelemetryValue.Timestamp(row, "ts")!.Value, "race-outcome", "",
                $"{TelemetryValue.Text(row, "result") ?? "unknown"}; stop {TelemetryValue.Text(row, "stopReason") ?? "unknown"}; " +
                $"queue wait {(wait >= 0 ? wait.Value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " s" : "unknown")}; race {id ?? "unknown"}");
            if (raceIds.Count > MaxEvents) { raceIds.Remove(id!); reader.MarkPartial("race-join-limit"); }
            if (TelemetryValue.TryProperty(row, "destinationOutcomes", out var destinations) && destinations.ValueKind == JsonValueKind.Array)
                foreach (var destination in destinations.EnumerateArray().Take(64))
                    Add(TelemetryValue.Timestamp(row, "ts")!.Value, "race-destination", TelemetryValue.Text(destination, "serverId"),
                        "Recorded destination outcome: " + (TelemetryValue.Text(destination, "outcome") ?? "unknown"));
        });
        Observe("downloads", row =>
        {
            if (!PathContainsRelease(TelemetryValue.Text(row, "remotePath", 4096), release)) return;
            Add(TelemetryValue.Timestamp(row, "ts")!.Value, "download-outcome", TelemetryValue.Text(row, "serverId"),
                $"{TelemetryValue.Text(row, "result") ?? "unknown"}; {TelemetryValue.Integer(row, "bytes")?.ToString() ?? "unknown"} bytes; {TelemetryValue.Text(row, "failureClass") ?? "no failure class"}.");
        });
        Observe("nukes", row =>
        {
            if (!Same(row)) return;
            var approximate = !TelemetryValue.TryProperty(row, "releaseTruncated", out var truncated) || truncated.ValueKind != JsonValueKind.False;
            Add(TelemetryValue.Timestamp(row, "ts")!.Value, "nuke-observation", TelemetryValue.Text(row, "serverId"),
                (approximate ? "Approximate reported name; not exact nuke authority. " : "Exact reported name. ") + (TelemetryValue.Text(row, "reason", 2048) ?? "Reason unavailable."));
        });
        Observe("transfers", row =>
        {
            var id = TelemetryValue.Text(row, "raceId");
            if (!(id != null && raceIds.Contains(id)) && !PathContainsRelease(TelemetryValue.Text(row, "file", 4096), release)) return;
            Add(TelemetryValue.Timestamp(row, "ts")!.Value, "transfer-outcome", TelemetryValue.Text(row, "dstServer"),
                $"From {TelemetryValue.Text(row, "srcServer") ?? "unknown"}; {TelemetryValue.Text(row, "abortReason") ?? "no abort recorded"}; bytes are a telemetry estimate.");
        });
        Observe("matched-announces", row =>
        {
            if (!Same(row)) return;
            Add(TelemetryValue.Timestamp(row, "ts")!.Value, "announce-rule-match", TelemetryValue.Text(row, "serverId"),
                $"Section {TelemetryValue.Text(row, "section") ?? "unknown"}; rule {TelemetryValue.Text(row, "ruleSource") ?? "unknown"}.");
        });
        Observe("wishlist-attempts", row =>
        {
            if (!Same(row)) return;
            var matched = TelemetryValue.TryProperty(row, "matched", out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean().ToString() : "unknown";
            Add(TelemetryValue.Timestamp(row, "ts")!.Value, "wishlist-verdict", TelemetryValue.Text(row, "serverId"),
                $"Item {TelemetryValue.Text(row, "wishlistItemId") ?? "unknown"}; matched {matched}; {TelemetryValue.Text(row, "missReason") ?? "no rejection recorded"}.");
        });
        DossierLocalSnapshot local;
        try { local = _local(release); } catch { local = new([], [], true); }
        if (local.Partial) reader.MarkPartial("local-store-incomplete");
        source = "local";
        foreach (var item in local.Events.Take(1000))
            Add(item.Time, item.Stage, item.ServerId, item.Detail);
        try
        {
            var journal = _journal(release);
            source = "journal";
            if (journal is IOpsReadCoverage { Partial: true }) reader.MarkPartial("ops-journal-incomplete");
            foreach (var item in journal.Take(MaxEvents))
            {
                Add(item.Time, item.Kind, item.ServerId, item.Detail);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { reader.MarkPartial("ops-journal-unavailable"); }
        AppConfig? config;
        try { config = _config(); } catch { config = null; }
        var predictions = ReleasePrediction.Evaluate(config, local.Wishlist, local.WishlistPartial, release, section, reader.MarkPartial, ct)
            .Select(p => new DossierPrediction(Safe(p.ServerId, 128), p.Stage, p.Verdict, Safe(p.Detail))).ToArray();
        // Give every evidence source a turn before taking another row from a noisy
        // stream. Each source retains its newest rows and cannot evict another source.
        var buckets = sources.Values.Select(queue => new Queue<DossierEvent>(queue.UnorderedItems
            .Select(row => row.Element).OrderByDescending(row => row.Time))).ToArray();
        var timeline = new List<DossierEvent>();
        while (timeline.Count < MaxEvents && buckets.Any(bucket => bucket.Count != 0))
            foreach (var bucket in buckets)
                if (timeline.Count < MaxEvents && bucket.TryDequeue(out var row)) timeline.Add(row);
        if (buckets.Any(bucket => bucket.Count != 0)) reader.MarkPartial("dossier-result-limit");
        return new(from, to, Safe(release, 1024), timeline.OrderBy(e => e.Time).ToArray(), predictions, reader.Coverage);
    }

    internal static bool PathContainsRelease(string? path, string release) => path is { Length: <= 4096 } &&
        path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Contains(release, StringComparer.OrdinalIgnoreCase);
}
