using System.Text.Json;

namespace GlDrive.Services.Automation;

/// <summary>Read-only local health evidence. Fixes are available only in the local Problems window.</summary>
public sealed class ProblemAutomation(Func<ProblemsService> service) : IAutomationModule
{
    public IReadOnlyList<AutomationToolDefinition> Tools { get; } =
    [
        AutomationToolDefinition.Create("problems.list", "Read cached standing health conditions, evidence and manual remedies. Checks use existing runtime state and local drive roots only. No FTP, provider calls, configuration changes or certificate decisions. Partial=true means some evidence was unavailable or incomplete. Bounded=true means frozen-decision evidence covers the normal recent audit window.",
            new { type = "object", properties = new { }, additionalProperties = false }, readOnly: true)
    ];

    public Task<object?> ExecuteAsync(string operation, JsonElement arguments, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (operation != "problems.list") throw new ArgumentException("Unknown problems operation");
        return Task.FromResult<object?>(service().Snapshot);
    }
}
