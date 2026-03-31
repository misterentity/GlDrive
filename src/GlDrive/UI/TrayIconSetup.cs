using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using GlDrive.Irc;
using GlDrive.Services;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Serilog;

namespace GlDrive.UI;

public static class TrayIconSetup
{
    public static void Configure(TaskbarIcon taskbarIcon, TrayViewModel vm)
    {
        var version = UpdateChecker.CurrentVersion;
        var versionStr = $"{version.Major}.{version.Minor}.{version.Build}";
        taskbarIcon.ToolTipText = $"GlDrive v{versionStr}";
        taskbarIcon.Icon = CyberpunkIconGenerator.Generate(MountState.Unmounted);

        var menu = new ContextMenu();
        BuildMenu(menu, vm);
        taskbarIcon.ContextMenu = menu;

        // Left-click opens dashboard
        taskbarIcon.TrayLeftMouseDown += (_, _) => vm.DashboardCommand.Execute(null);

        // Rebuild menu and update icon when server states change
        vm.ServerManager.ServerStateChanged += (_, _, _) =>
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                BuildMenu(menu, vm);

                // Update icon based on best state across all servers
                var mounted = vm.ServerManager.GetMountedServers();
                var bestState = MountState.Unmounted;
                foreach (var s in mounted)
                {
                    if (s.CurrentState == MountState.Connected) { bestState = MountState.Connected; break; }
                    if (s.CurrentState == MountState.Reconnecting) bestState = MountState.Reconnecting;
                    else if (s.CurrentState == MountState.Connecting && bestState != MountState.Reconnecting)
                        bestState = MountState.Connecting;
                    else if (s.CurrentState == MountState.Error && bestState == MountState.Unmounted)
                        bestState = MountState.Error;
                }

                taskbarIcon.ToolTipText = $"GlDrive v{versionStr} — {vm.StatusText}";
                taskbarIcon.Icon = CyberpunkIconGenerator.Generate(bestState);
            });
        };

        // Rebuild menu when IRC state changes
        vm.ServerManager.IrcStateChanged += (_, _, _) =>
        {
            Application.Current?.Dispatcher.BeginInvoke(() => BuildMenu(menu, vm));
        };

        // Rebuild menu when an update becomes available
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(vm.AvailableUpdate))
                Application.Current?.Dispatcher.BeginInvoke(() => BuildMenu(menu, vm));
        };

        // Wire balloon tip notifications
        vm.ShowNotificationRequested = (title, message) =>
        {
            taskbarIcon.ShowNotification(title, message, NotificationIcon.Info);
        };
    }

    private static void BuildMenu(ContextMenu menu, TrayViewModel vm)
    {
        menu.Items.Clear();

        // Header with version
        var ver = UpdateChecker.CurrentVersion;
        var header = new MenuItem { Header = $"GlDrive v{ver.Major}.{ver.Minor}.{ver.Build}", IsEnabled = false, FontWeight = FontWeights.Bold };
        menu.Items.Add(header);

        var statusItem = new MenuItem { Header = vm.StatusText, IsEnabled = false };
        menu.Items.Add(statusItem);

        menu.Items.Add(new Separator());

        // Per-server items
        var config = vm.Config;
        if (config.Servers.Count > 0)
        {
            foreach (var serverConfig in config.Servers)
            {
                var mounted = vm.ServerManager.GetServer(serverConfig.Id);
                var isConnected = mounted?.CurrentState == MountState.Connected;
                var isActive = mounted != null && mounted.CurrentState != MountState.Unmounted;

                var hasDrive = serverConfig.Mount.MountDrive;
                var stateLabel = mounted?.CurrentState switch
                {
                    MountState.Connected => hasDrive ? $"{serverConfig.Mount.DriveLetter}:" : "connected",
                    MountState.Connecting => "connecting...",
                    MountState.Reconnecting => "reconnecting...",
                    MountState.Error => "error",
                    _ => "disconnected"
                };

                var serverMenu = new MenuItem
                {
                    Header = $"{serverConfig.Name} ({stateLabel})"
                };

                // Mount/Unmount
                var serverId = serverConfig.Id;
                var serverName = serverConfig.Name;

                if (isActive)
                {
                    var disconnectItem = new MenuItem { Header = "Disconnect" };
                    disconnectItem.Click += async (_, _) =>
                    {
                        try
                        {
                            await vm.ServerManager.UnmountServerAsync(serverId);
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "Disconnect failed for {Server}", serverName);
                        }
                        vm.UpdateStatusText();
                    };
                    serverMenu.Items.Add(disconnectItem);
                }
                else
                {
                    var connectItem = new MenuItem { Header = "Connect" };
                    connectItem.Click += async (_, _) =>
                    {
                        try
                        {
                            await vm.ServerManager.MountServer(serverId);
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "Connect failed for {Server}", serverName);
                            vm.ShowNotification("GlDrive", $"Connect failed for {serverName}: {ex.Message}");
                        }
                    };
                    serverMenu.Items.Add(connectItem);
                }

                if (isConnected)
                {
                    // Open Drive (only if drive mount is enabled)
                    if (hasDrive)
                    {
                        var driveLetter = serverConfig.Mount.DriveLetter;
                        var openItem = new MenuItem { Header = "Open Drive" };
                        openItem.Click += (_, _) => Process.Start("explorer.exe", $"{driveLetter}:\\");
                        serverMenu.Items.Add(openItem);
                    }

                    var refreshItem = new MenuItem { Header = "Refresh Cache" };
                    refreshItem.Click += (_, _) =>
                    {
                        mounted?.RefreshCache();
                        vm.ShowNotification("GlDrive", $"Cache cleared for {serverName}");
                    };
                    serverMenu.Items.Add(refreshItem);
                }

                // IRC status
                var ircService = vm.ServerManager.GetIrcService(serverId);
                if (ircService != null || serverConfig.Irc.Enabled)
                {
                    var ircState = ircService?.State ?? IrcServiceState.Disconnected;
                    var ircLabel = ircState switch
                    {
                        IrcServiceState.Connected => "IRC: connected",
                        IrcServiceState.Connecting => "IRC: connecting...",
                        IrcServiceState.Reconnecting => "IRC: reconnecting...",
                        _ => "IRC: disconnected"
                    };
                    var ircItem = new MenuItem { Header = ircLabel, IsEnabled = false };
                    serverMenu.Items.Add(ircItem);
                }

                menu.Items.Add(serverMenu);
            }

            menu.Items.Add(new Separator());
        }

        // Dashboard
        var dashboard = new MenuItem { Header = "Dashboard..." };
        dashboard.Click += (_, _) => vm.DashboardCommand.Execute(null);
        dashboard.IsEnabled = vm.ServerManager.GetMountedServers().Any(s => s.CurrentState == MountState.Connected);
        menu.Items.Add(dashboard);

        menu.Items.Add(new Separator());

        // Settings
        var settings = new MenuItem { Header = "Settings..." };
        settings.Click += (_, _) => vm.SettingsCommand.Execute(null);
        menu.Items.Add(settings);

        // View Logs
        var logs = new MenuItem { Header = "View Logs..." };
        logs.Click += (_, _) => vm.ViewLogsCommand.Execute(null);
        menu.Items.Add(logs);

        // Install glftpd
        var installer = new MenuItem { Header = "Install glftpd..." };
        installer.Click += (_, _) =>
        {
            var win = Application.Current.Windows.OfType<GlftpdInstallerWindow>().FirstOrDefault();
            if (win != null) { win.Activate(); }
            else { new GlftpdInstallerWindow().Show(); }
        };
        menu.Items.Add(installer);

        // Extract Archives
        var extractor = new MenuItem { Header = "Extract Archives..." };
        extractor.Click += (_, _) =>
        {
            var win = Application.Current.Windows.OfType<ExtractorWindow>().FirstOrDefault();
            if (win != null) { win.Activate(); }
            else { new ExtractorWindow().Show(); }
        };
        menu.Items.Add(extractor);

        // Update
        var updateHeader = vm.AvailableUpdate != null
            ? $"Update to {vm.AvailableUpdate.TagName}"
            : "Update";
        var update = new MenuItem { Header = updateHeader };
        update.Click += (_, _) => vm.UpdateCommand.Execute(null);
        menu.Items.Add(update);

        menu.Items.Add(new Separator());

        // Exit
        var exit = new MenuItem { Header = "Exit" };
        exit.Click += (_, _) => vm.ExitCommand.Execute(null);
        menu.Items.Add(exit);
    }
}
