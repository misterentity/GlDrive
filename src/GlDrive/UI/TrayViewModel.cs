using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using GlDrive.Config;
using GlDrive.Services;
using Serilog;

namespace GlDrive.UI;

public class TrayViewModel : INotifyPropertyChanged
{
    private readonly ServerManager _serverManager;
    private readonly AppConfig _config;
    private string _statusText = "No servers";
    private readonly List<(string Category, string Release)> _releaseBatch = new();
    private System.Windows.Threading.DispatcherTimer? _batchTimer;
    private DashboardWindow? _dashboardWindow;

    public TrayViewModel(ServerManager serverManager, AppConfig config)
    {
        _serverManager = serverManager;
        _config = config;

        _serverManager.NewReleaseDetected += (serverId, serverName, category, release) =>
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                _releaseBatch.Add((category, release));
                _batchTimer ??= new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(3)
                };
                _batchTimer.Tick += (_, _) =>
                {
                    _batchTimer.Stop();
                    FlushReleaseBatch();
                };
                _batchTimer.Stop();
                _batchTimer.Start();
            });
        };

        _serverManager.ServerStateChanged += (serverId, serverName, state) =>
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                UpdateStatusText();
                OnPropertyChanged(nameof(StatusText));

                // Wire up wishlist matcher notifications for newly connected servers
                if (state == MountState.Connected)
                {
                    var server = _serverManager.GetServer(serverId);
                    if (server?.Matcher != null)
                    {
                        server.Matcher.MatchFound += (item, cat, rel) =>
                            Application.Current?.Dispatcher.Invoke(() =>
                                ShowNotification("Grabbed", $"{item.Title} [{cat}] ({serverName})"));
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
                _dashboardWindow = new DashboardWindow(_serverManager, _config);
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
            _serverManager.UnmountAll();
            Application.Current?.Shutdown();
        });

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
            StatusText = server.CurrentState switch
            {
                MountState.Connected => $"Connected ({server.DriveLetter}:)",
                MountState.Connecting => "Connecting...",
                MountState.Reconnecting => "Reconnecting...",
                MountState.Error => "Error",
                _ => "Unmounted"
            };
            return;
        }

        // Multi-server: summarize
        var drives = mounted
            .Where(s => s.CurrentState == MountState.Connected)
            .Select(s => $"{s.ServerName} ({s.DriveLetter}:)");
        StatusText = connectedCount > 0
            ? $"{connectedCount}/{total} connected"
            : "No servers connected";
    }

    private void FlushReleaseBatch()
    {
        if (_releaseBatch.Count == 0) return;

        if (_releaseBatch.Count == 1)
        {
            var (cat, rel) = _releaseBatch[0];
            ShowNotification(cat, rel);
        }
        else
        {
            var lines = _releaseBatch.Select(r => $"[{r.Category}] {r.Release}");
            ShowNotification($"{_releaseBatch.Count} new releases", string.Join("\n", lines));
        }

        _releaseBatch.Clear();
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
