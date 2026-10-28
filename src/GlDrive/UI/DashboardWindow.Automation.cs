using System.Windows;
using System.Windows.Controls;

namespace GlDrive.UI;

public partial class DashboardWindow
{
    internal object GetAutomationModel(string screen)
    {
        Dispatcher.VerifyAccess();
        var dashboard = (DashboardViewModel)DataContext;
        if (screen == "dashboard") return dashboard;
        if (screen == "plex") return dashboard.Plex;
        var title = screen switch { "player" => "Player", "spread" => "Spread", "browse" => "Browse", _ => throw new ArgumentException("Unknown screen") };
        var tab = MainTabControl.Items.OfType<TabItem>().First(t => GetTabName(t) == title);
        MainTabControl.SelectedItem = tab;
        // ContentRendered may not have subscribed yet on a window opened by this request.
        TabControl_SelectionChanged(MainTabControl,
            new SelectionChangedEventArgs(SelectorEvent(), Array.Empty<object>(), new object[] { tab }) { Source = MainTabControl });
        return screen switch
        {
            "player" => _playerVm!, "spread" => _spreadVm!, "browse" => _browseVm!,
            _ => throw new ArgumentException("Unknown screen")
        };
    }

    internal void ApplyAutomationSelection(string screen, string property, object[] rows)
    {
        Dispatcher.VerifyAccess();
        var (title, controlName) = (screen, property) switch
        {
            ("dashboard", "SelectedWishlistItems") => ("Wishlist", "WishlistGrid"),
            ("dashboard", "SelectedNotificationItems") => ("Notifications", "NotificationsGrid"),
            ("dashboard", "SelectedDownloadItems") => ("Downloads", "DownloadsGrid"),
            ("spread", "SelectedQueuedRaces") => ("Spread", "QueuedRacesList"),
            _ => throw new ArgumentException("Unsupported bulk selection")
        };
        MainTabControl.SelectedItem = MainTabControl.Items.OfType<TabItem>().First(tab => GetTabName(tab) == title);
        var control = FindName(controlName) as ItemsControl ?? throw new InvalidOperationException("Selection control is unavailable");
        if (rows.Any(row => !control.Items.Contains(row))) throw new ArgumentException("A selected row is hidden; adjust the view before selecting");
        var selected = control switch
        {
            DataGrid grid => grid.SelectedItems,
            ListBox list => list.SelectedItems,
            _ => throw new InvalidOperationException("Control does not support bulk selection")
        };
        selected.Clear();
        foreach (var row in rows) selected.Add(row);
    }

    private static RoutedEvent SelectorEvent() => System.Windows.Controls.Primitives.Selector.SelectionChangedEvent;
}
