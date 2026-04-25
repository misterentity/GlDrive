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
}
