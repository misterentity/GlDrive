using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using H.NotifyIcon;

namespace GlDrive.UI;

public static class TrayIconSetup
{
    public static void Configure(TaskbarIcon taskbarIcon, TrayViewModel vm)
    {
        taskbarIcon.ToolTipText = "GlDrive";
        taskbarIcon.IconSource = new GeneratedIconSource
        {
            Text = "G",
            Foreground = Brushes.White,
            FontSize = 18
        };

        var menu = new ContextMenu();

        // Header
        var header = new MenuItem { Header = "GlDrive", IsEnabled = false, FontWeight = FontWeights.Bold };
        menu.Items.Add(header);

        var statusItem = new MenuItem { IsEnabled = false };
        statusItem.SetBinding(MenuItem.HeaderProperty, new System.Windows.Data.Binding("StatusText") { Source = vm });
        menu.Items.Add(statusItem);

        menu.Items.Add(new Separator());

        // Open Drive
        var openDrive = new MenuItem { Header = "Open Drive" };
        openDrive.Click += (_, _) => vm.OpenDriveCommand.Execute(null);
        openDrive.SetBinding(UIElement.IsEnabledProperty,
            new System.Windows.Data.Binding("IsConnected") { Source = vm });
        menu.Items.Add(openDrive);

        // Refresh Cache
        var refresh = new MenuItem { Header = "Refresh (Clear Cache)" };
        refresh.Click += (_, _) => vm.RefreshCacheCommand.Execute(null);
        refresh.SetBinding(UIElement.IsEnabledProperty,
            new System.Windows.Data.Binding("IsConnected") { Source = vm });
        menu.Items.Add(refresh);

        menu.Items.Add(new Separator());

        // Mount/Unmount
        var mount = new MenuItem();
        mount.SetBinding(MenuItem.HeaderProperty,
            new System.Windows.Data.Binding("MountButtonText") { Source = vm });
        mount.Click += (_, _) => vm.ToggleMountCommand.Execute(null);
        menu.Items.Add(mount);

        menu.Items.Add(new Separator());

        // Settings
        var settings = new MenuItem { Header = "Settings..." };
        settings.Click += (_, _) => vm.SettingsCommand.Execute(null);
        menu.Items.Add(settings);

        // View Logs
        var logs = new MenuItem { Header = "View Logs..." };
        logs.Click += (_, _) => vm.ViewLogsCommand.Execute(null);
        menu.Items.Add(logs);

        menu.Items.Add(new Separator());

        // Exit
        var exit = new MenuItem { Header = "Exit" };
        exit.Click += (_, _) => vm.ExitCommand.Execute(null);
        menu.Items.Add(exit);

        taskbarIcon.ContextMenu = menu;

        // Double-click opens drive
        taskbarIcon.TrayLeftMouseDown += (_, _) => vm.OpenDriveCommand.Execute(null);

        // Update icon on state changes
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(TrayViewModel.CurrentState))
            {
                taskbarIcon.ToolTipText = $"GlDrive — {vm.StatusText}";
            }
        };
    }

}
