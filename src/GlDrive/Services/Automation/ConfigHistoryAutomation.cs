using System.Text.Json;
using System.Text.Json.Nodes;
using GlDrive.Config;

namespace GlDrive.Services.Automation;

/// <summary>User-requested history, experiments and time-boxed settings.</summary>
public sealed class ConfigHistoryAutomation(ConfigChangeService changes, ConfigurationAutomation configuration,
    Func<ConfigHistoryStore>? history = null) : IAutomationModule
{
    public IReadOnlyList<AutomationToolDefinition> Tools { get; } =
    [
        AutomationToolDefinition.Create("config_history", "Read numbered, attributed configuration versions and secret-masked diffs. History recording requires configHistory.enabled; encrypted snapshots are never returned.",
            new { type = "object", properties = new { limit = new { type = "integer", minimum = 1, maximum = 200 } }, additionalProperties = false }, readOnly: true),
        AutomationToolDefinition.Create("config_restore", "Explicitly restore a numbered version through typed validation, normal settings persistence and runtime refresh. Retains the current control listener, token and security policy. requireIdle optionally refuses the operation while races are active or queued.",
            new { type = "object", properties = new { version = new { type = "integer", minimum = 1 }, requireIdle = new { type = "boolean" } }, required = new[] { "version" }, additionalProperties = false }, destructive: true),
        AutomationToolDefinition.Create("config_preview", "Preview the A4 server remount and IRC restart impact of a JSON Patch without saving.",
            new { type = "object", properties = new { operations = new { type = "array", minItems = 1, maxItems = 100, items = new { type = "object" } } }, required = new[] { "operations" }, additionalProperties = false }, readOnly: true),
        AutomationToolDefinition.Create("config_lease", "List, start, or keep an explicitly approved temporary scalar setting. Expiry restores only while the leased value still holds; newer edits win. Persists across restarts. applyWhenIdle defers both initial edit and expiry using existing race-idle signals. No secrets, library enablement/dry-run controls, or indexed lists except stable server identities.",
            new { type = "object", properties = new { action = new { type = "string", @enum = new[] { "list", "start", "keep" } }, id = new { type = "string" }, pointer = new { type = "string" }, value = new { }, expiresUtc = new { type = "string" }, label = new { type = "string", maxLength = 256 }, applyWhenIdle = new { type = "boolean" } }, required = new[] { "action" }, additionalProperties = false }),
        AutomationToolDefinition.Create("config_experiment", "List, start, keep, or explicitly revert a labelled configuration experiment. Saves a protected baseline and local rolling-24h race counts before/after; counts are observational, not causal scores. Revert restores the full baseline through validation.",
            new { type = "object", properties = new { action = new { type = "string", @enum = new[] { "list", "start", "keep", "revert" } }, id = new { type = "string" }, label = new { type = "string", maxLength = 256 }, hypothesis = new { type = "string", maxLength = 2048 } }, required = new[] { "action" }, additionalProperties = false }, destructive: true)
    ];

    public async Task<object?> ExecuteAsync(string operation, JsonElement arguments, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (operation is not ("config_history" or "config_preview") &&
            System.Windows.Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
            return await (await dispatcher.InvokeAsync(() => ExecuteAsync(operation, arguments, ct)));
        using var attribution = ConfigWriteScope.Fallback("mcp:" + operation);
        switch (operation)
        {
            case "config_history": return new { versions = (history?.Invoke() ?? ConfigManager.History).Read()
                    .Reverse().Take(Math.Clamp(ConfigurationAutomation.Integer(arguments, "limit", 50), 1, 200)).ToArray() };
            case "config_restore": return changes.Restore((history?.Invoke() ?? ConfigManager.History)
                .RestoreSnapshot(arguments.GetProperty("version").GetInt64()), "restore:history", ConfigurationAutomation.Boolean(arguments, "requireIdle"));
            case "config_preview": return configuration.Preview(JsonNode.Parse(arguments.GetProperty("operations").GetRawText())!.AsArray());
            case "config_lease":
                switch (Required(arguments, "action"))
                {
                    case "list": return new { leases = changes.Leases(), changes.PersistenceHealthy };
                    case "start":
                        if (!DateTimeOffset.TryParse(Required(arguments, "expiresUtc"), out var expiry)) throw new ArgumentException("expiresUtc must be an ISO timestamp");
                        var lease = changes.CreateLease(Required(arguments, "pointer"), JsonNode.Parse(arguments.GetProperty("value").GetRawText()),
                            expiry.UtcDateTime, ConfigurationAutomation.String(arguments, "label") ?? "", ConfigurationAutomation.Boolean(arguments, "applyWhenIdle"));
                        return new { lease, changes.PersistenceHealthy };
                    case "keep": changes.KeepLease(Required(arguments, "id")); return new { kept = true, changes.PersistenceHealthy };
                    default: throw new ArgumentException("Unknown lease action");
                }
            case "config_experiment":
                return Required(arguments, "action") switch
                {
                    "list" => new { experiments = changes.Experiments(), changes.PersistenceHealthy },
                    "start" => changes.StartExperiment(Required(arguments, "label"), Required(arguments, "hypothesis")),
                    "keep" => changes.EndExperiment(Required(arguments, "id"), false),
                    "revert" => changes.EndExperiment(Required(arguments, "id"), true),
                    _ => throw new ArgumentException("Unknown experiment action")
                };
            default: throw new ArgumentException("Unknown configuration history operation");
        }
    }

    private static string Required(JsonElement args, string name) => ConfigurationAutomation.String(args, name)
        ?? throw new ArgumentException(name + " is required");
}
