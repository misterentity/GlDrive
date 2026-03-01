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
    private readonly MountService _mountService;
    private readonly AppConfig _config;
    private MountState _currentState;
    private readonly List<(string Category, string Release)> _releaseBatch = new();
    private System.Windows.Threading.DispatcherTimer? _batchTimer;
    private DashboardWindow? _dashboardWindow;

    public TrayViewModel(MountService mountService, AppConfig config)
    {
        _mountService = mountService;
        _config = config;

        _mountService.NewReleaseDetected += (category, release) =>
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

        _mountService.StateChanged += state =>
        {
            if (state == MountState.Connected && _mountService.Matcher != null)
            {
                _mountService.Matcher.MatchFound += (item, cat, rel) =>
                    Application.Current?.Dispatcher.Invoke(() =>
                        ShowNotification("Grabbed", $"{item.Title} [{cat}]"));
            }
        };

        _mountService.StateChanged += state =>
        {
            CurrentState = state;
            Application.Current?.Dispatcher.Invoke(() =>
            {
                switch (state)
                {
                    case MountState.Connected:
                        ShowNotification("GlDrive", $"Connected — {_config.Mount.DriveLetter}: is ready");
                        break;
                    case MountState.Reconnecting:
                        ShowNotification("GlDrive", "Disconnected — reconnecting...");
                        break;
                    case MountState.Error:
                        ShowNotification("GlDrive", "Connection error");
                        break;
                }
            });
        };

        OpenDriveCommand = new RelayCommand(() =>
        {
            var path = _config.Mount.DriveLetter + ":\\";
            Process.Start("explorer.exe", path);
        });

        RefreshCacheCommand = new RelayCommand(() =>
        {
            _mountService.RefreshCache();
            ShowNotification("GlDrive", "Cache cleared");
        });

        ToggleMountCommand = new RelayCommand(async () =>
        {
            if (_currentState == MountState.Connected || _currentState == MountState.Reconnecting)
            {
                _mountService.Unmount();
            }
            else
            {
                try { await _mountService.Mount(); }
                catch (Exception ex)
                {
                    Log.Error(ex, "Mount failed");
                    ShowNotification("GlDrive", $"Mount failed: {ex.Message}");
                }
            }
        });

        SettingsCommand = new RelayCommand(() =>
        {
            var window = new SettingsWindow(_config, _mountService);
            window.ShowDialog();
        });

        DashboardCommand = new RelayCommand(() =>
        {
            if (_dashboardWindow == null || !_dashboardWindow.IsLoaded)
            {
                _dashboardWindow = new DashboardWindow(_mountService, _config);
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
            _mountService.Unmount();
            Application.Current?.Shutdown();
        });
    }

    public MountState CurrentState
    {
        get => _currentState;
        set { _currentState = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(MountButtonText)); OnPropertyChanged(nameof(IsConnected)); }
    }

    public string StatusText => CurrentState switch
    {
        MountState.Connected => $"Connected ({_config.Mount.DriveLetter}:)",
        MountState.Connecting => "Connecting...",
        MountState.Reconnecting => "Reconnecting...",
        MountState.Error => "Error",
        _ => "Unmounted"
    };

    public string MountButtonText => CurrentState == MountState.Connected || CurrentState == MountState.Reconnecting
        ? "Unmount Drive" : "Mount Drive";

    public bool IsConnected => CurrentState == MountState.Connected;

    public ICommand OpenDriveCommand { get; }
    public ICommand RefreshCacheCommand { get; }
    public ICommand ToggleMountCommand { get; }
    public ICommand DashboardCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand ViewLogsCommand { get; }
    public ICommand ExitCommand { get; }

    public Action<string, string>? ShowNotificationRequested { get; set; }

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
