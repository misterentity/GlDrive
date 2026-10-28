using System.Windows.Input;
using Serilog;

namespace GlDrive.UI;

public partial class SpreadViewModel
{
    private readonly HashSet<string> _selectedQueuedRequestIds = new(StringComparer.Ordinal);
    public IEnumerable<QueuedRaceVm> SelectedQueuedRaces
    {
        get => QueuedRaces.Where(row => _selectedQueuedRequestIds.Contains(row.RequestId)).ToArray();
        set
        {
            _selectedQueuedRequestIds.Clear();
            foreach (var row in value ?? [])
                if (QueuedRaces.Contains(row) && row.RequestId.Length > 0) _selectedQueuedRequestIds.Add(row.RequestId);
        }
    }

    internal void RefreshQueuedRows(IReadOnlyList<QueuedRaceVm> rows)
    {
        var retained = rows.Select(row => row.RequestId).ToHashSet(StringComparer.Ordinal);
        _selectedQueuedRequestIds.IntersectWith(retained);
        for (var index = QueuedRaces.Count - 1; index >= 0; index--)
            if (!retained.Contains(QueuedRaces[index].RequestId)) QueuedRaces.RemoveAt(index);
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var existing = QueuedRaces.FirstOrDefault(current => current.RequestId == row.RequestId);
            if (existing == null) QueuedRaces.Insert(index, row);
            else
            {
                existing.UpdateFrom(row);
                var oldIndex = QueuedRaces.IndexOf(existing);
                if (oldIndex != index) QueuedRaces.Move(oldIndex, index);
            }
        }
    }
    private Func<string, Action, bool>? _scheduleBulk;
    private bool _queueBulkBusy;
    public ICommand RemoveSelectedQueuedRacesCommand { get; private set; } = null!;
    public ICommand AddSelectedQueuedSitesCommand { get; private set; } = null!;
    public ICommand RemoveSelectedQueuedSitesCommand { get; private set; } = null!;
    public void SetBulkScheduler(Func<string, Action, bool> schedule) => _scheduleBulk = schedule;

    private void InitializeBulkQueueActions()
    {
        RemoveSelectedQueuedRacesCommand = new RelayCommand(() =>
        {
            var ids = (SelectedQueuedRaces ?? []).Select(race => race.RequestId).Where(id => id.Length > 0).Distinct().ToArray();
            if (ids.Length == 0) { SpreadStatus = "Select queued requests first."; return; }
            if (_scheduleBulk == null) { SpreadStatus = "Open the dashboard to use removal with Undo."; return; }
            _scheduleBulk($"Remove {ids.Length} queued race requests", () => _ = RunQueueBulkAsync(() =>
            {
                var result = _serverManager.Spread?.RemoveQueuedRequests(ids);
                return result == null ? "Spread engine unavailable; nothing removed." :
                    $"Removed {result.Removed}; skipped {result.Skipped} started, replaced, busy or owned-partial requests. No content deleted.";
            }));
        });
        AddSelectedQueuedSitesCommand = new RelayCommand(() => EditSelectedQueueSites(true));
        RemoveSelectedQueuedSitesCommand = new RelayCommand(() => EditSelectedQueueSites(false));
    }

    private void EditSelectedQueueSites(bool add)
    {
        var selected = (SelectedQueuedRaces ?? []).Where(race => race.RequestId.Length > 0).DistinctBy(race => race.RequestId).ToArray();
        var site = SelectedQueuedSite;
        if (selected.Length == 0 || site == null) { SpreadStatus = "Select queued requests and a site first."; return; }
        Task ApplyAsync() => RunQueueBulkAsync(() =>
        {
            var result = _serverManager.Spread?.EditQueuedRaceSites(selected.Select(race => race.RequestId), site.Id, add);
            return result == null ? "Spread engine unavailable; nothing changed." :
                $"Updated {result.Changed} queued requests; skipped {result.Skipped} changed or ineligible requests." +
                (result.Saved ? "" : " Changes are in memory; the local queue file could not be saved.");
        });
        // Removing membership is deferred as a destructive list edit; adding remains immediate.
        if (add) _ = ApplyAsync();
        else if (_scheduleBulk != null) _scheduleBulk($"Remove the selected site from {selected.Length} queued races", () => _ = ApplyAsync());
    }

    private async Task RunQueueBulkAsync(Func<string> apply)
    {
        if (_disposed) return;
        if (_queueBulkBusy) { SpreadStatus = "Another queue update is still running."; return; }
        _queueBulkBusy = true;
        try
        {
            var status = await Task.Run(apply);
            if (_disposed) return;
            SpreadStatus = status;
            SafeRefresh();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Bulk queue update failed");
            if (!_disposed) SpreadStatus = "Queue update failed; refresh and try again.";
        }
        finally { _queueBulkBusy = false; }
    }
}
