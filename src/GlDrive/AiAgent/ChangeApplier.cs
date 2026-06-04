using System.Text.Json;
using System.Text.Json.Nodes;
using Serilog;

namespace GlDrive.AiAgent;

public sealed class ChangeApplier
{
    private readonly Dictionary<string, IChangeValidator> _validators;
    private readonly FreezeStore _freeze;
    private readonly AuditTrail _audit;

    public ChangeApplier(IEnumerable<IChangeValidator> validators, FreezeStore freeze, AuditTrail audit)
    {
        _validators = validators.ToDictionary(v => v.Category);
        _freeze = freeze;
        _audit = audit;
    }

    public sealed class RunReport
    {
        public int Applied { get; set; }
        public int Rejected { get; set; }
        public Dictionary<string, int> RejectionByReason { get; } = new();
        public Dictionary<string, int> AppliedByCategory { get; } = new();
    }

    public RunReport Apply(IEnumerable<AgentChange> changes, GlDrive.Config.AppConfig config,
                           GlDrive.Config.AgentConfig agentCfg, string runId, bool dryRun)
    {
        var report = new RunReport();
        var perCategoryCount = new Dictionary<string, int>();
        double confidenceFloor = agentCfg.ConfidenceThreshold_x100 / 100.0;

        // Serialize the live config ONCE per run (not per change) so the AgentPrompt's promised
        // "before must match the current value at target" cross-check has a stable JSON view to
        // resolve pointers against. Must use the SAME camelCase policy ConfigManager.Save uses —
        // otherwise pointers like "/servers/0/spread/maxSlots" wouldn't resolve. Lazily built so a
        // dry/all-rejected run that never reaches the check pays nothing.
        JsonNode? configNode = null;
        bool configNodeBuilt = false;

        foreach (var change in changes)
        {
            // STJ deserializes "target": null as null even though the property has = "" default.
            // Validators dereference Target via StartsWith etc. without null-checks; normalize once
            // here so a sloppy AI response can't NRE the whole run.
            change.Target ??= "";
            change.Category ??= "";

            string? reject = null;

            if (_freeze.IsFrozen(change.Target))
                reject = "frozen";
            else if (!_validators.TryGetValue(change.Category, out var v))
                reject = "unknown-category";
            else if (change.Confidence < confidenceFloor && change.Category != AgentCategories.ErrorReport)
                reject = "low-confidence";
            else if (report.Applied >= agentCfg.MaxChangesPerRun)
                reject = "budget-exceeded-total";
            else if (perCategoryCount.GetValueOrDefault(change.Category) >= agentCfg.MaxChangesPerCategory)
                reject = "budget-exceeded-category";

            // AgentPrompt tells the model "before must match the current value at target (the
            // Applier cross-checks)". Enforce it here, but LENIENTLY: the dangerous failure mode is
            // rejecting EVERY change because a pointer-shape mismatch makes the target unresolvable.
            // So we ONLY reject when the target resolves to a concrete non-null scalar AND `before`
            // is a non-empty value AND the normalized string forms genuinely differ. Anything
            // ambiguous (unresolved target, null/empty before, object/array shapes) is SKIPPED —
            // the per-category validator below still guards the actual mutation, so skipping here
            // can never let a bad change through; it just declines to add a second gate.
            if (reject is null)
            {
                if (!configNodeBuilt)
                {
                    configNodeBuilt = true;
                    try
                    {
                        // Same naming policy as ConfigManager.JsonOptions (private there); keeping it
                        // in sync is what makes JSON Pointers from the prompt resolve correctly.
                        var json = JsonSerializer.Serialize(config,
                            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                        configNode = JsonNode.Parse(json);
                    }
                    catch (Exception ex)
                    {
                        // Never let a serialization hiccup block the run — just disable the check.
                        Log.Debug(ex, "ChangeApplier before-check: config serialize failed; skipping cross-check");
                        configNode = null;
                    }
                }

                string? beforeNorm = NormalizeScalar(change.Before);
                if (configNode is not null && !string.IsNullOrEmpty(beforeNorm))
                {
                    JsonNode? resolved;
                    try { resolved = JsonPointer.Resolve(configNode, change.Target); }
                    catch { resolved = null; } // malformed pointer -> treat as unresolved (lenient)

                    if (resolved is not null)
                    {
                        string liveNorm = NormalizeScalar(resolved.ToJsonString()) ?? "";
                        if (!string.Equals(liveNorm, beforeNorm, StringComparison.Ordinal))
                            reject = "before-mismatch";
                    }
                }
            }

            if (reject is null)
            {
                var vr = _validators[change.Category].Validate(change, config);
                if (!vr.Ok)
                {
                    reject = vr.RejectionReason ?? "invariant-failed";
                }
                else
                {
                    bool mutationOk = true;
                    if (!dryRun)
                    {
                        try { vr.Mutate?.Invoke(config); }
                        catch (Exception ex)
                        {
                            Log.Warning(ex, "ChangeApplier mutation threw for {Category} {Target}", change.Category, change.Target);
                            reject = "mutation-threw:" + ex.GetType().Name;
                            mutationOk = false;
                        }
                    }
                    if (mutationOk)
                    {
                        _audit.Append(new AuditRow
                        {
                            RunId = runId,
                            Category = change.Category,
                            Target = change.Target,
                            Before = change.Before,
                            After = change.After,
                            Reasoning = change.Reasoning,
                            EvidenceRef = change.EvidenceRef,
                            Confidence = change.Confidence,
                            Applied = true,
                            DryRun = dryRun
                        });
                        report.Applied++;
                        perCategoryCount[change.Category] = perCategoryCount.GetValueOrDefault(change.Category) + 1;
                        report.AppliedByCategory[change.Category] = perCategoryCount[change.Category];
                        continue;
                    }
                }
            }

            _audit.Append(new AuditRow
            {
                RunId = runId,
                Category = change.Category,
                Target = change.Target,
                Before = change.Before,
                After = change.After,
                Reasoning = change.Reasoning,
                EvidenceRef = change.EvidenceRef,
                Confidence = change.Confidence,
                Applied = false,
                DryRun = dryRun,
                RejectionReason = reject
            });
            report.Rejected++;
            report.RejectionByReason[reject!] = report.RejectionByReason.GetValueOrDefault(reject!) + 1;
        }
        return report;
    }

    /// <summary>
    /// Reduces a `before` value (object? — usually a JsonElement or string from STJ) to a
    /// comparable scalar string. Serializes via JSON so numbers/bools/strings all round-trip
    /// the same way the live config node does, then strips ONE layer of surrounding quotes so a
    /// quoted scalar ("3") and an unquoted one (3) compare equal. Returns null for JSON null.
    /// </summary>
    private static string? NormalizeScalar(object? before)
    {
        if (before is null) return null;
        try
        {
            string raw = before is string s ? s : JsonSerializer.Serialize(before);
            return NormalizeScalar(raw);
        }
        catch { return null; }
    }

    /// <summary>Trims whitespace and one layer of surrounding double quotes. "null" -> null.</summary>
    private static string? NormalizeScalar(string? raw)
    {
        if (raw is null) return null;
        string t = raw.Trim();
        if (t.Length >= 2 && t[0] == '"' && t[^1] == '"')
            t = t[1..^1];
        return t == "null" ? null : t;
    }
}
