using System.Text.Json;
using GlDrive.Services.Hooks;

namespace GlDrive.Services.Automation;

public sealed class HookAutomation(Func<HookRunner> runner) : IAutomationModule
{
    public IReadOnlyList<AutomationToolDefinition> Tools { get; } =
    [
        AutomationToolDefinition.Create("hooks.status", "Read recent local hook outcomes and dropped event count. Commands are configured only by direct appsettings edit and restart; automation cannot create, enable or run executable hooks.",
            new { type = "object", properties = new { }, additionalProperties = false }, readOnly: true)
    ];
    public Task<object?> ExecuteAsync(string operation, JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (operation != "hooks.status") throw new ArgumentException("Unknown hook operation");
        var hooks = runner();
        return Task.FromResult<object?>(new { recent = hooks.Recent(), dropped = hooks.Dropped });
    }
}
