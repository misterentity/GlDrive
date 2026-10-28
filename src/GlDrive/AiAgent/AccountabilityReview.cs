using System.Text.Json;
using System.Text.Json.Nodes;
using GlDrive.Config;
using Serilog;

namespace GlDrive.AiAgent;

/// <summary>Runs on the existing agent worker. Neither scoring nor failed bookkeeping gates engine work.</summary>
internal static class AccountabilityReview
{
    internal static List<AuditRow> Assess(AuditTrail audit, LogDigester reader, AppConfig config, DateTime nowUtc)
    {
        List<AuditRow> rows;
        try { rows = audit.ReadAll().ToList(); }
        catch { Log.Warning("Agent accountability history unavailable; current settings are unchanged"); return []; }
        if (!config.Agent.AccountabilityEnabled) return rows;
        var outcomes = new Dictionary<string, AgentOutcome>();
        foreach (var row in rows.Where(r => r.WasApplied && !r.Undone && r.Id != null && r.AutomaticRevertOf == null))
        {
            if (row.ExpectedEffect == null) continue; // legacy/undeclared rows stay explicitly unscored
            if (row.Outcome is { Status: not "unscored" }) continue;
            if (row.Outcome?.Reason is "missing-or-invalid-expected-effect" or "window-aged-out") continue;
            if (!OutcomeEvaluator.TryUtc(row.Ts, out var applied) || applied > nowUtc) continue;
            var effect = row.ExpectedEffect;
            if (OutcomeEvaluator.Valid(effect) && applied.AddHours(effect!.WindowHours) > nowUtc) continue;
            AgentOutcome result;
            try
            {
                if (!OutcomeEvaluator.Valid(effect)) result = OutcomeEvaluator.Score(effect, [], [], true, nowUtc);
                else
                {
                    var baselineStart = applied.AddDays(-7);
                    if (baselineStart < nowUtc.AddDays(-Math.Max(1, config.Agent.DeleteAfterDays)))
                    {
                        result = new("unscored", null, null, 0, 0, nowUtc, "window-aged-out");
                        goto Assessed;
                    }
                    var priorSkips = reader.ParseSkips;
                    var before = ReadWindow(reader, row.Target, baselineStart, baselineStart.AddHours(effect!.WindowHours));
                    var after = ReadWindow(reader, row.Target, applied, applied.AddHours(effect.WindowHours));
                    result = OutcomeEvaluator.Score(effect, before, after, reader.ParseSkips == priorSkips, nowUtc,
                        config.Agent.OutcomeMinimumSamples, config.Agent.OutcomeTolerancePercent);
                }
            }
            catch { result = new("unscored", null, null, 0, 0, nowUtc, "evidence-unavailable"); }
        Assessed:
            if (row.Outcome is { } prior && prior with { EvaluatedAtUtc = result.EvaluatedAtUtc } == result) continue;
            row.Outcome = result;
            outcomes[row.Id!] = result;
        }
        audit.RecordOutcomes(outcomes);
        return rows;
    }

    internal static List<AgentOutcomeFeedback> Feedback(IEnumerable<AuditRow> rows, DateTime nowUtc) => rows
        .Where(r => r.WasApplied && r.IsAgentRun && r.Id != null && OutcomeEvaluator.Valid(r.ExpectedEffect)
            && r.Outcome?.Status is "effective" or "no-effect" or "regressed"
            && OutcomeEvaluator.TryUtc(r.Ts, out var at) && at >= nowUtc.AddDays(-30) && at <= nowUtc)
        .OrderByDescending(r => DateTimeOffset.Parse(r.Ts)).Take(50)
        .Select(r => new AgentOutcomeFeedback(r.Id!, r.RunId, r.Category, r.Target,
            r.ExpectedEffect!.Metric, r.ExpectedEffect.Direction, r.ExpectedEffect.WindowHours, r.Outcome!)).ToList();

