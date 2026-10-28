using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text.Json.Nodes;
using GlDrive.Config;

namespace GlDrive.AiAgent;

[JsonConverter(typeof(ExpectedEffectConverter))]
public sealed class ExpectedEffect
{
    [JsonPropertyName("metric")] public string Metric { get; set; } = "";
    [JsonPropertyName("direction")] public string Direction { get; set; } = "";
    [JsonPropertyName("windowHours")] public int WindowHours { get; set; } = 24;
}

// This optional observation must never invalidate the model's actionable response.
public sealed class ExpectedEffectConverter : JsonConverter<ExpectedEffect>
{
    public override ExpectedEffect? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement;
        if (value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("metric", out var metric) || metric.ValueKind != JsonValueKind.String
            || !value.TryGetProperty("direction", out var direction) || direction.ValueKind != JsonValueKind.String
            || !value.TryGetProperty("windowHours", out var hours) || hours.ValueKind != JsonValueKind.Number
            || !hours.TryGetInt32(out var window)) return null;
        var effect = new ExpectedEffect { Metric = metric.GetString()!, Direction = direction.GetString()!, WindowHours = window };
        return OutcomeEvaluator.Valid(effect) ? effect : null;
    }

    public override void Write(Utf8JsonWriter writer, ExpectedEffect value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("metric", value.Metric);
        writer.WriteString("direction", value.Direction);
        writer.WriteNumber("windowHours", value.WindowHours);
        writer.WriteEndObject();
    }
}

public sealed record AgentOutcome(string Status, double? Before, double? After,
    int BaselineSamples, int ObservedSamples, DateTime EvaluatedAtUtc, string Reason);

public sealed record AgentOutcomeFeedback(string Id, string RunId, string Category, string Target,
    string Metric, string Direction, int WindowHours, AgentOutcome Outcome);

/// <summary>Observational, local measurements. An absent sample is never a measured failure.</summary>
public static class OutcomeEvaluator
{
    public static bool Valid(ExpectedEffect? effect) => effect is
        { WindowHours: >= 1 and <= 168, Direction: "increase" or "decrease",
          Metric: "raceCompletionRate" or "deliveredRacesPerHour" or "queueWaitSeconds" };

    public static AgentOutcome Score(ExpectedEffect? effect, IReadOnlyList<RaceOutcomeEvent> baseline,
        IReadOnlyList<RaceOutcomeEvent> observed, bool completeEvidence, DateTime nowUtc,
        int minimumSamples = 10, double tolerancePercent = 10)
    {
        if (!Valid(effect)) return new("unscored", null, null, 0, 0, nowUtc, "missing-or-invalid-expected-effect");
        var before = Measure(effect!, baseline);
        var after = Measure(effect!, observed);
        if (!double.IsFinite(before.Value) || !double.IsFinite(after.Value))
            return new("unscored", null, null, before.Samples, after.Samples, nowUtc, "nonfinite-measurement");
        if (!completeEvidence || before.Samples < Math.Max(1, minimumSamples) || after.Samples < Math.Max(1, minimumSamples))
            return new("unscored", before.Value, after.Value, before.Samples, after.Samples, nowUtc,
                completeEvidence ? "insufficient-samples" : "incomplete-evidence");
        var delta = after.Value - before.Value;
        var tolerance = Math.Abs(before.Value) * Math.Clamp(tolerancePercent, 0, 100) / 100;
        var directed = effect!.Direction == "increase" ? delta : -delta;
        return new(Math.Abs(delta) <= tolerance ? "no-effect" : directed > 0 ? "effective" : "regressed",
            before.Value, after.Value, before.Samples, after.Samples, nowUtc, "observed-change-not-causal-proof");
    }

    private static (double Value, int Samples) Measure(ExpectedEffect effect, IReadOnlyList<RaceOutcomeEvent> rows)
    {
        if (effect.Metric == "queueWaitSeconds")
        {
            var measured = rows.Where(r => r.QueueWaitSeconds is >= 0 && double.IsFinite(r.QueueWaitSeconds.Value))
                .Select(r => r.QueueWaitSeconds!.Value).ToArray();
            return (measured.Length == 0 ? 0 : measured.Average(), measured.Length);
        }
        // Existing files at a destination are not payload delivered by this race.
        var delivered = rows.Where(r => r.Participants.Any(p => p.Role == "dst" && p.Bytes > 0)).ToArray();
        if (effect.Metric == "deliveredRacesPerHour")
            return ((double)delivered.Length / effect.WindowHours, rows.Count);
        return (delivered.Length == 0 ? 0 : (double)delivered.Count(r => r.Result == "complete") / delivered.Length,
            delivered.Length);
    }

