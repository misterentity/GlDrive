using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Ftp;
using GlDrive.Irc;
using GlDrive.Services;
using GlDrive.Services.Alerts;
using Serilog;

namespace GlDrive.UI;

public class TrayViewModel : INotifyPropertyChanged
{
    private readonly ServerManager _serverManager;
    private readonly AppConfig _config;
    private readonly NotificationStore _notificationStore;
    private readonly UpdateChecker _updateChecker;
    public AlertRouter Alerts { get; }
    private readonly Dictionary<string, (WishlistMatcher Matcher, Action<WishlistItem, string, string> Handler)> _matchHandlers = new();
    internal UpdateChecker AutomationUpdater => _updateChecker;
    private string _statusText = "No servers";
    private DashboardWindow? _dashboardWindow;
    private GitHubRelease? _availableUpdate;
    private readonly Dictionary<string, (Action<DownloadItem, DownloadProgress> progress, Action<DownloadItem> status)> _subscribedDownloadHandlers = new();
    private readonly Dictionary<string, double> _serverSpeeds = new();
    private readonly Dictionary<string, int> _serverDownloadCounts = new();
    private DateTime _lastSpeedUpdate;

    internal static bool ShouldNotifyConnectionState(MountState state, bool authenticationHeld) =>
        state == MountState.Connected || (!authenticationHeld && state is MountState.Reconnecting or MountState.Error);

