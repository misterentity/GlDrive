using System.Text.Json;
using GlDrive.Downloads;

namespace GlDrive.Services.Automation;

/// <summary>Explicit bounded provider refresh and normal wishlist additions from the current collection.</summary>
public sealed class CollectionAutomation(Func<CollectionGapService> service) : IAutomationModule
{
    public IReadOnlyList<AutomationToolDefinition> Tools { get; } =
    [
        AutomationToolDefinition.Create("collections.status", "Read the last collection comparison without file or network work. States include owned, downloading, wanted, missing and unknown; missing requires a complete local index. Partial collection responses contain only known members.",
            new { type = "object", properties = new { }, additionalProperties = false }, readOnly: true),
        AutomationToolDefinition.Create("collections.refresh", "Explicitly look up one movie's TMDB collection and compare its films with the existing local library index, downloads and wishlist. At most two bounded TMDB GETs; no background polling or FTP work. Offline or keyless results are unknown. Does not add films.",
            new { type = "object", properties = new { movieTmdbId = new { type = "integer", minimum = 1, maximum = int.MaxValue } }, required = new[] { "movieTmdbId" }, additionalProperties = false }),
        AutomationToolDefinition.Create("collections.add", "Explicitly add one missing, dated member of the current refreshed collection to the normal wishlist. Uses current default quality and preserves the movie source floor. Rechecks ownership, downloads and existing wanted identities; never reactivates existing items. Unknown ownership requires a successful library.index_refresh first; undated and unknown provider members are rejected.",
            new { type = "object", properties = new { movieTmdbId = new { type = "integer", minimum = 1, maximum = int.MaxValue }, filmTmdbId = new { type = "integer", minimum = 1, maximum = int.MaxValue } }, required = new[] { "movieTmdbId", "filmTmdbId" }, additionalProperties = false })
    ];

    public async Task<object?> ExecuteAsync(string operation, JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (operation == "collections.status") return service().Snapshot;
        if (operation is not ("collections.refresh" or "collections.add")) throw new ArgumentException("Unknown collection operation");
        var movieId = PositiveId(arguments, "movieTmdbId");
        if (operation == "collections.refresh")
            return await service().RefreshAsync(movieId, cancellationToken).ConfigureAwait(false);
        var filmId = PositiveId(arguments, "filmTmdbId");
        cancellationToken.ThrowIfCancellationRequested();
        return service().AddMissing(movieId, filmId);
    }

    internal static int PositiveId(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var id) || id <= 0)
            throw new ArgumentException($"{name} must be a positive integer TMDB ID");
        return id;
    }
}
