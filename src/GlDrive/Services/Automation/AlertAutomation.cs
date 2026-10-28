using System.Text.Json;
using GlDrive.Services.Alerts;

namespace GlDrive.Services.Automation;

public sealed class AlertAutomation(Func<AlertRouter> router) : IAutomationModule
{
    public IReadOnlyList<AutomationToolDefinition> Tools { get; } =
    [
        AutomationToolDefinition.Create("alerts.list", "Read local alert history (separate from the release feed). Off until alerts.enabled is enabled.",
            new { type = "object", properties = new { limit = new { type = "integer", minimum = 1, maximum = 500 } }, additionalProperties = false }, readOnly: true),
        AutomationToolDefinition.Create("alerts.digest", "Build a deterministic local 24-hour alert digest. No model or network call.",
            new { type = "object", properties = new { }, additionalProperties = false }, readOnly: true),
        AutomationToolDefinition.Create("alerts.update", "Acknowledge or snooze an existing local inbox item. Does not execute its suggested actions.",
            new { type = "object", properties = new { id = new { type = "string" }, acknowledged = new { type = "boolean" }, snoozeMinutes = new { type = "integer", minimum = 0, maximum = 10080 } }, required = new[] { "id" }, additionalProperties = false })
    ];

    public Task<object?> ExecuteAsync(string operation, JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var service = router();
        object result;
        switch (operation)
        {
            case "alerts.list": result = new { entries = service.Store.Snapshot().Reverse().Take(Math.Clamp(ConfigurationAutomation.Integer(arguments, "limit", 100), 1, 500)).ToArray(), dropped = service.Dropped }; break;
            case "alerts.digest": result = new { text = service.Digest(DateTimeOffset.UtcNow.AddDays(-1)) }; break;
            case "alerts.update":
                var minutes = ConfigurationAutomation.Integer(arguments, "snoozeMinutes", 0);
                result = new { updated = service.Store.Update(ConfigurationAutomation.String(arguments, "id") ?? "",
                    arguments.TryGetProperty("acknowledged", out _) ? ConfigurationAutomation.Boolean(arguments, "acknowledged") : null,
                    minutes > 0 ? DateTimeOffset.UtcNow.AddMinutes(minutes) : null,
                    updateSnooze: arguments.TryGetProperty("snoozeMinutes", out _)) };
                service.Store.Save();
                break;
            default: throw new ArgumentException("Unknown alert operation");
        }
        return Task.FromResult<object?>(result);
    }
}
