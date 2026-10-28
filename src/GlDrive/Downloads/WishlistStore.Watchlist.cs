using GlDrive.Plex;
using System.Text.Json;

namespace GlDrive.Downloads;

public sealed record WatchlistImportResult(int Added, int Existing);

public partial class WishlistStore
{
    internal WatchlistImportResult ImportWatchlistPage(IReadOnlyList<WishlistItem> incoming, Func<bool> enabled)
    {
        var added = 0; var existing = 0; var enriched = false;
        lock (_state.Sync)
        {
            EnsureLoaded();
            foreach (var item in incoming)
            {
                if (!enabled()) break;
                if (item.Source != PlexWatchlistSync.Source || string.IsNullOrWhiteSpace(item.PlexWatchlistId)) continue;
                var matches = _state.Items.Where(other => WishlistOwnership.SameIdentity(other, item)).ToArray();
                if (matches.Length > 0)
                {
                    existing++;
                    if (matches.Length == 1)
                    {
                        var old = matches[0];
                        // A known provider identity can supply missing identifiers, never
                        // replace user choices or convert a manual entry into an import.
                        if (old.Source == PlexWatchlistSync.Source || old.ImdbId != null && old.ImdbId == item.ImdbId ||
                            old.TmdbId.HasValue && old.TmdbId == item.TmdbId || old.TvdbId.HasValue && old.TvdbId == item.TvdbId)
                        {
                            if (old.ImdbId == null && item.ImdbId != null) { old.ImdbId = item.ImdbId; enriched = true; }
                            if (old.TmdbId == null && item.TmdbId != null) { old.TmdbId = item.TmdbId; enriched = true; }
                            if (old.TvdbId == null && item.TvdbId != null) { old.TvdbId = item.TvdbId; enriched = true; }
                        }
                    }
                    continue;
                }
                item.Id = Guid.NewGuid().ToString("N");
                NormalizeQualityState(item);
                _state.Items.Add(item);
                added++;
            }
            if (added > 0 || enriched) Save(); // Failed bookkeeping retains the new inbox in memory.
        }
        if (added > 0 || enriched) _state.NotifyChanged();
        return new(added, existing);
    }

    internal bool TryBeginWatchlistMetadata(string id, DateTimeOffset now, Func<bool> enabled)
    {
        lock (_state.Sync)
        {
            EnsureLoaded();
            var item = _state.Items.FirstOrDefault(i => i.Id == id);
            if (!enabled() || item == null || item.Source != PlexWatchlistSync.Source || item.Type != MediaType.TvShow ||
                item.Status != WishlistStatus.Watching || item.Dead || item.WatchlistMetadataCheckedAtUtc is { } checkedAt &&
                checkedAt <= now.AddDays(1) && now - checkedAt < TimeSpan.FromDays(1)) return false;
            item.WatchlistMetadataCheckedAtUtc = now;
            Save();
            return true;
        }
    }

    internal void ApplyWatchlistMetadata(string id, int tvMazeId, bool ended,
        IReadOnlyList<SeriesEpisode>? inventory, DateTimeOffset now, Func<bool> enabled)
    {
        var changed = false;
        lock (_state.Sync)
        {
            EnsureLoaded();
            var item = _state.Items.FirstOrDefault(i => i.Id == id);
            if (!enabled() || item == null || item.Source != PlexWatchlistSync.Source || item.Dead || item.Status != WishlistStatus.Watching) return;
            if (item.TvMazeId is { } previous && previous != tvMazeId) return;
            item.TvMazeId = tvMazeId;
            // Ended alone cannot prove the episode inventory was read completely.
            item.WatchlistSeriesEnded = ended && inventory is { Count: > 0 };
            if (inventory is { Count: > 0 })
            {
                item.SeriesInventory = inventory.Select(SeriesInventoryRefresher.Clone).ToList();
                item.SeriesInventoryUpdatedAtUtc = now;
            }
            Save();
            changed = true;
        }
        if (changed) _state.NotifyChanged();
    }

    internal int CompleteOwnedWatchlist(Func<WishlistItem, bool> owns, Func<bool> enabled)
    {
        (WishlistItem Snapshot, string Before)[] candidates;
        lock (_state.Sync)
        {
            EnsureLoaded();
            candidates = _state.Items.Where(item => item.Source == PlexWatchlistSync.Source &&
                    item.Status == WishlistStatus.Watching && !item.Dead)
                .Select(item => JsonSerializer.Serialize(item, JsonOptions))
                .Select(json => (JsonSerializer.Deserialize<WishlistItem>(json, JsonOptions)!, json)).ToArray();
        }
        // Ownership may parse and compare a large download/library snapshot. It must
        // never hold the lock used by synchronous announce processing.
        var owned = candidates.Where(candidate => enabled() && owns(candidate.Snapshot)).ToArray();
        var completed = 0;
        lock (_state.Sync)
        {
            foreach (var candidate in owned)
            {
                if (!enabled()) break;
                var item = _state.Items.FirstOrDefault(item => item.Id == candidate.Snapshot.Id);
                // A user edit, removal, metadata update or newly recorded outcome wins
                // over a result computed from an older snapshot.
                if (item == null || JsonSerializer.Serialize(item, JsonOptions) != candidate.Before) continue;
                item.Status = WishlistStatus.Completed;
                completed++;
            }
            if (completed > 0) Save();
        }
        if (completed > 0) _state.NotifyChanged();
        return completed;
    }
}
