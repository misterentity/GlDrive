using System.Text.Json;

namespace GlDrive.Services.Automation;

/// <summary>Reads bounded, already-redacted audit metadata without waiting for its disk writer.</summary>
public sealed class AutomationLedgerAutomation(Func<AutomationCallLedger?> getLedger) : IAutomationModule
{
    public IReadOnlyList<AutomationToolDefinition> Tools { get; } =
    [
        AutomationToolDefinition.Create("automation_ledger",
            "Read recent local automation call events. Arguments contain only redacted structure and types; client identifiers are stable hashes. Storage failures never stop operations.",
            new { type = "object", properties = new { limit = new { type = "integer", minimum = 1, maximum = 500, @default = 200 } }, additionalProperties = false },
            readOnly: true)
    ];

    public Task<object?> ExecuteAsync(string operation, JsonElement arguments, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (operation != "automation_ledger") throw new ArgumentException("Unknown ledger operation");
        var limit = AutomationService.Integer(arguments, "limit", 200, 1, 500);
        var ledger = getLedger();
        return Task.FromResult<object?>(new
        {
            entries = ledger?.Snapshot(limit) ?? [],
            loading = ledger != null && !ledger.Ready.IsCompleted,
            persistenceHealthy = ledger?.PersistenceHealthy ?? false,
            dropped = ledger?.Dropped ?? 0
        });
    }
}
