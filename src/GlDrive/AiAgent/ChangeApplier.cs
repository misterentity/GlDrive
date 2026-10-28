using System.Text.Json;
using System.Text.Json.Nodes;
using Serilog;

namespace GlDrive.AiAgent;

public sealed class ChangeApplier
{
    private readonly Dictionary<string, IChangeValidator> _validators;
    private readonly FreezeStore _freeze;
    private readonly AuditTrail _audit;
    private readonly Func<GlDrive.Config.AppConfig, RacesDigest?>? _winnerEvidence;
    private readonly Func<GlDrive.Config.AppConfig, ReplayEvidence>? _replayEvidence;
    private readonly Func<GlDrive.Config.AppConfig, CancellationToken, ReplayEvidence>? _cancellableReplayEvidence;
    private readonly Func<string, int?>? _transferLimit;

    public ChangeApplier(IEnumerable<IChangeValidator> validators, FreezeStore freeze, AuditTrail audit,
        Func<GlDrive.Config.AppConfig, RacesDigest?>? winnerEvidence = null,
        Func<GlDrive.Config.AppConfig, ReplayEvidence>? replayEvidence = null,
        Func<string, int?>? transferLimit = null,
        Func<GlDrive.Config.AppConfig, CancellationToken, ReplayEvidence>? cancellableReplayEvidence = null)
    {
        _validators = validators.ToDictionary(v => v.Category);
        _freeze = freeze;
        _audit = audit;
        _winnerEvidence = winnerEvidence;
        _replayEvidence = replayEvidence;
        _cancellableReplayEvidence = cancellableReplayEvidence;
        _transferLimit = transferLimit;
    }

    public sealed class RunReport
    {
        public int Applied { get; set; }
        public int Rejected { get; set; }
        public Dictionary<string, int> RejectionByReason { get; } = new();
        public Dictionary<string, int> AppliedByCategory { get; } = new();
    }

