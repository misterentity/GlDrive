using System.Text.Json;
using GlDrive.Services.Operations;

namespace GlDrive.Services.Automation;

/// <summary>Read-only local evidence joins. No operational action is exposed by these tools.</summary>
public sealed class OperationsAutomation : IAutomationModule
{
    private readonly Func<string, string?, int, CancellationToken, Task<ReleaseDossier>> _dossier;
    private readonly Func<string?, int, int, CancellationToken, Task<IReadOnlyList<OpsEvent>>> _timeline;
    private readonly Func<string?, int, int, CancellationToken, Task<IReadOnlyList<OpsIncident>>> _incidents;
    private readonly Func<(bool Healthy, long Dropped)> _journalStatus;

    public OperationsAutomation(Func<ReleaseDossierService> dossier, Func<OpsJournal> journal)
        : this((release, section, days, ct) => dossier().ReadAsync(release, section, days, ct),
            (server, days, limit, ct) => journal().ReadTimelineAsync(server, days, limit, ct),
            (server, days, limit, ct) => journal().ReadIncidentsAsync(server, days, limit, ct),
            () => { var current = journal(); return (current.PersistenceHealthy, current.Dropped); }) { }

    internal OperationsAutomation(Func<string, string?, int, CancellationToken, Task<ReleaseDossier>> dossier,
        Func<string?, int, int, CancellationToken, Task<IReadOnlyList<OpsEvent>>> timeline,
        Func<string?, int, int, CancellationToken, Task<IReadOnlyList<OpsIncident>>> incidents,
        Func<(bool Healthy, long Dropped)>? journalStatus = null)
    { _dossier = dossier; _timeline = timeline; _incidents = incidents; _journalStatus = journalStatus ?? (() => (true, 0)); }

    public IReadOnlyList<AutomationToolDefinition> Tools { get; } =
    [
        AutomationToolDefinition.Create("release.trace", "Join retained local observations for one exact release name and preview current local configuration gates. Bounded background reads; partial coverage and predictions are explicit. Missing events mean not observed, never absent from a site. This dry run cannot start a race, download, login, provider call or site search.",
            new { type = "object", properties = new { release = new { type = "string", minLength = 1, maxLength = 1024 }, section = new { type = "string", maxLength = 256 }, days = new { type = "integer", minimum = 1, maximum = 90 } }, required = new[] { "release" }, additionalProperties = false }, readOnly: true),
        AutomationToolDefinition.Create("ops.timeline", "Read bounded retained, redacted local operations, newest first. Oldest journal events can be evicted by retention limits; missing events do not prove an operation did not happen. No config or network work.",
            new { type = "object", properties = new { serverId = new { type = "string", maxLength = 256 }, days = new { type = "integer", minimum = 1, maximum = 90 }, limit = new { type = "integer", minimum = 1, maximum = 500 } }, additionalProperties = false }, readOnly: true),
        AutomationToolDefinition.Create("ops.incidents", "Read bounded local incident groups by exact server and nearby time, with retained context events and up to 10 preceding configuration, update or agent changes in the same server or global scope. Grouping describes correlation in time, not a proven common cause. No operational actions.",
            new { type = "object", properties = new { serverId = new { type = "string", maxLength = 256 }, days = new { type = "integer", minimum = 1, maximum = 90 }, limit = new { type = "integer", minimum = 1, maximum = 100 } }, additionalProperties = false }, readOnly: true)
    ];

    public async Task<object?> ExecuteAsync(string operation, JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (operation is not ("release.trace" or "ops.timeline" or "ops.incidents")) throw new ArgumentException("Unknown operations query");
        if (arguments.ValueKind != JsonValueKind.Object) throw new ArgumentException("Query arguments must be an object");
        if (operation == "release.trace")
        {
            var release = Text(arguments, "release", 1024, required: true)!;
            var section = Text(arguments, "section", 256);
            var days = Integer(arguments, "days", 30, 90);
            return await _dossier(release, section, days, cancellationToken).ConfigureAwait(false);
        }
        var server = Text(arguments, "serverId", 256);
        var window = Integer(arguments, "days", 7, 90);
        var limit = Integer(arguments, "limit", operation == "ops.timeline" ? 200 : 50, operation == "ops.timeline" ? 500 : 100);
        if (operation == "ops.timeline")
        {
            var events = await _timeline(server, window, limit, cancellationToken).ConfigureAwait(false);
            var status = _journalStatus();
            var coverage = events as IOpsReadCoverage;
            return new { events, persistenceHealthy = status.Healthy, dropped = status.Dropped,
                partial = coverage?.Partial ?? false, limits = coverage?.Limits ?? [] };
        }
        var incidents = await _incidents(server, window, limit, cancellationToken).ConfigureAwait(false);
        var incidentStatus = _journalStatus();
        var incidentCoverage = incidents as IOpsReadCoverage;
        return new { incidents, persistenceHealthy = incidentStatus.Healthy, dropped = incidentStatus.Dropped,
            partial = incidentCoverage?.Partial ?? false, limits = incidentCoverage?.Limits ?? [] };
    }

    private static string? Text(JsonElement arguments, string name, int maximum, bool required = false)
    {
        if (!arguments.TryGetProperty(name, out var value))
        {
            if (required) throw new ArgumentException($"{name} is required");
            return null;
        }
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text || text.Length > maximum)
            throw new ArgumentException($"{name} must be a string of at most {maximum} characters");
        text = text.Trim();
        if (text.Length != 0) return text;
        if (required) throw new ArgumentException($"{name} is required");
        return null;
    }

    private static int Integer(JsonElement arguments, string name, int fallback, int maximum)
    {
        if (!arguments.TryGetProperty(name, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number < 1 || number > maximum)
            throw new ArgumentException($"{name} must be an integer from 1 to {maximum}");
        return number;
    }
}
