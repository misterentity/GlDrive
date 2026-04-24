using GlDrive.Config;

namespace GlDrive.AiAgent;

/// <summary>
/// Validator for the "blacklist" category.
/// The section blacklist is maintained by <see cref="GlDrive.Spread.SectionBlacklistStore"/> as
/// runtime-only state (file-backed at %AppData%\GlDrive\section-blacklist.json) and is not part of
/// AppConfig. Direct mutation from the AI agent is deferred until a Phase 8 integration that injects
/// SectionBlacklistStore into the ChangeApplier pipeline.
/// </summary>
public sealed class BlacklistValidator : IChangeValidator
{
    public string Category => AgentCategories.Blacklist;

    public ValidationResult Validate(AgentChange change, AppConfig config)
        => new(false, "blacklist-mutation-deferred-to-phase-8", null);
}
