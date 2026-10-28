using System.Text.Json;
using GlDrive.Config;
using GlDrive.Plex;

namespace GlDrive.Services.Automation;

public sealed class WatchlistAutomation(AppConfig config, Func<PlexWatchlistSync> sync) : IAutomationModule
{
    public IReadOnlyList<AutomationToolDefinition> Tools { get; } =
    [
        AutomationToolDefinition.Create("plex.watchlist_status", "Read opt-in Plex Watchlist synchronization status. No network call or Plex write.",
            new { type = "object", properties = new { }, additionalProperties = false }, readOnly: true),
        AutomationToolDefinition.Create("plex.watchlist_sync", "Import Plex Watchlist titles now when plex.watchlistSyncEnabled is true. One-way, GET-only provider reads; preserves manual wishlist choices and completes imports only from positive ownership evidence. Never writes to Plex.",
            new { type = "object", properties = new { }, additionalProperties = false })
    ];

    public async Task<object?> ExecuteAsync(string operation, JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var service = sync();
        return operation switch
        {
            "plex.watchlist_status" => new { enabled = config.Plex.WatchlistSyncEnabled, intervalMinutes = config.Plex.WatchlistSyncMinutes, last = service.LastResult },
            "plex.watchlist_sync" => await service.SyncAsync(force: true, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentException("Unknown watchlist operation")
        };
    }
}
