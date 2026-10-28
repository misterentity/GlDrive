using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using GlDrive.Downloads;

namespace GlDrive.UI;

public partial class DashboardViewModel
{
    private FeedViewOption? _selectedFeedView;
    private string _feedViewName = "";
    private string _feedViewStatus = "";
    private int _feedRefreshQueued;
    public ObservableCollection<FeedViewOption> FeedViews { get; } = [];
    public FeedViewOption? SelectedFeedView
    {
        get => _selectedFeedView;
        set
        {
            if (ReferenceEquals(_selectedFeedView, value)) return;
            _selectedFeedView = value;
            OnPropertyChanged();
            ApplySavedFeedView();
        }
    }
    public string FeedViewName { get => _feedViewName; set { _feedViewName = value; OnPropertyChanged(); } }
    public string FeedViewStatus { get => _feedViewStatus; private set { _feedViewStatus = value; OnPropertyChanged(); } }
    public ICommand SaveFeedViewCommand { get; private set; } = null!;
    public ICommand RemoveFeedViewCommand { get; private set; } = null!;
    public ICommand MarkFeedViewReadCommand { get; private set; } = null!;
    public ICommand MuteFeedCategoryCommand { get; private set; } = null!;
    public ICommand ClearFeedMutesCommand { get; private set; } = null!;

    private void InitializeFeedViews()
    {
        SaveFeedViewCommand = new RelayCommand(() =>
        {
            try
            {
                var id = FeedViews.FirstOrDefault(view => view.View.Name == FeedViewName.Trim())?.View.Id;
                var saved = _notificationStore.FeedViews.Save(id, FeedViewName, NotificationFilterText,
                    NotificationFilterCategory, NotificationFilterServer);
                RefreshFeedViewCounts();
                SelectedFeedView = FeedViews.FirstOrDefault(v => v.View.Id == saved.Id);
                FeedViewStatus = "View saved. Unread counts cover retained, visible feed items.";
            }
            catch (ArgumentException ex) { FeedViewStatus = ex.Message; }
        });
        RemoveFeedViewCommand = new RelayCommand(() =>
        {
            if (SelectedFeedView is not { } selected) return;
            _notificationStore.FeedViews.Remove(selected.View.Id);
            FeedViewStatus = "Saved view removed; feed items are kept.";
        });
        MarkFeedViewReadCommand = new RelayCommand(() =>
        {
            if (SelectedFeedView is not { } selected) { FeedViewStatus = "Save or select a view first."; return; }
            _notificationStore.FeedViews.MarkRead(selected.View.Id, _notificationStore.Items, _notifications.Items.Select(item => item.Id));
            FeedViewStatus = "Current visible items in the saved view marked read.";
        });
        MuteFeedCategoryCommand = new RelayCommand(() =>
        {
            if (SelectedNotificationItem is not { } item) return;
            try
            {
                _notificationStore.FeedViews.SetMuted(item.ServerId, item.Category, true);
                FeedViewStatus = "Category hidden for this server in the feed. Wishlist, races and alerts are unchanged.";
            }
            catch (ArgumentException ex) { FeedViewStatus = ex.Message; }
        });
        ClearFeedMutesCommand = new RelayCommand(() =>
        {
            _notificationStore.FeedViews.ClearMutes();
            FeedViewStatus = "All feed categories are visible again.";
        });
        _notificationStore.FeedViews.Changed += FeedPreferencesChanged;
        _notificationStore.Changed += FeedNotificationChanged;
        _notifications.SetMutedCategories(_notificationStore.FeedViews.MutedCategories);
        RefreshFeedViewCounts();
    }

    private void ApplySavedFeedView()
    {
        if (SelectedFeedView is not { } selected) return;
        FeedViewName = selected.View.Name;
        _notificationFilterText = selected.View.Text;
        _notificationFilterCategory = selected.View.Category;
        _notificationFilterServer = selected.View.ServerName;
        OnPropertyChanged(nameof(NotificationFilterText));
        OnPropertyChanged(nameof(NotificationFilterCategory));
        OnPropertyChanged(nameof(NotificationFilterServer));
        ApplyNotificationFilter();
    }

    private void RefreshFeedViewCounts()
    {
        var counts = _notificationStore.FeedViews.Counts(_notificationStore.Items);
        var ids = counts.Select(c => c.View.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var stale in FeedViews.Where(v => !ids.Contains(v.View.Id)).ToArray()) FeedViews.Remove(stale);
        foreach (var count in counts)
        {
            var existing = FeedViews.FirstOrDefault(v => v.View.Id == count.View.Id);
            if (existing == null) FeedViews.Add(new(count)); else existing.Update(count);
        }
        if (_selectedFeedView != null && !ids.Contains(_selectedFeedView.View.Id)) SelectedFeedView = null;
    }

    private void FeedPreferencesChanged() => DispatchFeed(() =>
    {
        RefreshFeedViewCounts();
        ApplySavedFeedView();
        _notifications.SetMutedCategories(_notificationStore.FeedViews.MutedCategories);
        if (SelectedNotificationItem != null && !_notifications.Items.Contains(SelectedNotificationItem)) SelectedNotificationItem = null;
    });

    private void FeedNotificationChanged(NotificationItem? item)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || _disposed || Interlocked.Exchange(ref _feedRefreshQueued, 1) != 0) return;
        dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref _feedRefreshQueued, 0);
            if (_disposed) return;
            // Events are only wakeups. Re-read ordered retained state so an add queued
            // before Clear cannot resurrect a deleted row when the dispatcher drains.
            _notifications.Synchronize(_notificationStore.Items.Select(NotificationVm));
            if (SelectedNotificationItem != null && !_notifications.Items.Contains(SelectedNotificationItem)) SelectedNotificationItem = null;
            RefreshFeedViewCounts();
        });
    }

    private void DispatchFeed(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || _disposed) return;
        if (dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(() => { if (!_disposed) action(); });
    }

    private static NotificationItemVm NotificationVm(NotificationItem item) => new()
    {
        Id = item.Id, ServerId = item.ServerId, ServerName = item.ServerName, Category = item.Category,
        ReleaseName = item.ReleaseName, RemotePath = item.RemotePath, TimeDisplay = item.Timestamp.ToLocalTime().ToString("g")
    };
}

public sealed class FeedViewOption(FeedViewCount count) : INotifyPropertyChanged
{
    public FeedView View { get; private set; } = count.View;
    public int Unread { get; private set; } = count.Unread;
    public string Display => $"{View.Name} ({Unread} unread)";
    internal void Update(FeedViewCount current)
    {
        View = current.View;
        Unread = current.Unread;
        PropertyChanged?.Invoke(this, new(nameof(Display)));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
