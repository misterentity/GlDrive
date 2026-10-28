using System.Windows.Input;
using System.Windows.Threading;
using GlDrive.Downloads;
using GlDrive.Services;

namespace GlDrive.UI;

public partial class DashboardViewModel
{
    private readonly DeferredBulkAction _bulkAction = new();
    private DispatcherTimer? _bulkTimer;
    private string _bulkStatus = "";
    private QualityProfile _bulkWishlistQuality;
    public IEnumerable<WishlistItemVm>? SelectedWishlistItems { get; set; }
    public IEnumerable<NotificationItemVm>? SelectedNotificationItems { get; set; }
    public QualityProfile[] WishlistQualities { get; } = Enum.GetValues<QualityProfile>();
    public QualityProfile BulkWishlistQuality
    {
        get => _bulkWishlistQuality;
        set { _bulkWishlistQuality = value; OnPropertyChanged(); }
    }
    public string BulkStatus { get => _bulkStatus; private set { _bulkStatus = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasBulkStatus)); } }
    public bool HasBulkStatus => _bulkStatus.Length > 0;
    public bool HasPendingBulk => _bulkAction.Description != null;
    public ICommand UndoBulkCommand { get; private set; } = null!;
    public ICommand SetWishlistQualityCommand { get; private set; } = null!;
    public ICommand SetWishlistStatusCommand { get; private set; } = null!;
    public ICommand RemoveSelectedNotificationsCommand { get; private set; } = null!;

    private void InitializeBulkActions()
    {
        _bulkWishlistQuality = WishlistQuality.Parse(_config.Downloads.QualityDefault);
        UndoBulkCommand = new RelayCommand(() =>
        {
            if (_bulkAction.Undo()) BulkStatus = "Removal cancelled; no rows were removed.";
            OnPropertyChanged(nameof(HasPendingBulk));
        });
        SetWishlistQualityCommand = new RelayCommand(() =>
        {
            var ids = WishlistSelection();
            var custom = _wishlistStore.Items.Count(item => ids.Contains(item.Id) && item.ProfileId != null &&
                item.ProfileId != ScoredQualityProfile.Legacy(item.Quality).Id);
            var changed = _wishlistStore.UpdateMany(ids, quality: BulkWishlistQuality);
            RefreshWishlist();
            BulkStatus = $"Updated resolution for {changed} wishlist items. Existing copies and source permissions are preserved." +
                (custom > 0 ? $" {custom} custom quality profiles remain in effect." : "");
        });
        SetWishlistStatusCommand = new RelayCommand<string>(value =>
        {
            if (!Enum.TryParse<WishlistStatus>(value, out var status) || !Enum.IsDefined(status)) return;
            var changed = _wishlistStore.UpdateMany(WishlistSelection(), status: status);
            RefreshWishlist(); BulkStatus = $"Updated {changed} wishlist items to {status}.";
        });
        RemoveSelectedNotificationsCommand = new RelayCommand(() => ScheduleNotificationRemoval(NotificationSelection().Select(item => item.Id).ToArray()));
        _bulkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _bulkTimer.Tick += (_, _) => TickBulkAction();
        // A dashboard opened by automation must not retain that request's origin
        // in recurring dispatcher callbacks after the request has completed.
        if (ExecutionContext.IsFlowSuppressed()) _bulkTimer.Start();
        else { using (ExecutionContext.SuppressFlow()) _bulkTimer.Start(); }
    }

    public bool TryScheduleBulk(string description, Action apply)
    {
        if (_disposed) return false;
        if (!_bulkAction.TrySchedule(description, DateTimeOffset.UtcNow, () =>
            { if (!_disposed) { BulkStatus = "Removal finished. See the affected list for the result."; apply(); } }))
        {
            BulkStatus = "Another removal is pending. Undo it or wait for it to finish before starting another.";
            return false;
        }
        UpdateBulkCountdown();
        return true;
    }

    private void TickBulkAction()
    {
        if (_disposed) return;
        try { _bulkAction.RunDue(DateTimeOffset.UtcNow); }
        catch { BulkStatus = "The removal could not finish. Current rows are retained where possible; it will not be retried automatically."; }
        UpdateBulkCountdown();
    }

    private void UpdateBulkCountdown()
    {
        if (_bulkAction.Description is { } description && _bulkAction.DueAt is { } due)
            BulkStatus = $"{description} in {Math.Max(0, (int)Math.Ceiling((due - DateTimeOffset.UtcNow).TotalSeconds))} seconds. Undo cancels this removal.";
        OnPropertyChanged(nameof(HasPendingBulk));
    }

    private string[] WishlistSelection() => (SelectedWishlistItems?.ToArray() is { Length: > 0 } selected
        ? selected : SelectedWishlistItem is { } single ? [single] : []).Select(item => item.Id).Distinct(StringComparer.Ordinal).ToArray();
    private NotificationItemVm[] NotificationSelection() => (SelectedNotificationItems?.ToArray() is { Length: > 0 } selected
        ? selected : SelectedNotificationItem is { } single ? [single] : []).DistinctBy(item => item.Id).ToArray();

    private void ScheduleWishlistRemoval()
    {
        var ids = WishlistSelection();
        if (ids.Length == 0) return;
        TryScheduleBulk($"Remove {ids.Length} wishlist items", () =>
        {
            var removed = _wishlistStore.RemoveMany(ids);
            RefreshWishlist(); BulkStatus = $"Removed {removed} wishlist items. Imported watchlist entries remain inactive to prevent re-import.";
        });
    }

    private void ToggleWishlistSelection()
    {
        var ids = WishlistSelection();
        if (ids.Length == 0) return;
        var selected = _wishlistStore.Items.Where(item => ids.Contains(item.Id)).ToArray();
        var status = selected.All(item => item.Status == WishlistStatus.Paused) ? WishlistStatus.Watching : WishlistStatus.Paused;
        var updated = _wishlistStore.UpdateMany(ids, status: status);
        RefreshWishlist(); BulkStatus = $"Updated {updated} wishlist items to {status}.";
    }

    private void ScheduleNotificationRemoval(string[] ids)
    {
        ids = ids.Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0) return;
        TryScheduleBulk($"Remove {ids.Length} feed items", () =>
        {
            var removed = _notificationStore.RemoveMany(ids);
            _notifications.Synchronize(_notificationStore.Items.Select(NotificationVm));
            RefreshFeedViewCounts();
            if (SelectedNotificationItem != null && !NotificationItems.Contains(SelectedNotificationItem)) SelectedNotificationItem = null;
            BulkStatus = $"Removed {removed} feed items. New arrivals, wishlist, races and alerts are unchanged.";
        });
    }

    private void ScheduleFinishedDownloadRemoval(DownloadStatus? status = null)
    {
        var selected = _serverManager.GetDownloadQueues().SelectMany(queue => queue.Manager.Store.Items
            .Where(item => item.Status is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Cancelled &&
                (!status.HasValue || item.Status == status.Value))
            .Select(item => (queue.Id, item.Id))).ToArray();
        ScheduleDownloadHistoryRemoval(selected);
    }

    private void ScheduleSelectedDownloadRemoval()
    {
        var selected = SelectedDownloadItems?.ToArray() ?? [];
        if (selected.Length == 0 && SelectedDownloadItem is { } item) selected = [item];
        ScheduleDownloadHistoryRemoval(selected.Select(item => (item.ServerId, item.Id)).ToArray());
    }

    private void ScheduleDownloadHistoryRemoval((string ServerId, string Id)[] selected)
    {
        if (selected.Length == 0) return;
        TryScheduleBulk($"Remove terminal history from {selected.Length} selected downloads", () =>
        {
            var removed = selected.GroupBy(item => item.ServerId).Sum(group =>
                _serverManager.GetDownloadManager(group.Key)?.RemoveTerminalHistory(group.Select(item => item.Id)) ?? 0);
            RefreshDownloads();
            BulkStatus = $"Removed {removed} history rows; skipped {selected.Length - removed} nonterminal, active or unavailable rows. Local files were kept.";
        });
    }
}
