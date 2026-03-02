using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Services;

namespace GlDrive.UI;

public partial class DashboardWindow : Window
{
    private bool _upcomingLoaded;
    private Point _dragStartPoint;

    public DashboardWindow(ServerManager serverManager, AppConfig config, NotificationStore notificationStore)
    {
        InitializeComponent();
        DataContext = new DashboardViewModel(serverManager, config, notificationStore);
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
        if (tab.Header?.ToString() != "Upcoming" || _upcomingLoaded) return;

        _upcomingLoaded = true;
        if (DataContext is DashboardViewModel vm)
            await vm.LoadUpcoming();
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
