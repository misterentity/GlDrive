using System.Text.Json;

namespace GlDrive.Logging;

internal sealed record LogSignatureRule(string Id, string Kind, string Match, string Then, int Threshold, int WindowSeconds, bool RequireCorrelation = false);
internal sealed record LogSignatureHit(string Id, string Correlation, DateTimeOffset Time);

/// <summary>Bounded, literal template/message rules. No regex, site actions or inferred outages.</summary>
internal sealed class LogSignatureRules(IEnumerable<LogSignatureRule> rules)
{
    private sealed class State { public Queue<DateTimeOffset> Times { get; } = new(); public DateTimeOffset? Started; public DateTimeOffset LastHit; }
    private readonly LogSignatureRule[] _rules = rules.Take(32).Where(r => r.Id.Length <= 80 && r.Match.Length is > 0 and <= 256 &&
        r.Then.Length <= 256 && r.WindowSeconds is > 0 and <= 86400 && r.Threshold is > 0 and <= 100).ToArray();
    private readonly Dictionary<(string Rule, string Key), State> _states = new();
    internal static LogSignatureRules Shipped()
    {
        using var stream = typeof(LogSignatureRules).Assembly.GetManifestResourceStream("GlDrive.Logging.ops-signatures.json")!;
        return new(JsonSerializer.Deserialize<LogSignatureRule[]>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? []);
    }
    internal IReadOnlyList<LogSignatureHit> Observe(RecordedLog row)
    {
        var hits = new List<LogSignatureHit>();
        foreach (var rule in _rules)
        {
            if (rule.RequireCorrelation && row.Correlation.Length == 0) continue;
            var first = Contains(row, rule.Match);
            var second = rule.Then.Length > 0 && Contains(row, rule.Then);
            if (!first && !second) continue;
            var key = (rule.Id, rule.RequireCorrelation ? row.Correlation : "");
            if (!_states.TryGetValue(key, out var state))
            {
                if (_states.Count >= 1024) _states.Remove(_states.MinBy(x => x.Value.Started ?? x.Value.LastHit).Key);
                _states[key] = state = new();
            }
            var cutoff = row.Time.AddSeconds(-rule.WindowSeconds);
            while (state.Times.TryPeek(out var at) && at < cutoff) state.Times.Dequeue();
            if (rule.Kind == "rate" && first)
            {
                state.Times.Enqueue(row.Time);
                while (state.Times.Count > rule.Threshold) state.Times.Dequeue();
                if (state.Times.Count >= rule.Threshold) Hit(rule, key.Item2, state, row.Time, hits);
            }
            else if (rule.Kind is "sequence" or "absence")
            {
                if (second && state.Started is { } started)
                {
                    if (rule.Kind == "sequence" && started >= cutoff) Hit(rule, key.Item2, state, row.Time, hits);
                    state.Started = null;
                }
                if (first)
                {
                    if (rule.Kind == "sequence") state.Started = row.Time;
                    else state.Started ??= row.Time;
                }
            }
        }
        return hits;
    }
    internal IReadOnlyList<LogSignatureHit> Tick(DateTimeOffset now)
    {
        var hits = new List<LogSignatureHit>();
        foreach (var pair in _states)
        {
            var rule = _rules.First(r => r.Id == pair.Key.Rule);
            if (rule.Kind == "absence" && pair.Value.Started is { } started && now >= started.AddSeconds(rule.WindowSeconds))
            { Hit(rule, pair.Key.Key, pair.Value, now, hits); pair.Value.Started = null; }
        }
        return hits;
    }
    private static bool Contains(RecordedLog row, string value) => row.Template.Contains(value, StringComparison.OrdinalIgnoreCase) || row.Text.Contains(value, StringComparison.OrdinalIgnoreCase);
    private static void Hit(LogSignatureRule rule, string key, State state, DateTimeOffset now, List<LogSignatureHit> hits)
    {
        if (now - state.LastHit < TimeSpan.FromHours(1)) return;
        state.LastHit = now; hits.Add(new(rule.Id, key, now));
    }
}