    public TrayViewModel(ServerManager serverManager, AppConfig config, NotificationStore notificationStore)
    {
        _serverManager = serverManager;
        _config = config;
        _notificationStore = notificationStore;
        _updateChecker = new UpdateChecker();
        Alerts = new AlertRouter(config, (title, message, severity) =>
            Application.Current?.Dispatcher.BeginInvoke(() => ShowNotificationRequested?.Invoke(title, message, severity)));
        _serverManager.Hooks.Alerts = Alerts;
        _serverManager.IrcAttentionReceived += alert => Alerts.Publish(alert);
        _serverManager.NukeWatch.Flagged += entry =>
            ShowNotification("Owned release nuked", $"{entry.Owned.ReleaseName}: {entry.Reason}",
                AlertKind.OwnedReleaseNuke, AlertSeverity.Warning, release: entry.Owned.ReleaseName);

        _serverManager.ServerStateChanged += (serverId, serverName, state) =>
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
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
                        if (_matchHandlers.Remove(serverId, out var previous)) previous.Matcher.MatchFound -= previous.Handler;
                        Action<WishlistItem, string, string> handler = (item, cat, rel) =>
                            Application.Current?.Dispatcher.BeginInvoke(() =>
                                ShowNotification("Grabbed", $"{item.Title} [{cat}] ({serverName})"));
                        server.Matcher.MatchFound += handler;
                        _matchHandlers[serverId] = (server.Matcher, handler);
                    }
                    if (server?.Downloads != null)
                    {
                        // Remove old handlers if server was previously mounted
                        if (_subscribedDownloadHandlers.TryGetValue(serverId, out var old))
                        {
                            server.Downloads.DownloadProgressChanged -= old.progress;
                            server.Downloads.DownloadStatusChanged -= old.status;
                        }

                        var capturedServerId = serverId;
                        Action<DownloadItem, DownloadProgress> progressHandler = (downloadItem, progress) =>
                        {
                            _serverSpeeds[capturedServerId] = progress.BytesPerSecond;
                            var now = DateTime.UtcNow;
                            if ((now - _lastSpeedUpdate).TotalSeconds < 1) return;
                            _lastSpeedUpdate = now;
                            Application.Current?.Dispatcher.BeginInvoke(() =>
                            {
                                UpdateStatusText();
                                OnPropertyChanged(nameof(StatusText));
                            });
                        };
                        Action<DownloadItem> statusHandler = downloadItem =>
                        {
                            _serverDownloadCounts[capturedServerId] = server.Downloads.Store.Items
                                .Count(i => i.Status == DownloadStatus.Downloading);
                            if (downloadItem.Status == DownloadStatus.Completed)
                            {
                                Application.Current?.Dispatcher.BeginInvoke(() =>
                                    ShowNotification("Download Complete", downloadItem.ReleaseName,
                                        AlertKind.DownloadComplete, AlertSeverity.Information, capturedServerId, downloadItem.ReleaseName));
                                if (_config.Downloads.PlaySoundOnComplete)
                                    System.Media.SystemSounds.Asterisk.Play();
                            }
                            else if (downloadItem.Status == DownloadStatus.Failed)
                                Application.Current?.Dispatcher.BeginInvoke(() =>
                                    ShowNotification("Download Failed",
                                        $"{downloadItem.ReleaseName}: {downloadItem.ErrorMessage ?? "Unknown error"}",
                                        AlertKind.DownloadFailed, AlertSeverity.Error, capturedServerId, downloadItem.ReleaseName));
                        };
                        server.Downloads.DownloadProgressChanged += progressHandler;
                        server.Downloads.DownloadStatusChanged += statusHandler;
                        _subscribedDownloadHandlers[serverId] = (progressHandler, statusHandler);
                    }
                }

                if (!_config.Health.Enabled && ShouldNotifyConnectionState(state, _serverManager.IsAuthenticationHeld(serverId)))
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
            Application.Current?.Dispatcher.BeginInvoke(() =>
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
            var mounted = _serverManager.GetMountedServers()
                .Where(s => s.CurrentState == MountState.Connected && !string.IsNullOrEmpty(s.DriveLetter))
                .ToList();
            if (mounted.Count == 1)
            {
                Process.Start("explorer.exe", mounted[0].DriveLetter + ":\\");
            }
            else if (mounted.Count > 1)
            {
                // Multiple servers — open each drive in Explorer
                foreach (var s in mounted)
                    Process.Start("explorer.exe", s.DriveLetter + ":\\");
            }
        });

        SettingsCommand = new RelayCommand(async () =>
        {
            var window = new SettingsWindow(_config, _serverManager);
            if (window.ShowDialog() == true)
                await _serverManager.SyncAfterConfigChange();
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
            App.MarkExiting("tray Exit");
            _updateChecker.StopPeriodicCheck();

            // Unmount with a hard timeout — don't let hung FTP connections prevent exit
            var unmountTask = Task.Run(async () =>
            {
                try { await _serverManager.UnmountAllAsync(); } catch { }
            });

            if (!unmountTask.Wait(TimeSpan.FromSeconds(5)))
                Log.Warning("Unmount timed out after 5s — forcing exit");

            Application.Current?.Shutdown();

            // Ensure process actually exits even if WPF shutdown hangs
            Task.Delay(3000).ContinueWith(_ => Environment.Exit(0));
        });

        UpdateCommand = new RelayCommand(async () =>
        {
            try
            {
                // Always re-check — don't trust stale _availableUpdate
                var release = await _updateChecker.CheckForUpdateAsync();
                if (release != null)
                {
                    AvailableUpdate = release;
                    ShowNotification("GlDrive", $"Downloading {release.TagName}...");
                    // Clear so we don't re-download if something goes wrong
                    AvailableUpdate = null;
                    await Task.Run(() => _updateChecker.DownloadAndInstallAsync(release));
                }
                else
                {
                    AvailableUpdate = null;
                    ShowNotification("GlDrive", $"Already up to date (v{UpdateChecker.CurrentVersion.Major}.{UpdateChecker.CurrentVersion.Minor}.{UpdateChecker.CurrentVersion.Build})");
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Update failed");
                ShowNotification("GlDrive", $"Update failed: {ex.Message}");
            }
        });

        _serverManager.OwnNukeAlerted += notice =>
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                var server = _config.Servers.FirstOrDefault(s => s.Id == notice.ServerId);
                ShowNotification("Own-account nuke", $"{server?.Name ?? notice.ServerId}: {notice.Release} ({notice.Multiplier}x) — {notice.Reason}",
                    AlertKind.OwnNuke, AlertSeverity.Error, notice.ServerId, notice.Release);
            });

        _serverManager.SiteHealthNotice += notice =>
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                var name = _config.Servers.FirstOrDefault(s => s.Id == notice.ServerId)?.Name ?? notice.ServerId;
                ShowNotification(notice.Recovered ? "FTP recovered" : "FTP outage", $"{name}: {notice.Message}",
                    notice.Recovered ? AlertKind.SiteRecovered : AlertKind.SiteOutage,
                    notice.Recovered ? AlertSeverity.Information : AlertSeverity.Error, notice.ServerId);
            });
        _serverManager.AnnounceFeedNotice += notice =>
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                var name = _config.Servers.FirstOrDefault(s => s.Id == notice.ServerId)?.Name ?? notice.ServerId;
                ShowNotification(notice.Recovered ? "Announce feed recovered" : "Announce feed silent",
                    $"{name} {notice.Channel}: {notice.Diagnosis}",
                    notice.Recovered ? AlertKind.AnnounceRecovered : AlertKind.AnnounceSilent,
                    notice.Recovered ? AlertSeverity.Information : AlertSeverity.Warning, notice.ServerId, incident: notice.Channel);
            });

        // Race completion notifications
        if (_serverManager.Spread != null)
        {
            _serverManager.Spread.PartialCleanupNotice += (release, message) =>
                Application.Current?.Dispatcher.BeginInvoke(() =>
                    ShowNotification("Partial race cleanup", $"{release}: {message}"));
            _serverManager.Spread.JobCompleted += job =>
            {
                if (_config.Spread.NotifyOnRaceComplete)
                {
                    Application.Current?.Dispatcher.BeginInvoke(() =>
                        ShowNotification("Race Complete",
                            $"{job.ReleaseName} [{job.Section}] — {job.Sites.Count} sites",
                            AlertKind.RaceComplete, AlertSeverity.Information, release: job.ReleaseName));
                }
            };
        }

        _updateChecker.UpdateAvailable += release =>
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                AvailableUpdate = release;
                ShowNotification("Update Available", $"GlDrive {release.TagName} is available");
            });
        };

        _updateChecker.RestartRequested += () =>
        {
            // Updater is running and waiting for our PID to exit — shut down fast.
            // Delete the crash marker BEFORE exiting so the watchdog doesn't
            // interfere by restarting the old app while the updater replaces files.
            // Also write an update marker so the watchdog knows to stand down.
            try
            {
                var appData = GlDrive.Config.ConfigManager.AppDataPath;
                File.Delete(Path.Combine(appData, ".running"));
                // LaunchUpdater already wrote the HMAC-authenticated .updating marker. Do not
                // replace it with a plain timestamp: the watchdog rejects unsigned markers and
                // would exit instead of waiting to relaunch the completed update.
            }
            catch { }

            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                try { _serverManager.UnmountAll(); } catch { }
                // Use Process.Kill instead of Environment.Exit to ensure immediate
                // termination — Environment.Exit can hang on finalizers/GnuTLS teardown
                Process.GetCurrentProcess().Kill();
            });
        };

        _serverManager.BncRateLimitDetected += (serverName, message) =>
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
                ShowNotification("BNC Rate Limit", $"{serverName}: {message}"));
        };

        _serverManager.AuthenticationRejected += notice =>
        {
            if (_config.Health.Enabled) return; // The supervisor owns the outage/recovery pair.
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                var accountKey = ServerLoginGateRegistry.KeyFor(notice.Account.Host, notice.Account.Port, notice.Account.Username);
                var server = _config.Servers.FirstOrDefault(s =>
                    ServerLoginGateRegistry.KeyFor(s.Connection.Host, s.Connection.Port, s.Connection.Username) == accountKey);
                var label = string.IsNullOrWhiteSpace(server?.Name) ? "FTP account" : server.Name;
                ShowNotification("FTP Authentication", $"{label}: login rejected. Check the saved password and retry the connection. " +
                    $"Automatic login probe after {notice.NextProbeAtUtc.ToLocalTime():HH:mm}.");
            });
        };

        // Auto-install updates, but never restart while bytes are moving. The gate used to
        // require zero spread jobs, but a box that races around the clock is essentially never
        // job-idle, so updates starved (v3.10.37 sat deferred 10.5h). Gate on in-flight
        // TRANSFERS instead: a job that's merely queued or scanning loses nothing on restart
        // (the queue re-scans), so those windows are safe — only an active FXP transfer blocks.
        _updateChecker.CanInstallNow = () =>
            (_serverManager.Spread?.ActiveJobs.Sum(j => j.ActiveTransferList.Count) ?? 0) == 0;

        // If the gate still can't clear (a truly continuous transfer stream), make the hold
        // visible so the user can pause racing and apply the update, rather than it silently
        // waiting for the 12h forced install to interrupt a transfer.
        _updateChecker.UpdateInstallStalled += (release, heldFor) =>
            Application.Current?.Dispatcher.BeginInvoke(() => ShowNotification(
                "Update pending",
                $"{release.TagName} has been held back {heldFor.TotalHours:F0}h by active transfers. " +
                $"It auto-installs after {UpdateChecker.MaxInstallDeferral.TotalHours:F0}h total, " +
                "or pause racing to apply it now."));

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

    public string ProblemsBadge => $"Problems ({App.Problems?.Snapshot.Problems.Count ?? 0})";

    public ICommand OpenDriveCommand { get; }
    public ICommand DashboardCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand ViewLogsCommand { get; }
    public ICommand ExitCommand { get; }
    public ICommand UpdateCommand { get; }

    // ── AI Agent tray commands ─────────────────────────────────────────────

    public bool AgentEnabled
    {
        get => _config.Agent.Enabled;
        set
        {
            if (value && !_config.Agent.HasAcceptedConsent)
            {
                var dlg = new FirstRunAgentConsentDialog(_config.Agent.ModelId)
                {
                    Owner = System.Windows.Application.Current.MainWindow
                };
                if (dlg.ShowDialog() != true)
                {
                    OnPropertyChanged();
                    return;
                }
                _config.Agent.HasAcceptedConsent = true;
            }
            _config.Agent.Enabled = value;
            GlDrive.Config.ConfigManager.Save(_config, "tray");
            if (value) App.AgentRunner?.Start();
            else       App.AgentRunner?.Stop();
            OnPropertyChanged();
        }
    }

    public ICommand ToggleAgentEnabledCommand => new RelayCommand(() => AgentEnabled = !AgentEnabled);

    // ── Downloads slow mode ───────────────────────────────────────────────

    public bool SlowMode
    {
        get => _config.Downloads.MultiStream.SlowMode;
        set
        {
            _config.Downloads.MultiStream.SlowMode = value;
            GlDrive.Config.ConfigManager.Save(_config, "tray");
            Log.Information("Downloads slow mode {State}", value ? "on" : "off");
            _serverManager.RefreshTorrentDownloadRate();
            OnPropertyChanged();
        }
    }

    public string SlowModeHeader => $"⚡ Slow mode ({FormatKbps(_config.Downloads.MultiStream.SlowModeKbps)})";

    public ICommand ToggleSlowModeCommand => new RelayCommand(() => SlowMode = !SlowMode);

    internal static string FormatKbps(int kbps) =>
        kbps >= 1024 ? $"{kbps / 1024.0:0.#} MB/s" : $"{kbps} KB/s";

    public ICommand AgentRunNowCommand => new RelayCommand(async () =>
    {
        if (App.AgentRunner != null) await App.AgentRunner.RunNowAsync();
    });

    public ICommand OpenDashboardToAgentCommand => new RelayCommand(() =>
    {
        DashboardCommand.Execute(null);
    });

    public ICommand PauseAgent24hCommand => new RelayCommand(() =>
    {
        try
        {
            using var attribution = ConfigWriteScope.Begin("tray");
            var changes = App.ConfigChanges ?? throw new InvalidOperationException("Configuration leases are unavailable");
            changes.CreateLease("/agent/enabled", System.Text.Json.Nodes.JsonValue.Create(false),
                DateTime.UtcNow.AddHours(24), "Pause agent for 24 hours");
            App.AgentRunner?.Stop();
            OnPropertyChanged(nameof(AgentEnabled));
            MessageBox.Show(changes.PersistenceHealthy
                ? "AI agent paused for 24 hours. A later edit takes precedence; the lease survives restarts."
                : "AI agent paused for this session. The lease could not be persisted and may not survive a restart.");
        }
        catch (Exception ex)
        {
            Log.Warning("Agent pause lease failed ({ErrorType})", ex.GetType().Name);
            MessageBox.Show("The pause was not applied. Check the existing leases and local diagnostics.");
        }
    });

    public ICommand PanicRevertLastRunCommand => new RelayCommand(() =>
    {
        var last = App.AuditTrail?.ReadAll().Where(r => r.WasApplied && !r.Undone).LastOrDefault()?.RunId;
        if (last is null)
        {
            System.Windows.MessageBox.Show("No recent applied changes to revert.");
            return;
        }
        if (System.Windows.MessageBox.Show(
            $"Revert every change from run {last[..Math.Min(8, last.Length)]}?",
            "Confirm revert",
            System.Windows.MessageBoxButton.YesNo) != System.Windows.MessageBoxResult.Yes) return;
        RevertRun(last);
    });

    public ICommand RevertLastNCommand => new RelayCommand(() =>
    {
        var dlg = new RevertRunsDialog();
        if (dlg.ShowDialog() != true || dlg.SelectedRunIds.Count == 0) return;
        foreach (var id in dlg.SelectedRunIds) RevertRun(id);
    });

    internal void RevertRun(string runId)
    {
        if (App.AuditTrail is null || App.ChangeApplier is null) return;
        var rows = App.AuditTrail.ReadAll()
            .Where(r => r.RunId == runId && r.WasApplied && !r.Undone)
            .Reverse().ToList();
        var failures = new List<string>();
        var reverted = 0;
        foreach (var r in rows)
        {
            var inverse = new GlDrive.AiAgent.AgentChange
            {
                Category = r.Category,
                Target = r.Target,
                Before = r.After,
                After = r.Before,
                Reasoning = "Panic revert",
                EvidenceRef = "panic-revert",
                Confidence = 1.0
            };
            try
            {
                var report = AgentUndoCompletion.ApplyInverse(r,
                    () => App.ChangeApplier.Apply(new[] { inverse }, _config, _config.Agent,
                        "panic-" + Guid.NewGuid().ToString()[..8], dryRun: false));
                if (AgentUndoCompletion.TryComplete(report,
                    () => GlDrive.Config.ConfigManager.Save(_config, "tray"),
                    () => App.AuditTrail.MarkUndone(r.RunId, r.Target, "panic-revert"),
                    null, out var failure))
                    reverted++;
                else failures.Add(failure);
            }
            catch (Exception ex) { failures.Add("Undo failed: " + ex.Message); }
        }
        if (failures.Count > 0)
            System.Windows.MessageBox.Show($"Reverted {reverted} change(s); {failures.Count} could not be reverted.\n\n" +
                string.Join("\n", failures.Distinct()), "Revert incomplete");
    }

    // ── end AI Agent tray commands ─────────────────────────────────────────

    public ICommand BuildDigestDebugCommand => new RelayCommand(async () =>
    {
        try
        {
            var aiData = Path.Combine(ConfigManager.AppDataPath, "ai-data");
            Directory.CreateDirectory(aiData);
            var windowDays = _config.Agent.WindowDays;
            var digester = new GlDrive.AiAgent.LogDigester(aiData, () => new WishlistStore().Items);
            var bundle = await digester.BuildAsync(windowDays);
            var outPath = Path.Combine(aiData, "last-digest.json");
            var approxTokens = await Task.Run(async () =>
            {
                var json = System.Text.Json.JsonSerializer.Serialize(bundle,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(outPath, json);
                return new FileInfo(outPath).Length / 4;
            });
            MessageBox.Show(
                $"Digest written to:\n{outPath}\n\nApprox tokens: {approxTokens:N0}\nWindow: {windowDays}d",
                "Build digest (debug)",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Digest failed: " + ex.Message,
                "Build digest (debug)",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    });

    public GitHubRelease? AvailableUpdate
    {
        get => _availableUpdate;
        private set { _availableUpdate = value; OnPropertyChanged(); }
    }

    public Action<string, string, AlertSeverity>? ShowNotificationRequested { get; set; }

    public void UpdateStatusText()
    {
        var mounted = _serverManager.GetMountedServers();
        var total = _config.Servers.Count;
        var totalSpeed = _serverSpeeds.Values.Sum();
        var totalDownloads = _serverDownloadCounts.Values.Sum();

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
            StatusText = totalDownloads > 0 && totalSpeed > 0
                ? $"{baseStatus} | {FormatSpeed(totalSpeed)}"
                : baseStatus;
            return;
        }

        // Multi-server: summarize
        StatusText = connectedCount > 0
            ? $"{connectedCount}/{total} connected"
            : "No servers connected";

        // Append aggregate transfer info
        if (totalDownloads > 0 && totalSpeed > 0)
            StatusText += $" | {FormatSpeed(totalSpeed)}";
    }

    private static string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond >= 1024 * 1024)
            return $"{bytesPerSecond / (1024 * 1024):F1} MB/s";
        if (bytesPerSecond >= 1024)
            return $"{bytesPerSecond / 1024:F0} KB/s";
        return $"{bytesPerSecond:F0} B/s";
    }

    public void ShowNotification(string title, string message, AlertKind kind = AlertKind.General,
        AlertSeverity severity = AlertSeverity.Information, string serverId = "", string release = "", string incident = "")
    {
        Log.Information("{Title}: {Message}", title, message);
        Alerts.Publish(new(kind, severity, title, message, serverId, release, incident));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public interface IAsyncRelayCommand : ICommand
{
    Task ExecuteAsync(object? parameter);
}

public class RelayCommand : IAsyncRelayCommand
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
        => await ExecuteAsync(parameter);

    public async Task ExecuteAsync(object? parameter)
    {
        if (_asyncExecute != null)
            await _asyncExecute();
        else
            _syncExecute?.Invoke();
    }
}

public class RelayCommand<T> : IAsyncRelayCommand
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
        => await ExecuteAsync(parameter);

    public async Task ExecuteAsync(object? parameter)
    {
        var value = parameter is T typed ? typed : default;
        if (value == null) return;
        if (_asyncExecute != null)
            await _asyncExecute(value);
        else
            _syncExecute?.Invoke(value);
    }
}