    internal static bool TryUtc(string? text, out DateTime utc)
    {
        var ok = DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at);
        utc = at.UtcDateTime;
        return ok;
    }
}

public sealed record NegativeCorrection(string Target, string Reason, string? AuditId);

/// <summary>Read-only feedback. It does not freeze, mutate, or reject a future proposal.</summary>
public static class CorrectionDetector
{
    public static bool IsHumanActor(string? actor) => actor is
        "settings-ui" or "server-editor" or "tray" or "ai-setup" or "import" or "restore:history"
        or "restore:settings-ui" or "restore:snapshot"
        or "mcp:config_patch" or "settings-ui:agent-undo"
        || actor?.StartsWith("experiment-revert:", StringComparison.Ordinal) == true;

    public static IReadOnlyList<NegativeCorrection> Detect(AppConfig config, IEnumerable<AuditRow> audit,
        IEnumerable<ConfigOverrideEvent> overrides, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var rows = audit.ToArray();
        var result = new Dictionary<string, NegativeCorrection>(StringComparer.Ordinal);
        foreach (var row in rows.Where(r => r.Undone && r.UndoneReason is
                     "user-click" or "user-dismiss" or "mcp-undo" or "user-freeze"))
        {
            var target = ChangeApplier.CanonicalizeTarget(row.Target, config);
            result[target] = new(target, row.WasApplied ? "manual-undo" : "manual-dismissal", row.Id);
        }
        foreach (var change in overrides.Where(e => IsHumanActor(e.Actor)))
        {
            if (!OutcomeEvaluator.TryUtc(change.Ts, out var at) || at > now) continue;
            // Old indexed events cannot identify which server occupied that position then.
            if (change.CanonicalPointer == null && IndexedServerPointer(change.JsonPointer)) continue;
            var target = ChangeApplier.CanonicalizeTarget(change.CanonicalPointer ?? change.JsonPointer, config);
            var prior = rows.LastOrDefault(r => r.WasApplied && OutcomeEvaluator.TryUtc(r.Ts, out var applied)
                && applied <= at && AppliedValueMatches(r, target, change.BeforeValue, config));
            if (prior != null && change.BeforeValue != change.AfterValue)
                result[target] = new(target, "manual-edit-after-agent", prior.Id);
        }
        return result.Values.ToArray();
    }

    private static bool IndexedServerPointer(string pointer) => pointer.StartsWith("/servers/", StringComparison.Ordinal)
        && int.TryParse(pointer.Split('/').ElementAtOrDefault(2), out _);

    private static bool AppliedValueMatches(AuditRow row, string target, string? before, AppConfig config)
    {
        if (IndexedServerPointer(row.Target)) return false;
        var appliedTarget = ChangeApplier.CanonicalizeTarget(row.Target, config);
        if (!JsonPointer.IsAncestorOrSelf(appliedTarget, target)) return false;
        try
        {
            var value = JsonSerializer.SerializeToNode(row.After);
            var physicalTarget = ChangeApplier.ConfigTarget(target, config);
            var physicalApplied = ChangeApplier.ConfigTarget(appliedTarget, config);
            var suffix = physicalTarget[physicalApplied.Length..];
            if (suffix.Length > 0) value = JsonPointer.Resolve(value!, suffix);
            return JsonNode.DeepEquals(value, before == null ? null : JsonNode.Parse(before));
        }
        catch { return false; }
    }

    internal static string? CanonicalOverridePointer(string pointer, JsonNode before, JsonNode after)
    {
        if (!IndexedServerPointer(pointer)) return pointer;
        var parts = pointer.Split('/');
        var index = int.Parse(parts[2], CultureInfo.InvariantCulture);
        if (index < 0 || before["servers"] is not JsonArray old || after["servers"] is not JsonArray current
            || index >= old.Count || index >= current.Count) return null;
        var oldId = old[index]?["id"]?.GetValue<string>();
        var id = current[index]?["id"]?.GetValue<string>();
        if (string.IsNullOrEmpty(id) || id != oldId) return null;
        parts[2] = id;
        return ChangeApplier.CanonicalizeTarget(string.Join('/', parts), new AppConfig());
    }
}
