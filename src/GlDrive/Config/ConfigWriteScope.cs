namespace GlDrive.Config;

/// <summary>Attribution follows a save through UI, automation and asynchronous dispatch.</summary>
public sealed class ConfigWriteScope : IDisposable
{
    private static readonly AsyncLocal<ConfigWriteContext?> Context = new();
    private readonly ConfigWriteContext? _previous;
    public static ConfigWriteContext Current => Context.Value ?? new("application");

    private ConfigWriteScope(string actor, string? auditRef)
    {
        _previous = Context.Value;
        Context.Value = new(actor, auditRef);
    }

    public static ConfigWriteScope Begin(string actor, string? auditRef = null) => new(actor, auditRef);
    public static ConfigWriteScope Fallback(string actor, string? auditRef = null) => new(Context.Value?.Actor ?? actor, Context.Value?.AuditRef ?? auditRef);
    public void Dispose() => Context.Value = _previous;
}

public sealed record ConfigWriteContext(string Actor, string? AuditRef = null);
