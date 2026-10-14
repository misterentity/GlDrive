using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using GlDrive.AiAgent;
using GlDrive.Config;
using GlDrive.Util;

namespace GlDrive.Services.Automation;

/// <summary>Manual agent controls continue to use the existing validators and audit trail.</summary>
public sealed class AgentAutomation : IAutomationModule
{
    private readonly AppConfig _config;
    private readonly Func<AgentRunner?> _runner;
    private readonly Func<FreezeStore?> _freeze;
    private readonly Func<AuditTrail?> _audit;
    private readonly Func<ChangeApplier?> _applier;
    private readonly string _root;
    private readonly DiagnosticAutomation _diagnostics;
    private readonly object _gate = new();
    private int _manualRunActive;

    public AgentAutomation(AppConfig config, Func<AgentRunner?> getRunner, Func<FreezeStore?> getFreeze,
        Func<AuditTrail?> getAudit, Func<ChangeApplier?> getApplier, string? appDataRoot = null)
    {
        _config = config;
        _runner = getRunner;
        _freeze = getFreeze;
        _audit = getAudit;
        _applier = getApplier;
        _root = Path.Combine(appDataRoot ?? ConfigManager.AppDataPath, "ai-data");
        _diagnostics = new DiagnosticAutomation(config, appDataRoot);
    }

    public IReadOnlyList<AutomationToolDefinition> Tools { get; } =
    [
        AutomationToolDefinition.Create("agent_status", "Read AI agent settings, service availability, freezes, and whether an MCP-triggered run is active. Scheduled run activity is not included in that flag.",
            new { type = "object", properties = new { }, additionalProperties = false }, readOnly: true),
        AutomationToolDefinition.Create("agent_run", "Run the existing AI tuning agent now using its configured model, consent, dry-run, freeze, confidence, and validation rules. Calls the configured external model service and may apply settings.",
            new { type = "object", properties = new { }, additionalProperties = false }),
        AutomationToolDefinition.Create("agent_abort", "Cancel the active AI run and stop its scheduler for this session. Use agent_run for a manual run, or restart to resume an enabled schedule.",
            new { type = "object", properties = new { }, additionalProperties = false }),
        AutomationToolDefinition.Create("agent_freeze", "List, add, or remove AI change freezes. Pointers use the AI agent's canonical targets (for example /servers/SERVER_ID/pool); root pointer freezes everything.",
            new { type = "object", properties = new { action = new { type = "string", @enum = new[] { "list", "set", "remove" } }, pointer = new { type = "string" }, note = new { type = "string" } }, required = new[] { "action" }, additionalProperties = false }),
        AutomationToolDefinition.Create("agent_audit", "Read up to 200 recent AI audit rows from a bounded 512 KiB tail, optionally filtered by runId. Secrets are redacted.",
            new { type = "object", properties = new { limit = new { type = "integer", minimum = 1, maximum = 200 }, runId = new { type = "string" } }, additionalProperties = false }, readOnly: true),
        AutomationToolDefinition.Create("agent_audit_action", "Undo an applied AI configuration change or apply a suggested configuration change through the existing validators and freeze rules. Identifies an audit row by runId and target. Appended/deleted list entries require config_patch; wishlist/error actions use their dedicated tools. Restart after a successful configuration change.",
            new { type = "object", properties = new { action = new { type = "string", @enum = new[] { "undo", "apply" } }, runId = new { type = "string" }, target = new { type = "string" } }, required = new[] { "action", "runId", "target" }, additionalProperties = false }, destructive: true),
        AutomationToolDefinition.Create("agent_memo", "Read or atomically replace the agent's persistent Markdown memory. Writes require action=set and content (maximum 32 KiB). Read content is credential-redacted.",
            new { type = "object", properties = new { action = new { type = "string", @enum = new[] { "get", "set" } }, content = new { type = "string", maxLength = 32768 } }, additionalProperties = false })
    ];

