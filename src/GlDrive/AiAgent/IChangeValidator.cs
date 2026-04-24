namespace GlDrive.AiAgent;

public sealed record ValidationResult(bool Ok, string? RejectionReason, Action<GlDrive.Config.AppConfig>? Mutate);

public interface IChangeValidator
{
    string Category { get; }
    ValidationResult Validate(AgentChange change, GlDrive.Config.AppConfig config);
}
