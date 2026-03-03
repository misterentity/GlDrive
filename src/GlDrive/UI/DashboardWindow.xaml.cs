using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Services;
using Microsoft.Web.WebView2.Core;

namespace GlDrive.UI;

public partial class DashboardWindow : Window
{
    private readonly AppConfig _config;
    private readonly ServerManager _serverManager;
    private bool _upcomingLoaded;
    private bool _preDbLoaded;
    private Point _dragStartPoint;

    public DashboardWindow(ServerManager serverManager, AppConfig config, NotificationStore notificationStore)
    {
        _config = config;
        _serverManager = serverManager;
        InitializeComponent();
        var vm = new DashboardViewModel(serverManager, config, notificationStore);
        DataContext = vm;

        // Auto-scroll IRC messages
        vm.Irc.ScrollToBottom += () =>
        {
            if (IrcMessageList.Items.Count > 0)
                IrcMessageList.ScrollIntoView(IrcMessageList.Items[^1]);
        };

        // Re-focus input after sending
        vm.Irc.FocusInput += () => IrcInputBox.Focus();

        // Tab-complete nicks
        IrcInputBox.PreviewKeyDown += (s, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Tab)
            {
                e.Handled = true;
                if (vm.Irc.HandleTabComplete())
                    IrcInputBox.CaretIndex = IrcInputBox.Text?.Length ?? 0;
            }
            else
            {
                vm.Irc.ResetTabComplete();
            }
        };
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        // Find the TabControl and subscribe to selection changes
        if (Content is Grid grid && grid.Children[0] is TabControl tabControl)
        {
            tabControl.SelectionChanged += TabControl_SelectionChanged;
        }
    }

    private async void TabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source is not TabControl tc) return;
        if (tc.SelectedItem is not TabItem tab) return;
        var header = tab.Header?.ToString();

        if (header == "Upcoming" && !_upcomingLoaded)
        {
            _upcomingLoaded = true;
            if (DataContext is DashboardViewModel vm)
                await vm.LoadUpcoming();
        }
        else if (header == "PreDB" && !_preDbLoaded)
        {
            _preDbLoaded = true;
            await InitPreDbBrowser();
        }
    }

    private async Task InitPreDbBrowser()
    {
        await PreDbBrowser.EnsureCoreWebView2Async();
        PreDbBrowser.CoreWebView2.Navigate("https://predb.net/");
    }

    // Drag-and-drop: record start point
    private void DragSource_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
    }

    // Drag-and-drop: initiate drag if moved enough
    private void DragSource_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;

        var diff = e.GetPosition(null) - _dragStartPoint;
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        if (sender is not DataGrid grid || grid.SelectedItem == null) return;

        var data = new DataObject("DashboardDragItem", grid.SelectedItem);
        DragDrop.DoDragDrop(grid, data, DragDropEffects.Copy);
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(_config, _serverManager);
        window.Owner = this;
        window.ShowDialog();
    }

    // Drag-and-drop: handle drop on Downloads grid
    private void Downloads_Drop(object sender, DragEventArgs e)
    {
        if (DataContext is not DashboardViewModel vm) return;

        if (e.Data.GetData("DashboardDragItem") is NotificationItemVm)
        {
            vm.DownloadNotificationCommand.Execute(null);
        }
        else if (e.Data.GetData("DashboardDragItem") is SearchResultVm)
        {
            vm.DownloadSearchResultCommand.Execute(null);
        }
    }
}
