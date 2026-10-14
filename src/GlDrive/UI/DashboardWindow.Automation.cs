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

    private static RoutedEvent SelectorEvent() => System.Windows.Controls.Primitives.Selector.SelectionChangedEvent;
}