    private static IReadOnlyList<RaceOutcomeEvent> ReadWindow(LogDigester reader, string target, DateTime from, DateTime to)
    {
        var id = target.StartsWith("/servers/", StringComparison.Ordinal) ? target.Split('/').ElementAtOrDefault(2) : null;
        var result = new List<RaceOutcomeEvent>();
        foreach (var row in reader.ReadStream<RaceOutcomeEvent>("races", from.ToLocalTime(), to.ToLocalTime()))
        {
            if (!OutcomeEvaluator.TryUtc(row.Ts, out var at)) throw new System.IO.InvalidDataException("Invalid observation timestamp");
            if (at >= from && at < to && (id == null || row.Participants.Any(p => p.ServerId == id))) result.Add(row);
        }
        return result;
    }

    internal static List<NegativeCorrection> Corrections(LogDigester reader, AppConfig config,
        IEnumerable<AuditRow> rows, DateTime nowUtc)
    {
        if (!config.Agent.AccountabilityEnabled) return [];
        try
        {
            var from = nowUtc.AddDays(-Math.Clamp(config.Agent.WindowDays, 1, 30));
            var events = reader.ReadStream<ConfigOverrideEvent>("overrides", from, nowUtc)
                .Where(r => OutcomeEvaluator.TryUtc(r.Ts, out var at) && at >= from && at <= nowUtc);
            return [.. CorrectionDetector.Detect(config, rows, events, nowUtc)];
        }
        catch { return []; }
    }

    internal static List<AuditRow> RevertRegressions(ChangeApplier applier, AppConfig candidate,
        IEnumerable<AuditRow> scoredRows, string runId)
    {
        var pending = new List<AuditRow>();
        if (!candidate.Agent.AutoRevertRegressions || !candidate.Agent.AccountabilityEnabled) return pending;
        foreach (var row in scoredRows.Where(r => r.WasApplied && r.IsAgentRun && !r.Undone && r.Id != null
                     && r.AutomaticRevertOf == null && r.Outcome?.Status == "regressed"))
        {
            if (row.Target.EndsWith("/-", StringComparison.Ordinal)) continue;
            var before = JsonSerializer.SerializeToNode(row.Before);
            var after = JsonSerializer.SerializeToNode(row.After);
            if (before is not JsonValue || after is not JsonValue) continue;
            var snapshot = JsonSerializer.SerializeToNode(candidate,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;
            JsonNode? actual;
            try { actual = JsonPointer.Resolve(snapshot, ChangeApplier.ConfigTarget(row.Target, candidate)); }
            catch { continue; }
            // A subsequent user edit always wins, even when the old outcome regressed.
            if (!JsonNode.DeepEquals(actual, after)) continue;
            var inverse = new AgentChange
            {
                Category = row.Category, Target = row.Target, Before = row.After, After = row.Before,
                Confidence = 1, Reasoning = "Opt-in revert of a locally measured regression", EvidenceRef = row.Id!
            };
            applier.Apply([inverse], candidate, candidate.Agent, runId, false,
                entry => { entry.AutomaticRevertOf = row.Id; pending.Add(entry); }, configOnly: true, explicitUndo: true);
        }
        return pending;
    }

    internal static string Report(IEnumerable<AuditRow> rows, IEnumerable<AuditRow> previous)
    {
        var current = rows.ToArray();
        var measured = previous.Where(r => r.WasApplied).ToArray();
        var lines = new List<string> { "# Validated agent results", "",
            $"Applied: {current.Count(r => r.WasApplied)}. Previews: {current.Count(r => r.Applied && r.DryRun)}. Rejected: {current.Count(r => !r.Applied)}.", "" };
        foreach (var row in current)
            lines.Add($"- {row.Status}: {row.Category} {row.Target}" + (row.RejectionReason == null ? "" : $" ({row.RejectionReason})")
                + (row.WasApplied && !OutcomeEvaluator.Valid(row.ExpectedEffect) ? " — unscored: missing/invalid expected effect" : ""));
        lines.Add("");
        lines.Add("Measured outcomes (observational): " + string.Join(", ", measured.GroupBy(r => r.Outcome?.Status
                ?? (OutcomeEvaluator.Valid(r.ExpectedEffect) ? "pending" : "unscored"))
            .Select(g => $"{g.Key}={g.Count()}")) + ".");
        return string.Join("\n", lines);
    }
}
