using System.Windows;
using System.Windows.Controls;
using GlDrive.Config;
using GlDrive.Services;

namespace GlDrive.UI;

public partial class DashboardWindow : Window
{
    private bool _upcomingLoaded;

    public DashboardWindow(ServerManager serverManager, AppConfig config)
    {
        InitializeComponent();
        DataContext = new DashboardViewModel(serverManager, config);
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
}
