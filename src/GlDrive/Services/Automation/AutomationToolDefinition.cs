using System.Text.Json;

namespace GlDrive.Services.Automation;

public sealed record AutomationToolDefinition(string Name, string Description, JsonElement InputSchema,
    bool ReadOnly = false, bool Destructive = false)
{
    public static AutomationToolDefinition Create(string name, string description, object schema,
        bool readOnly = false, bool destructive = false) =>
        new(name, description, JsonSerializer.SerializeToElement(schema), readOnly, destructive);

    public object ToProtocol() => new
    {
        name = Name, description = Description, inputSchema = InputSchema,
        annotations = new { readOnlyHint = ReadOnly, destructiveHint = Destructive,
            idempotentHint = ReadOnly, openWorldHint = true }
    };
}

public interface IAutomationModule
{
    IReadOnlyList<AutomationToolDefinition> Tools { get; }
    Task<object?> ExecuteAsync(string operation, JsonElement arguments, CancellationToken ct);
}
