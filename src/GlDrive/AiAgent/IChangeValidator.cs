namespace GlDrive.AiAgent;

public sealed record ValidationResult(bool Ok, string? RejectionReason, Action<GlDrive.Config.AppConfig>? Mutate);

public interface IChangeValidator
{
    string Category { get; }
    ValidationResult Validate(AgentChange change, GlDrive.Config.AppConfig config);
}

internal static class ChangeValueJson
{
    /// <summary>
    /// For reading a change's object-valued `after`. The model writes the config's own camelCase
    /// keys and string enums ("action": "Deny"); default options are case-sensitive PascalCase, so
    /// every field silently defaulted and appended rules/mappings failed as empty.
    /// </summary>
    internal static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    internal static T? Read<T>(object? after) =>
        System.Text.Json.JsonSerializer.Deserialize<T>(System.Text.Json.JsonSerializer.Serialize(after), Options);
}
