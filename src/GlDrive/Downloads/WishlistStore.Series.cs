using System.Text.Json;

namespace GlDrive.Downloads;

public partial class WishlistStore
{
    internal bool TryBeginSeriesRefresh(string itemId, DateTimeOffset now, out int tvMazeId)
    {
        tvMazeId = 0;
        lock (_state.Sync)
        {
            EnsureLoaded();
            var item = _state.Items.FirstOrDefault(i => i.Id == itemId);
            if (item == null || !SeriesMonitorPolicy.IsMonitored(item) || item.TvMazeId is not > 0 ||
                !SeriesInventoryRefresher.IsDue(item.SeriesInventoryLastAttemptAtUtc, now)) return false;
            var previous = item.SeriesInventoryLastAttemptAtUtc;
            item.SeriesInventoryLastAttemptAtUtc = now;
            try { _state.Write(JsonSerializer.Serialize(_state.Items, JsonOptions)); }
            catch { item.SeriesInventoryLastAttemptAtUtc = previous; throw; }
            tvMazeId = item.TvMazeId.Value;
            return true;
        }
    }

    internal bool ApplySeriesInventory(string itemId, int tvMazeId, IReadOnlyList<SeriesEpisode> inventory, DateTimeOffset now)
    {
        if (inventory.Count == 0 || inventory.Count > SeriesInventoryRefresher.MaximumEpisodes) return false;
        lock (_state.Sync)
        {
            EnsureLoaded();
            var item = _state.Items.FirstOrDefault(i => i.Id == itemId);
            if (item == null || !SeriesMonitorPolicy.IsMonitored(item) || item.TvMazeId != tvMazeId) return false;
            var previousInventory = item.SeriesInventory;
            var previousUpdated = item.SeriesInventoryUpdatedAtUtc;
            item.SeriesInventory = inventory.Select(SeriesInventoryRefresher.Clone).ToList();
            item.SeriesInventoryUpdatedAtUtc = now;
            try { _state.Write(JsonSerializer.Serialize(_state.Items, JsonOptions)); }
            catch
            {
                item.SeriesInventory = previousInventory;
                item.SeriesInventoryUpdatedAtUtc = previousUpdated;
                throw;
            }
            _state.ReleaseEvaluations.ForgetItem(item.Id);
        }
        _state.NotifyChanged();
        return true;
    }
}
