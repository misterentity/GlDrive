using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Irc;
using GlDrive.Services;
using Serilog;

namespace GlDrive.UI;

public class TrayViewModel : INotifyPropertyChanged
{
    private readonly ServerManager _serverManager;
    private readonly AppConfig _config;
    private readonly NotificationStore _notificationStore;
    private readonly UpdateChecker _updateChecker;
    private string _statusText = "No servers";
    private DashboardWindow? _dashboardWindow;
    private GitHubRelease? _availableUpdate;
    private readonly Dictionary<string, (Action<DownloadItem, DownloadProgress> progress, Action<DownloadItem> status)> _subscribedDownloadHandlers = new();
    private double _lastDownloadSpeed;
    private int _activeDownloadCount;
    private DateTime _lastSpeedUpdate;

    public TrayViewModel(ServerManager serverManager, AppConfig config, NotificationStore notificationStore)
    {
        _serverManager = serverManager;
        _config = config;
        _notificationStore = notificationStore;
        _updateChecker = new UpdateChecker();

        _serverManager.ServerStateChanged += (serverId, serverName, state) =>
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                UpdateStatusText();
                OnPropertyChanged(nameof(StatusText));

                // Wire up wishlist matcher and download notifications for newly connected servers
                // Re-subscribe on each connect since unmount creates new MountService instances
                if (state == MountState.Connected)
                {
                    var server = _serverManager.GetServer(serverId);
                    if (server?.Matcher != null)
                    {
                        server.Matcher.MatchFound += (item, cat, rel) =>
                            Application.Current?.Dispatcher.Invoke(() =>
                                ShowNotification("Grabbed", $"{item.Title} [{cat}] ({serverName})"));
                    }
                    if (server?.Downloads != null)
                    {
                        // Remove old handlers if server was previously mounted
                        if (_subscribedDownloadHandlers.TryGetValue(serverId, out var old))
                        {
                            server.Downloads.DownloadProgressChanged -= old.progress;
                            server.Downloads.DownloadStatusChanged -= old.status;
                        }

                        Action<DownloadItem, DownloadProgress> progressHandler = (downloadItem, progress) =>
                        {
                            _lastDownloadSpeed = progress.BytesPerSecond;
                            var now = DateTime.UtcNow;
                            if ((now - _lastSpeedUpdate).TotalSeconds < 1) return;
                            _lastSpeedUpdate = now;
                            Application.Current?.Dispatcher.InvokeAsync(() =>
                            {
                                UpdateStatusText();
                                OnPropertyChanged(nameof(StatusText));
                            });
                        };
                        Action<DownloadItem> statusHandler = downloadItem =>
                        {
                            _activeDownloadCount = server.Downloads.Store.Items
                                .Count(i => i.Status == DownloadStatus.Downloading);
                            if (downloadItem.Status == DownloadStatus.Completed)
                            {
                                Application.Current?.Dispatcher.Invoke(() =>
                                    ShowNotification("Download Complete", downloadItem.ReleaseName));
                                if (_config.Downloads.PlaySoundOnComplete)
                                    System.Media.SystemSounds.Asterisk.Play();
                            }
                            else if (downloadItem.Status == DownloadStatus.Failed)
                                Application.Current?.Dispatcher.Invoke(() =>
                                    ShowNotification("Download Failed",
                                        $"{downloadItem.ReleaseName}: {downloadItem.ErrorMessage ?? "Unknown error"}"));
                        };
                        server.Downloads.DownloadProgressChanged += progressHandler;
                        server.Downloads.DownloadStatusChanged += statusHandler;
                        _subscribedDownloadHandlers[serverId] = (progressHandler, statusHandler);
                    }
                }

                switch (state)
                {
                    case MountState.Connected:
                        ShowNotification("GlDrive", $"{serverName} connected");
                        break;
                    case MountState.Reconnecting:
                        ShowNotification("GlDrive", $"{serverName} disconnected — reconnecting...");
                        break;
                    case MountState.Error:
                        ShowNotification("GlDrive", $"{serverName} connection error");
                        break;
                }
            });
        };

        _serverManager.IrcStateChanged += (serverId, serverName, state) =>
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                switch (state)
                {
                    case IrcServiceState.Connected:
                        ShowNotification("IRC", $"{serverName} IRC connected");
                        break;
                    case IrcServiceState.Disconnected:
                        ShowNotification("IRC", $"{serverName} IRC disconnected");
                        break;
                }
            });
        };

        OpenDriveCommand = new RelayCommand(() =>
        {
            var mounted = _serverManager.GetMountedServers();
            if (mounted.Count > 0)
            {
                var path = mounted[0].DriveLetter + ":\\";
                Process.Start("explorer.exe", path);
            }
        });

        SettingsCommand = new RelayCommand(() =>
        {
            var window = new SettingsWindow(_config, _serverManager);
            window.ShowDialog();
        });

        DashboardCommand = new RelayCommand(() =>
        {
            if (_dashboardWindow == null || !_dashboardWindow.IsLoaded)
            {
                _dashboardWindow = new DashboardWindow(_serverManager, _config, _notificationStore);
                _dashboardWindow.Show();
            }
            else
            {
                _dashboardWindow.Activate();
            }
        });

        ViewLogsCommand = new RelayCommand(() =>
        {
            var logPath = Path.Combine(ConfigManager.AppDataPath, "logs");
            Directory.CreateDirectory(logPath);
            Process.Start("explorer.exe", logPath);
        });

        ExitCommand = new RelayCommand(() =>
        {
            _updateChecker.StopPeriodicCheck();
            _serverManager.UnmountAll();
            Application.Current?.Shutdown();
        });

        UpdateCommand = new RelayCommand(async () =>
        {
            var release = _availableUpdate ?? await _updateChecker.CheckForUpdateAsync();
            if (release != null)
            {
                AvailableUpdate = release;
                ShowNotification("GlDrive", $"Downloading {release.TagName}...");
                await _updateChecker.DownloadAndInstallAsync(release);
            }
            else
            {
                ShowNotification("GlDrive", "No update available");
            }
        });

        _updateChecker.UpdateAvailable += release =>
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                AvailableUpdate = release;
                ShowNotification("Update Available", $"GlDrive {release.TagName} is available");
            });
        };

        _updateChecker.RestartRequested += () =>
        {
            Application.Current?.Dispatcher.Invoke(() => ExitCommand.Execute(null));
        };

        _serverManager.BncRateLimitDetected += (serverName, message) =>
        {
            Application.Current?.Dispatcher.Invoke(() =>
                ShowNotification("BNC Rate Limit", $"{serverName}: {message}"));
        };

        _updateChecker.StartPeriodicCheck();

        UpdateStatusText();
    }

    public ServerManager ServerManager => _serverManager;
    public AppConfig Config => _config;

    public string StatusText
    {
        get => _statusText;
        private set { _statusText = value; OnPropertyChanged(); }
    }

    public ICommand OpenDriveCommand { get; }
    public ICommand DashboardCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand ViewLogsCommand { get; }
    public ICommand ExitCommand { get; }
    public ICommand UpdateCommand { get; }

    public GitHubRelease? AvailableUpdate
    {
        get => _availableUpdate;
        private set { _availableUpdate = value; OnPropertyChanged(); }
    }

    public Action<string, string>? ShowNotificationRequested { get; set; }

    public void UpdateStatusText()
    {
        var mounted = _serverManager.GetMountedServers();
        var total = _config.Servers.Count;

        if (total == 0)
        {
            StatusText = "No servers configured";
            return;
        }

        var connectedCount = mounted.Count(s => s.CurrentState == MountState.Connected);

        if (connectedCount == 0 && mounted.Count == 0)
        {
            StatusText = "Unmounted";
            return;
        }

        if (total == 1 && mounted.Count > 0)
        {
            var server = mounted[0];
            var baseStatus = server.CurrentState switch
            {
                MountState.Connected => $"Connected ({server.DriveLetter}:)",
                MountState.Connecting => "Connecting...",
                MountState.Reconnecting => "Reconnecting...",
                MountState.Error => "Error",
                _ => "Unmounted"
            };
            StatusText = _activeDownloadCount > 0 && _lastDownloadSpeed > 0
                ? $"{baseStatus} | {FormatSpeed(_lastDownloadSpeed)}"
                : baseStatus;
            return;
        }

        // Multi-server: summarize
        var drives = mounted
            .Where(s => s.CurrentState == MountState.Connected)
            .Select(s => $"{s.ServerName} ({s.DriveLetter}:)");
        StatusText = connectedCount > 0
            ? $"{connectedCount}/{total} connected"
            : "No servers connected";

        // Append active transfer info
        if (_activeDownloadCount > 0 && _lastDownloadSpeed > 0)
            StatusText += $" | {FormatSpeed(_lastDownloadSpeed)}";
    }

    private static string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond >= 1024 * 1024)
            return $"{bytesPerSecond / (1024 * 1024):F1} MB/s";
        if (bytesPerSecond >= 1024)
            return $"{bytesPerSecond / 1024:F0} KB/s";
        return $"{bytesPerSecond:F0} B/s";
    }

    public void ShowNotification(string title, string message)
    {
        Log.Information("{Title}: {Message}", title, message);
        ShowNotificationRequested?.Invoke(title, message);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public class RelayCommand : ICommand
{
    private readonly Func<Task>? _asyncExecute;
    private readonly Action? _syncExecute;

    public RelayCommand(Action execute) => _syncExecute = execute;
    public RelayCommand(Func<Task> execute) => _asyncExecute = execute;

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
    public bool CanExecute(object? parameter) => true;

    public async void Execute(object? parameter)
    {
        if (_asyncExecute != null)
            await _asyncExecute();
        else
            _syncExecute?.Invoke();
    }
}

public class RelayCommand<T> : ICommand
{
    private readonly Func<T, Task>? _asyncExecute;
    private readonly Action<T>? _syncExecute;

    public RelayCommand(Action<T> execute) => _syncExecute = execute;
    public RelayCommand(Func<T, Task> execute) => _asyncExecute = execute;

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
    public bool CanExecute(object? parameter) => true;

    public async void Execute(object? parameter)
    {
        var value = parameter is T typed ? typed : default;
        if (value == null) return;
        if (_asyncExecute != null)
            await _asyncExecute(value);
        else
            _syncExecute?.Invoke(value);
    }
}