    public async Task<object?> ExecuteAsync(string operation, JsonElement arguments, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        switch (operation)
        {
            case "agent_status":
                return new { settings = _config.Agent, available = _runner() != null, manualRunActive = Volatile.Read(ref _manualRunActive) != 0,
                    freezes = _freeze()?.All, auditAvailable = _audit() != null };
            case "agent_run":
                if (!_config.Agent.HasAcceptedConsent) throw new InvalidOperationException("AI agent consent has not been accepted; review and enable it in settings first");
                var runner = _runner() ?? throw new InvalidOperationException("AI agent is unavailable");
                if (Interlocked.CompareExchange(ref _manualRunActive, 1, 0) != 0) throw new InvalidOperationException("An MCP-triggered AI run is already active");
                try
                {
                    using var registration = ct.Register(runner.Abort);
                    await runner.RunNowAsync();
                    ct.ThrowIfCancellationRequested();
                    return new { completed = true, message = "Run request completed. Inspect agent_audit and diagnostics briefs for results or rejections." };
                }
                finally { Volatile.Write(ref _manualRunActive, 0); }
            case "agent_abort":
                (_runner() ?? throw new InvalidOperationException("AI agent is unavailable")).Abort();
                return new { aborted = true, schedulerStopped = true };
            case "agent_freeze": return Freeze(arguments);
            case "agent_audit": return Audit(ConfigurationAutomation.Integer(arguments, "limit", 50), ConfigurationAutomation.String(arguments, "runId"));
            case "agent_memo": return Memo(arguments);
            case "agent_audit_action":
                if (System.Windows.Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
                    return await dispatcher.InvokeAsync(() => AuditAction(arguments));
                return AuditAction(arguments);
            default: throw new ArgumentException("Unknown agent operation");
        }
    }

    private object Freeze(JsonElement arguments)
    {
        var store = _freeze() ?? throw new InvalidOperationException("Freeze store is unavailable");
        var action = ConfigurationAutomation.String(arguments, "action") ?? "list";
        if (action != "list")
        {
            if (ConfigManager.ReadOnly) throw new InvalidOperationException("Settings are read-only");
            var pointer = ConfigurationAutomation.String(arguments, "pointer") ?? throw new ArgumentException("pointer is required");
            _ = JsonPointer.Split(pointer);
            if (pointer.Length > 4096) throw new ArgumentException("pointer is too long");
            if (action == "set") store.Freeze(pointer, ConfigurationAutomation.String(arguments, "note"));
            else if (action == "remove") store.Unfreeze(pointer);
            else throw new ArgumentException("action must be list, set, or remove");
        }
        return new { freezes = store.All };
    }

    private object Audit(int limit, string? runId)
    {
        _diagnostics.EnsureDirectory(_root);
        var path = Path.Combine(_root, "ai-audit.jsonl");
        if (!File.Exists(path)) return new { rows = Array.Empty<object>(), truncated = false, parseSkips = 0 };
        CheckFile(path);
        var tail = DiagnosticAutomation.ReadTail(path, 1000, 524288);
        var parsed = new List<JsonNode>();
        var skips = 0;
        foreach (var line in tail.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var row = JsonNode.Parse(line);
                if (row is not JsonObject) { skips++; continue; }
                if (ConfigSecretPointers.IsSecret(row["Target"]?.GetValue<string>()))
                {
                    if (row["Before"] != null) row["Before"] = "[redacted]";
                    if (row["After"] != null) row["After"] = "[redacted]";
                }
                if (runId == null || row["RunId"]?.GetValue<string>() == runId) parsed.Add(row);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { skips++; }
        }
        var redact = _diagnostics.CreateTextRedactor();
        return new { rows = parsed.TakeLast(Math.Clamp(limit, 1, 200)).Reverse().Select(row => _diagnostics.RedactJson(row, redact)).ToArray(),
            truncated = tail.Truncated || parsed.Count > limit, parseSkips = skips };
    }

    private object Memo(JsonElement arguments)
    {
        lock (_gate)
        {
            _diagnostics.EnsureDirectory(_root);
            var action = ConfigurationAutomation.String(arguments, "action") ?? "get";
            var path = Path.Combine(_root, "agent-memo.md");
            if (File.Exists(path)) CheckFile(path);
            if (action == "set")
            {
                if (ConfigManager.ReadOnly) throw new InvalidOperationException("Settings are read-only");
                var content = ConfigurationAutomation.String(arguments, "content") ?? throw new ArgumentException("content is required");
                if (content.Length > 32768) throw new ArgumentException("Memo exceeds 32 KiB characters");
                Directory.CreateDirectory(_root);
                SecureFile.WriteAllTextRestricted(path, content);
                return new { saved = true };
            }
            if (action != "get") throw new ArgumentException("action must be get or set");
            var tail = File.Exists(path) ? DiagnosticAutomation.ReadTail(path, 1000, 131072) : new DiagnosticAutomation.Tail("", false, 0);
            return new { content = _diagnostics.RedactText(tail.Text), tail.Truncated };
        }
    }

    private object AuditAction(JsonElement arguments)
    {
        if (ConfigManager.ReadOnly) throw new InvalidOperationException("Settings are read-only");
        var action = ConfigurationAutomation.String(arguments, "action");
        if (action is not ("undo" or "apply")) throw new ArgumentException("action must be undo or apply");
        var runId = ConfigurationAutomation.String(arguments, "runId") ?? throw new ArgumentException("runId is required");
        var target = ConfigurationAutomation.String(arguments, "target") ?? throw new ArgumentException("target is required");
        var audit = _audit() ?? throw new InvalidOperationException("Audit trail is unavailable");
        var applier = _applier() ?? throw new InvalidOperationException("AI change service is unavailable");
        object? result = null;
        SecureFile.WithPathLock(ConfigManager.ConfigPath, () =>
        {
            var row = audit.ReadAll().LastOrDefault(r => r.RunId == runId && r.Target == target)
                ?? throw new ArgumentException("Audit row was not found");
            if (row.Undone || (action == "undo" && !row.Applied) || (action == "apply" && row.Applied))
                throw new ArgumentException("The audit row is not eligible for this action");
            if (row.Category is AgentCategories.WishlistPrune or AgentCategories.ErrorReport)
                throw new ArgumentException("Use the dedicated wishlist/diagnostics tools for this action");
            var candidate = new ConfigurationAutomation(_config).CurrentSnapshot().Deserialize<AppConfig>(ConfigurationAutomation.Json)!;
            var inverse = new AgentChange
            {
                Category = row.Category, Target = row.Target, Before = action == "undo" ? row.After : row.Before,
                After = action == "undo" ? row.Before : row.After, Confidence = 1,
                Reasoning = "Explicit MCP " + action, EvidenceRef = row.RunId
            };
            var newRunId = "mcp-" + action + "-" + Guid.NewGuid().ToString("N")[..8];
            var pending = new List<AuditRow>();
            var report = action == "undo"
                ? GlDrive.UI.AgentUndoCompletion.ApplyInverse(row, () => applier.Apply([inverse], candidate, candidate.Agent, newRunId, false, pending.Add))
                : applier.Apply([inverse], candidate, candidate.Agent, newRunId, false, pending.Add);
            if (report is not { Applied: 1, Rejected: 0 })
            {
                foreach (var entry in pending) audit.Append(entry);
                result = new { applied = false, rejectionReasons = report?.RejectionByReason.Keys.ToArray() ?? ["service-unavailable"], restartRequired = false };
                return;
            }
            ConfigurationAutomation.Validate(candidate);
            var expected = File.ReadAllText(ConfigManager.ConfigPath);
            AgentConfigCommit.Commit(ConfigManager.ConfigPath, expected, candidate, pending, _root, ConfigManager.Save, audit);
            ConfigurationAutomation.Reconcile(_config, candidate);
            if (action == "undo") audit.MarkUndone(row.RunId, row.Target, "mcp-undo");
            result = new { applied = true, restartRequired = true, runId = newRunId };
        });
        return result!;
    }

    private static void CheckFile(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("Linked agent files are not supported");
    }
}