    public RunReport Apply(IEnumerable<AgentChange> changes, GlDrive.Config.AppConfig config,
                           GlDrive.Config.AgentConfig agentCfg, string runId, bool dryRun,
                           Action<AuditRow>? record = null, bool configOnly = false,
                           RacesDigest? winnerEvidence = null, bool explicitUndo = false,
                           bool explicitOperator = false, CancellationToken cancellationToken = default)
    {
        using var replayBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        replayBudget.CancelAfter(TimeSpan.FromSeconds(5));
        record ??= _audit.Append;
        var report = new RunReport();
        var perCategoryCount = new Dictionary<string, int>();
        double confidenceFloor = agentCfg.ConfidenceThreshold_x100 / 100.0;
        ChangeValidationContext? validationContext = null;

        // Cache the current config until a mutation. Both manual and automatic batches must
        // compare later proposals with the values left by earlier changes in this run.
        JsonNode? configNode = null;
        bool configNodeBuilt = false;
        ReplayEvidence? replay = null;

        foreach (var change in changes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // STJ deserializes "target": null as null even though the property has = "" default.
            // Validators dereference Target via StartsWith etc. without null-checks; normalize once
            // here so a sloppy AI response can't NRE the whole run.
            change.Target ??= "";
            change.Category ??= "";
            // Before the freeze check: freezes are stored in the canonical form, so a real-path
            // proposal must not slip past one.
            change.Target = CanonicalizeTarget(change.Target, config);

            string? reject = null;
            ImpactSummary? impact = null;

            // Before the freeze check: an empty Target is the JSON Pointer root, an ancestor of
            // every freeze, so malformed model output was audited as "frozen" (2026-10-05).
            if (string.IsNullOrWhiteSpace(change.Category) || string.IsNullOrWhiteSpace(change.Target))
                reject = "malformed";
            else if (ConfigSecretPointers.IsSecret(change.Target))
                reject = "secret-target";
            else if (_freeze.IsFrozen(change.Target) || _freeze.All.Any(entry =>
                {
                    var frozen = CanonicalizeTarget(entry.Path, config);
                    return JsonPointer.IsAncestorOrSelf(frozen, change.Target)
                        || JsonPointer.IsAncestorOrSelf(change.Target, frozen)
                        || RemovalShiftsFrozenRule(change, frozen);
                }))
                reject = "frozen";
            // Role changes alter routing. Confidence and a cited filename are
            // not verified evidence: use the existing explicit review flow.
            else if (configOnly && !dryRun && change.Category is AgentCategories.WishlistPrune or AgentCategories.ErrorReport or AgentCategories.DownloadOnly)
                reject = "requires-manual-action";
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
            // the per-category validator below still guards the actual mutation. Whole-object
            // proposals can be partial patches, so they need their category-specific validation.
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
                if (configNode is not null && (configOnly || !string.IsNullOrEmpty(beforeNorm)))
                {
                    JsonNode? resolved;
                    try { resolved = JsonPointer.Resolve(configNode, ConfigTarget(change.Target, config)); }
                    catch { resolved = null; } // malformed pointer -> treat as unresolved (lenient)

                    if (resolved is JsonValue)
                    {
                        string? liveNorm = NormalizeScalar(resolved);
                        // Config persists priorities as numeric enum values; the model may use
                        // their documented names. Compare the same semantic tier either way.
                        if (change.Category == AgentCategories.Priority
                            && Enum.TryParse<GlDrive.Config.SitePriority>(beforeNorm, out var priority))
                            beforeNorm = ((int)priority).ToString(System.Globalization.CultureInfo.InvariantCulture);
                        if (!string.Equals(liveNorm, beforeNorm, StringComparison.Ordinal))
                            reject = "before-mismatch";
                    }
                }
            }

            if (reject is null)
            {
                if (validationContext == null && change.Category is AgentCategories.Priority or AgentCategories.DownloadOnly)
                {
                    if (!explicitUndo && winnerEvidence == null && _winnerEvidence != null)
                    {
                        try { winnerEvidence = _winnerEvidence(config); }
                        catch (Exception ex)
                        {
                            Log.Warning("Winner evidence unavailable ({Failure}); priority and role changes require measured coverage", ex.GetType().Name);
                        }
                    }
                    validationContext = new ChangeValidationContext(winnerEvidence, explicitUndo);
                }
                var vr = _validators[change.Category].Validate(change, config,
                    validationContext ?? new ChangeValidationContext(explicitUndo: explicitUndo));
                if (!vr.Ok)
                {
                    reject = vr.RejectionReason ?? "invariant-failed";
                }
                else
                {
                    // Only config-owned validators may be projected. Wishlist/error validators
                    // write external stores regardless of their AppConfig argument.
                    if (MutatesConfig(change.Category) && (agentCfg.NoOpRejectionEnabled
                        || (!explicitUndo && agentCfg.CounterfactualReplayEnabled && ImpactSimulator.RequiresReplay(change.Category))))
                    {
                        try
                        {
                            var beforeProjection = SerializeConfig(config) ?? throw new InvalidOperationException();
                            var projection = System.Text.Json.JsonSerializer.Deserialize<GlDrive.Config.AppConfig>(beforeProjection,
                                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;
                            var projected = _validators[change.Category].Validate(change, projection,
                                validationContext ?? new ChangeValidationContext(explicitUndo: explicitUndo));
                            if (!projected.Ok) reject = projected.RejectionReason ?? "invariant-failed";
                            else
                            {
                                projected.Mutate?.Invoke(projection);
                                if (agentCfg.NoOpRejectionEnabled && beforeProjection == SerializeConfig(projection)) reject = "no-effect";
                                else if (agentCfg.NoOpRejectionEnabled && !explicitUndo && CapacityUnchanged(change, config, projection))
                                    reject = "capacity-no-effect";
                                if (reject == null && !explicitUndo && agentCfg.CounterfactualReplayEnabled
                                    && ImpactSimulator.RequiresReplay(change.Category))
                                {
                                    try
                                    {
                                        replayBudget.Token.ThrowIfCancellationRequested();
                                        replay ??= _cancellableReplayEvidence?.Invoke(config, replayBudget.Token)
                                            ?? _replayEvidence?.Invoke(config) ?? new ReplayEvidence([], [], false);
                                        impact = ImpactSimulator.Evaluate(change, config, projection, replay, replayBudget.Token);
                                    }
                                    catch (OperationCanceledException)
                                    { impact = new(0, 0, 0, 0, false, "impact-time-budget-exceeded"); }
                                    catch { impact = new(0, 0, 0, 0, false, "impact-preview-unavailable"); }
                                    if (!explicitOperator) reject = impact.RejectionReason;
                                }
                            }
                        }
                        catch { reject = "impact-preview-unavailable"; }
                    }
                    if (reject != null) goto Rejected;
                    cancellationToken.ThrowIfCancellationRequested();
                    bool mutationOk = true;
                    object? appliedBefore = change.Before;
                    object? appliedAfter = change.After;
                    if (!dryRun)
                    {
                        // Validators clamp and guard INSIDE their mutations (login ceiling, ±1 tier,
                        // user-edited triggers), so an accepted change can still write nothing.
                        // Recording that as applied told the model its change had landed:
                        // maxConcurrentRaces 1→2 was "applied" 50 days running while config stayed 1.
                        var beforeJson = MutatesConfig(change.Category) ? SerializeConfig(config) : null;
                        try { vr.Mutate?.Invoke(config); }
                        catch (Exception ex)
                        {
                            Log.Warning(ex, "ChangeApplier mutation threw for {Category} {Target}", change.Category, change.Target);
                            reject = "mutation-threw:" + ex.GetType().Name;
                            mutationOk = false;
                        }
                        var afterJson = beforeJson is not null ? SerializeConfig(config) : null;
                        if (mutationOk && beforeJson is not null && beforeJson == afterJson)
                        {
                            reject = "no-effect";
                            mutationOk = false;
                        }
                        if (mutationOk && beforeJson is not null && afterJson is not null)
                        {
                            // Audit the persisted values, including validator clamps and partial
                            // object patches. Requested values are evidence only for rejections.
                            var target = ConfigTarget(change.Target, config);
                            appliedBefore = AuditValue(JsonNode.Parse(beforeJson)!, target);
                            appliedAfter = IsRuleRemoval(change) ? null
                                : AuditValue(JsonNode.Parse(afterJson)!, target, appended: true);
                        }
                        // Even a throwing validator may have touched config before throwing.
                        configNodeBuilt = false;
                    }
                    if (mutationOk)
                    {
                        record(new AuditRow
                        {
                            RunId = runId,
                            Origin = explicitOperator ? "operator" : explicitUndo ? "undo" : "agent",
                            Category = change.Category,
                            Target = change.Target,
                            Before = appliedBefore,
                            After = appliedAfter,
                            Reasoning = change.Reasoning,
                            EvidenceRef = change.EvidenceRef,
                            Confidence = change.Confidence,
                            ExpectedEffect = change.ExpectedEffect,
                            Impact = impact,
                            Outcome = !dryRun && !explicitOperator && !explicitUndo && agentCfg.AccountabilityEnabled && !OutcomeEvaluator.Valid(change.ExpectedEffect)
                                ? OutcomeEvaluator.Score(change.ExpectedEffect, [], [], true, DateTime.UtcNow) : null,
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

        Rejected:
            record(new AuditRow
            {
                RunId = runId,
                Category = change.Category,
                Target = change.Target,
                Before = AuditProposedValue(change.Target, change.Before),
                After = AuditProposedValue(change.Target, change.After),
                Reasoning = change.Reasoning,
                EvidenceRef = change.EvidenceRef,
                Confidence = change.Confidence,
                ExpectedEffect = change.ExpectedEffect,
                Impact = impact,
                Applied = false,
                DryRun = dryRun,
                RejectionReason = reject
            });
            report.Rejected++;
            report.RejectionByReason[reject!] = report.RejectionByReason.GetValueOrDefault(reject!) + 1;
        }
        return report;
    }

    private bool CapacityUnchanged(AgentChange change, GlDrive.Config.AppConfig before, GlDrive.Config.AppConfig after)
    {
        if (change.Category != AgentCategories.PoolSizing) return false;
        // The runtime caps spread pools at three; changing a larger nominal value cannot add capacity.
        if (change.Target == "/spread/spreadPoolSize")
            return Math.Min(3, before.Spread.SpreadPoolSize) == Math.Min(3, after.Spread.SpreadPoolSize);
        var id = change.Target.Split('/').ElementAtOrDefault(2);
        var oldSite = before.Servers.FirstOrDefault(s => s.Id == id);
        var newSite = after.Servers.FirstOrDefault(s => s.Id == id);
        if (oldSite == null || newSite == null) return false;
        var upload = change.Target.EndsWith("/maxUploadSlots", StringComparison.Ordinal);
        var download = change.Target.EndsWith("/maxDownloadSlots", StringComparison.Ordinal);
        if (!upload && !download) return false;
        if (upload && oldSite.SpreadSite.DownloadOnly) return true;
        // Prefer the observed transfer gate; fall back to the same configured reservation model.
        var reserved = Math.Min(2, Math.Max(1, oldSite.Pool.LoginCap - Math.Max(0, oldSite.Pool.LoginHeadroom)) - 1);
        var cap = Math.Max(1, Math.Min(3, _transferLimit?.Invoke(oldSite.Id) ?? reserved));
        cap = Math.Min(cap, Math.Max(1, before.Spread.SpreadPoolSize));
        return Math.Min(cap, upload ? oldSite.SpreadSite.MaxUploadSlots : oldSite.SpreadSite.MaxDownloadSlots)
            == Math.Min(cap, upload ? newSite.SpreadSite.MaxUploadSlots : newSite.SpreadSite.MaxDownloadSlots);
    }

    internal void VerifyCommitFreezes(IEnumerable<AuditRow> rows, GlDrive.Config.AppConfig config)
    {
        _freeze.Reload();
        foreach (var row in rows.Where(r => r.WasApplied))
        {
            var target = CanonicalizeTarget(row.Target, config);
            if (_freeze.IsFrozen(target) || _freeze.All.Any(f =>
                JsonPointer.IsAncestorOrSelf(target, CanonicalizeTarget(f.Path, config))
                || JsonPointer.IsAncestorOrSelf(CanonicalizeTarget(f.Path, config), target)))
                throw new InvalidOperationException("An applied proposal became frozen before commit; settings were preserved.");
        }
    }

    internal void GuardCommit(string configPath, IEnumerable<AuditRow> rows, GlDrive.Config.AppConfig config, Action commit)
    {
        // Match the settings transaction's lock order, and keep Freeze()/Unfreeze()
        // serialized with the actual persistence boundary, not only background scoring.
        GlDrive.Util.SecureFile.WithPathLock(configPath, () => _freeze.WithLock(() =>
        {
            VerifyCommitFreezes(rows, config);
            commit();
        }));
    }

    // wishlistPrune writes the wishlist store and errorReport writes a Markdown file; neither
    // touches AppConfig, so a config diff cannot tell whether they took effect.
    private static bool MutatesConfig(string category) =>
        category is not (AgentCategories.WishlistPrune or AgentCategories.ErrorReport);

    private static object? AuditProposedValue(string target, object? value) =>
        ConfigSecretPointers.IsSecret(target)
            ? ConfigSecretPointers.Mask(value is null ? null : JsonSerializer.Serialize(value))
            : value;

    private static string? SerializeConfig(GlDrive.Config.AppConfig config)
    {
        try
        {
            return JsonSerializer.Serialize(config,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "ChangeApplier: config serialize failed; skipping no-effect check");
            return null;
        }
    }

    // The validators address server fields through a stable alias (/servers/{id}/spread/...), which
    // is also the form freezes are stored in. The model reads the real config and uses its keys
    // (/servers/{id}/spreadSite/skiplist, .../priority) and sometimes an array index instead of the
    // id. Map both onto the alias so every validator and the freeze check see one spelling.
    private static readonly Dictionary<string, string> SpreadFieldAliases = new(StringComparer.Ordinal)
    {
        ["skiplist"] = "skiplistRules",
        ["priority"] = "sitePriority",
    };

    internal static string CanonicalizeTarget(string target, GlDrive.Config.AppConfig config)
    {
        const string prefix = "/servers/";
        if (!target.StartsWith(prefix, StringComparison.Ordinal)) return target;
        var parts = target[prefix.Length..].Split('/');
        if (int.TryParse(parts[0], out var idx) && idx >= 0 && idx < config.Servers.Count
            && !config.Servers.Any(s => s.Id == parts[0]))
            parts[0] = config.Servers[idx].Id;

        if (parts.Length > 1 && parts[1] is "spreadSite" or "spread")
        {
            parts[1] = "spread";
            if (parts.Length > 2 && SpreadFieldAliases.TryGetValue(parts[2], out var alias))
                parts[2] = alias;
        }
        // Validators accept numeric indices with int.TryParse. Normalize those spellings
        // before freeze comparisons so /00 and /+0 cannot evade a freeze on /0.
        if (parts.Length > 3
            && ((parts[1] == "spread" && parts[2] is "skiplistRules" or "sectionMappings")
                || (parts[1] == "irc" && parts[2] == "announceRules"))
            && int.TryParse(parts[3], out var itemIndex) && itemIndex >= 0)
            parts[3] = itemIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return prefix + string.Join('/', parts);
    }

    private static bool IsRuleRemoval(AgentChange change) =>
        change.Category == AgentCategories.Skiplist && change.After is null;

    private static bool RemovalShiftsFrozenRule(AgentChange change, string frozen)
    {
        if (!IsRuleRemoval(change)) return false;
        int slash = change.Target.LastIndexOf('/');
        if (slash < 0 || !int.TryParse(change.Target[(slash + 1)..], out var removedIndex)) return false;
        var listPrefix = change.Target[..(slash + 1)];
        if (!frozen.StartsWith(listPrefix, StringComparison.Ordinal)) return false;
        var frozenIndex = frozen[listPrefix.Length..].Split('/')[0];
        return int.TryParse(frozenIndex, out var index) && index >= removedIndex;
    }

    // Validator/freeze aliases are not actual JSON pointers into AppConfig: servers serialize
    // as an array and their spread settings live under spreadSite. Translate only for reading.
    internal static string ConfigTarget(string target, GlDrive.Config.AppConfig config)
    {
        if (!target.StartsWith("/servers/", StringComparison.Ordinal)) return target;
        var parts = target[1..].Split('/');
        var index = config.Servers.FindIndex(s => s.Id == parts[1]);
        if (index < 0) return target;
        parts[1] = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (parts.Length > 2 && parts[2] == "spread")
        {
            parts[2] = "spreadSite";
            if (parts.Length > 3)
                parts[3] = parts[3] switch { "skiplistRules" => "skiplist", "sitePriority" => "priority", _ => parts[3] };
        }
        return "/" + string.Join('/', parts);
    }

    private static object? AuditValue(JsonNode root, string target, bool appended = false)
    {
        JsonNode? value;
        if (appended && target.EndsWith("/-", StringComparison.Ordinal))
        {
            var list = JsonPointer.Resolve(root, target[..^2]) as JsonArray;
            value = list is { Count: > 0 } ? list[list.Count - 1] : null;
        }
        else value = JsonPointer.Resolve(root, target);
        return value is null ? null : JsonSerializer.SerializeToElement(value);
    }

    /// <summary>Compare decoded scalar values, preserving regex escapes and string whitespace.</summary>
    private static string? NormalizeScalar(object? before)
    {
        if (before is null) return null;
        try
        {
            if (before is string s) return s;
            var value = JsonSerializer.SerializeToElement(before);
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
                _ => null,
            };
        }
        catch { return null; }
    }

}
