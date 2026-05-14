using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using GlDrive.Config;
using GlDrive.Services;
using GlDrive.Spread;
using Serilog;

namespace GlDrive.UI;

public class SpreadViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ServerManager _serverManager;
    private readonly AppConfig _config;
    private readonly DispatcherTimer _refreshTimer;
    private Action? _openSettingsAction;
    private string _selectedSection = "";
    private string _spreadReleaseName = "";
    private string _spreadStatus = "";
    private SpreadJobVm? _selectedSpreadJob;
    private bool _isRefreshing;

    // Per-job max-observed speed for adaptive sparkline scaling. Keyed by
    // SpreadJob.Id. Pruned in RefreshFromManager when the job is removed
    // from SpreadJobs so the dictionary doesn't leak.
    private readonly Dictionary<string, double> _jobMaxSpeed = new();

    public ObservableCollection<SpreadJobVm> SpreadJobs { get; } = new();
    public ObservableCollection<SpreadFileVm> SpreadFileTransfers { get; } = new();
    public ObservableCollection<SpreadScoreVm> SpreadScoreboard { get; } = new();
    public ObservableCollection<string> SpreadSections { get; } = new();
    public ObservableCollection<AutoRaceLogVm> AutoRaceLog { get; } = new();
    public ObservableCollection<RaceHistoryVm> RaceHistory { get; } = new();
    public ObservableCollection<AutoRaceFeedItem> AutoRaceFeed { get; } = new();

    public int LeaderScore => SpreadJobs.Count > 0 ? SpreadJobs.Max(j => j.Score) : 0;
    public int QueueDepth => SpreadJobs.Count;

    public string SelectedSection
    {
        get => _selectedSection;
        set { _selectedSection = value; OnPropertyChanged(); }
    }

    public string SpreadReleaseName
    {
        get => _spreadReleaseName;
        set { _spreadReleaseName = value; OnPropertyChanged(); }
    }

    public string SpreadStatus
    {
        get => _spreadStatus;
        set { _spreadStatus = value; OnPropertyChanged(); }
    }

    public SpreadJobVm? SelectedSpreadJob
    {
        get => _selectedSpreadJob;
        set
        {
            _selectedSpreadJob = value;
            OnPropertyChanged();
        }
    }

    public ICommand StartRaceCommand { get; }
    public ICommand StopJobCommand { get; }
    public ICommand PauseJobCommand { get; }
    public ICommand ResumeJobCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand ShowRaceDetailCommand { get; }

    public bool HasSections => SpreadSections.Count > 0;
    public bool NeedSetup => SpreadSections.Count == 0;

    public SpreadViewModel(ServerManager serverManager, AppConfig config)
    {
        _serverManager = serverManager;
        _config = config;

        StartRaceCommand = new RelayCommand(StartRace);
        StopJobCommand = new RelayCommand(StopJob);
        PauseJobCommand = new RelayCommand<SpreadJobVm>(PauseJob);
        ResumeJobCommand = new RelayCommand<SpreadJobVm>(ResumeJob);
        OpenSettingsCommand = new RelayCommand(() => _openSettingsAction?.Invoke());
        ShowRaceDetailCommand = new RelayCommand<RaceHistoryVm>(ShowRaceDetail);

        RefreshSections();
        LoadHistory();

        // Subscribe to auto-race detection events from both notification polling and IRC announces
        _serverManager.NewReleaseDetected += (serverId, serverName, category, release, remotePath) =>
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                AddAutoRaceLog(category, release, serverName, "Detected");
                AddFeedItem(release, category, "DETECTED", serverName);
            });
        };

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refreshTimer.Tick += (_, _) => SafeRefresh();
    }

    public void RefreshSections()
    {
        SpreadSections.Clear();
        var sections = new HashSet<string>();
        foreach (var server in _config.Servers)
        {
            foreach (var section in server.SpreadSite.Sections.Keys)
                sections.Add(section);
        }
        foreach (var s in sections.OrderBy(s => s))
            SpreadSections.Add(s);

        if (SpreadSections.Count > 0 && string.IsNullOrEmpty(SelectedSection))
            SelectedSection = SpreadSections[0];

        OnPropertyChanged(nameof(HasSections));
        OnPropertyChanged(nameof(NeedSetup));
    }

    private void StartRace()
    {
        if (string.IsNullOrWhiteSpace(SpreadReleaseName))
        {
            SpreadStatus = "Enter a release name";
            return;
        }
        if (string.IsNullOrWhiteSpace(SelectedSection))
        {
            SpreadStatus = "Select a section";
            return;
        }

        var spread = _serverManager.Spread;
        if (spread == null)
        {
            SpreadStatus = "Spread engine not available";
            return;
        }

        // Pass ALL connected servers — the job will auto-discover which ones have the release
        var connectedIds = spread.GetConnectedServerIds();
        var serverIds = _config.Servers
            .Where(s => s.Enabled && connectedIds.Contains(s.Id) && s.SpreadSite.Sections.Count > 0)
            .Select(s => s.Id)
            .ToList();

        if (serverIds.Count < 2)
        {
            var connectedNames = _config.Servers
                .Where(s => connectedIds.Contains(s.Id))
                .Select(s => s.Name);
            SpreadStatus = $"Need 2+ connected servers with sections configured (connected: {string.Join(", ", connectedNames)})";
            return;
        }

        try
        {
            spread.StartRace(SelectedSection, SpreadReleaseName, serverIds, SpreadMode.Race);
            SpreadStatus = $"Race queued: {SpreadReleaseName} — scanning {serverIds.Count} servers for release...";
        }
        catch (Exception ex)
        {
            SpreadStatus = $"Race failed: {ex.Message}";
            Log.Warning(ex, "StartRace failed for {Release}", SpreadReleaseName);
        }
    }

    private void StopJob()
    {
        if (SelectedSpreadJob == null) return;
        _serverManager.Spread?.StopJob(SelectedSpreadJob.Id);
    }

    private void PauseJob(SpreadJobVm? vm)
    {
        if (vm == null) return;
        var spread = _serverManager.Spread;
        if (spread == null) return;
        var job = spread.ActiveJobs.FirstOrDefault(j => j.Id == vm.Id);
        job?.Pause();
    }

    private void ResumeJob(SpreadJobVm? vm)
    {
        if (vm == null) return;
        var spread = _serverManager.Spread;
        if (spread == null) return;
        var job = spread.ActiveJobs.FirstOrDefault(j => j.Id == vm.Id);
        job?.Resume();
    }

    private void SafeRefresh()
    {
        if (_isRefreshing) return;
        _isRefreshing = true;
        try
        {
            RefreshFromManager();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Spread refresh error");
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private void RefreshFromManager()
    {
        var spread = _serverManager.Spread;
        if (spread == null) return;

        var jobs = spread.ActiveJobs;
        var currentIds = jobs.Select(j => j.Id).ToHashSet();

        // Remove gone jobs
        for (int i = SpreadJobs.Count - 1; i >= 0; i--)
        {
            if (!currentIds.Contains(SpreadJobs[i].Id))
            {
                // Prune per-job sparkline scale state so the dictionary
                // doesn't leak across the app's lifetime.
                _jobMaxSpeed.Remove(SpreadJobs[i].Id);
                SpreadJobs.RemoveAt(i);
            }
        }

        // Update or add jobs — update in-place to avoid collection churn
        foreach (var job in jobs)
        {
            var vm = SpreadJobs.FirstOrDefault(j => j.Id == job.Id);
            if (vm == null)
            {
                vm = new SpreadJobVm { Id = job.Id };
                SpreadJobs.Add(vm);
            }

            vm.Release = job.ReleaseName;
            vm.Section = job.Section;
            vm.State = job.State;

            var sites = job.Sites.Values.ToList();
            var totalFiles = sites.Count > 0 ? sites.Max(s => s.FilesTotal) : 0;

            double avgOwned = 0;
            if (totalFiles > 0)
            {
                var nonDownloadOnly = sites.Where(s =>
                {
                    var cfg = _config.Servers.FirstOrDefault(sc => sc.Id == s.ServerId);
                    return cfg != null && !cfg.SpreadSite.DownloadOnly;
                }).ToList();

                if (nonDownloadOnly.Count > 0)
                    avgOwned = nonDownloadOnly.Average(s => s.FilesOwned * 100.0 / totalFiles);
            }

            vm.ProgressPercent = avgOwned;
            vm.Status = job.State.ToString();

            // Card display fields (best-effort population from available data)
            var srcSite = sites.FirstOrDefault(s => s.FilesOwned > 0 && s.FilesOwned == sites.Max(x => x.FilesOwned));
            vm.SourceDisplay = srcSite != null && !string.IsNullOrEmpty(srcSite.ServerName) ? srcSite.ServerName : "—";

            var destNames = sites
                .Where(s => s.ServerId != srcSite?.ServerId && !string.IsNullOrEmpty(s.ServerName))
                .Select(s => s.ServerName)
                .ToList();
            vm.DestDisplay = destNames.Count > 0 ? string.Join(" · ", destNames) : "—";

            var totalOwnedAcross = sites.Sum(s => s.FilesOwned);
            var aggregateTotal = totalFiles > 0 ? totalFiles * Math.Max(sites.Count, 1) : 0;
            vm.FilesDoneTotalDisplay = totalFiles > 0
                ? $"{totalOwnedAcross} / {aggregateTotal}"
                : "0 / 0";

            vm.IsPaused = job.State == SpreadJobState.Paused;
            // Real wiring (v1.90): SpreadJob now exposes IsAutoRace / IsPred /
            // Score directly. ScoreLabel encodes a one-word state line
            // (PRED takes precedence; auto-race vs manual otherwise; PAUSED /
            // STOPPED override). The big-score card pulls from vm.Score.
            vm.IsAutoRace = job.IsAutoRace;
            vm.IsPred = job.IsPred;
            vm.Score = job.Score;
            vm.ScoreLabel = job.State switch
            {
                SpreadJobState.Paused    => "PAUSED",
                SpreadJobState.Stopped   => "STOPPED",
                SpreadJobState.Failed    => "FAILED",
                SpreadJobState.Completed => "DONE",
                _ => job.IsPred ? "PRE" : (job.IsAutoRace ? "AUTO RACE" : "MANUAL")
            };
        }

        OnPropertyChanged(nameof(LeaderScore));
        OnPropertyChanged(nameof(QueueDepth));

        // Update scoreboard and transfers only if a job is selected
        var selectedId = SelectedSpreadJob?.Id;
        if (selectedId == null)
        {
            if (SpreadScoreboard.Count > 0) SpreadScoreboard.Clear();
            if (SpreadFileTransfers.Count > 0) SpreadFileTransfers.Clear();
            return;
        }

        var selectedJob = jobs.FirstOrDefault(j => j.Id == selectedId);
        if (selectedJob == null)
        {
            if (SpreadScoreboard.Count > 0) SpreadScoreboard.Clear();
            if (SpreadFileTransfers.Count > 0) SpreadFileTransfers.Clear();
            return;
        }

        // Update scoreboard in-place
        var siteList = selectedJob.Sites.Values.ToList();
        // Resize to match
        while (SpreadScoreboard.Count > siteList.Count)
            SpreadScoreboard.RemoveAt(SpreadScoreboard.Count - 1);

        for (int i = 0; i < siteList.Count; i++)
        {
            var site = siteList[i];
            if (i < SpreadScoreboard.Count)
            {
                var existing = SpreadScoreboard[i];
                existing.SiteName = site.ServerName;
                existing.FilesOwned = site.FilesOwned;
                existing.FilesTotal = site.FilesTotal;
                existing.SpeedBps = site.SpeedBps;
                existing.Status = site.IsComplete ? "Complete"
                    : site.ActiveTransfers > 0 ? $"Transferring ({site.ActiveTransfers})"
                    : "Waiting";
            }
            else
            {
                SpreadScoreboard.Add(new SpreadScoreVm
                {
                    SiteName = site.ServerName,
                    FilesOwned = site.FilesOwned,
                    FilesTotal = site.FilesTotal,
                    SpeedBps = site.SpeedBps,
                    Status = site.IsComplete ? "Complete"
                        : site.ActiveTransfers > 0 ? $"Transferring ({site.ActiveTransfers})"
                        : "Waiting"
                });
            }
        }

        // Update file transfers — these change rapidly so replace if different
        var transfers = selectedJob.ActiveTransferList;
        // Quick check: if same count and same filenames, update in-place
        bool canUpdateInPlace = SpreadFileTransfers.Count == transfers.Count;
        if (canUpdateInPlace)
        {
            for (int i = 0; i < transfers.Count; i++)
            {
                if (SpreadFileTransfers[i].FileName != transfers[i].FileName ||
                    SpreadFileTransfers[i].Source != transfers[i].SourceName)
                {
                    canUpdateInPlace = false;
                    break;
                }
            }
        }

        if (canUpdateInPlace)
        {
            for (int i = 0; i < transfers.Count; i++)
            {
                var t = transfers[i];
                var vm = SpreadFileTransfers[i];
                vm.SpeedBps = t.SpeedBps;
                vm.ProgressPercent = t.ProgressPercent;
            }
        }
        else
        {
            SpreadFileTransfers.Clear();
            foreach (var t in transfers)
            {
                SpreadFileTransfers.Add(new SpreadFileVm
                {
                    FileName = t.FileName,
                    Size = t.FileSize,
                    Source = t.SourceName,
                    Dest = t.DestName,
                    SpeedBps = t.SpeedBps,
                    ProgressPercent = t.ProgressPercent,
                    Status = "Transferring"
                });
            }
        }
    }

    public void SetOpenSettingsAction(Action action) => _openSettingsAction = action;
    public void Activate() => _refreshTimer.Start();
    public void Deactivate() => _refreshTimer.Stop();

    public void WireAutoRaceEvents()
    {
        var spread = _serverManager.Spread;
        if (spread == null) return;
        spread.AutoRaceAttempted += (section, release, result) =>
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                AddAutoRaceLog(section, release, "", result);
                AddFeedItem(release, section, "ATTEMPT", result);
            });
        spread.JobStarted += job =>
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                AddFeedItem(job.ReleaseName, job.Section, "STARTED", "");
                SafeRefresh();
            });
        spread.JobCompleted += job =>
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                var action = job.State == SpreadJobState.Completed ? "DONE"
                    : job.State == SpreadJobState.Failed ? "FAILED"
                    : job.State == SpreadJobState.Paused ? "PAUSED"
                    : job.State == SpreadJobState.Stopped ? "STOPPED"
                    : job.State.ToString().ToUpperInvariant();
                AddFeedItem(job.ReleaseName, job.Section, action, "");
                SafeRefresh();
                RefreshHistory();
            });
        spread.JobProgressChanged += job =>
            Application.Current?.Dispatcher.BeginInvoke(() => TickSparkline(job));
    }

    private void TickSparkline(SpreadJob job)
    {
        try
        {
            var vm = SpreadJobs.FirstOrDefault(j => j.Id == job.Id);
            if (vm == null) return;
            var pts = vm.SparklinePoints;
            var totalSpeed = job.Sites.Values.Sum(s => s.SpeedBps);

            // Adaptive Y scaling per job — the chart fills from the bottom
            // (Y=40) to the top (Y=0) when speed hits the per-job max-observed
            // value. This avoids the old hardcoded 5 MB/s ceiling clipping
            // fast races to a flat line at the top. Pruned in
            // RefreshFromManager when the job is removed.
            if (!_jobMaxSpeed.TryGetValue(job.Id, out var maxSpeed) || totalSpeed > maxSpeed)
                _jobMaxSpeed[job.Id] = maxSpeed = totalSpeed;
            if (maxSpeed <= 0) maxSpeed = 1; // avoid div-by-zero on first tick

            var y = 40.0 - Math.Min(40.0, totalSpeed / maxSpeed * 40.0);

            // Shift left: drop oldest, re-index remaining, append new
            if (pts.Count >= 60) pts.RemoveAt(0);
            // Re-index x coords so polyline doesn't drift right forever
            for (int i = 0; i < pts.Count; i++)
                pts[i] = new Point(i, pts[i].Y);
            pts.Add(new Point(pts.Count, y));
        }
        catch
        {
            // Sparkline is decorative — never let it break the VM
        }
    }

    private void AddFeedItem(string release, string section, string action, string detail)
    {
        AutoRaceFeed.Insert(0, new AutoRaceFeedItem
        {
            Timestamp = DateTime.Now.ToString("HH:mm:ss"),
            Release = release ?? "",
            Section = section ?? "",
            Action = action ?? "",
            Detail = detail ?? ""
        });
        while (AutoRaceFeed.Count > 50)
            AutoRaceFeed.RemoveAt(AutoRaceFeed.Count - 1);
    }

    private void AddAutoRaceLog(string section, string release, string source, string result)
    {
        AutoRaceLog.Insert(0, new AutoRaceLogVm
        {
            Time = DateTime.Now.ToString("HH:mm:ss"),
            Section = section,
            Release = release,
            Source = source,
            Result = result
        });
        while (AutoRaceLog.Count > 200)
            AutoRaceLog.RemoveAt(AutoRaceLog.Count - 1);
    }

    private void LoadHistory()
    {
        var spread = _serverManager.Spread;
        if (spread == null) return;
        foreach (var item in spread.History.Items.Take(100))
        {
            RaceHistory.Add(new RaceHistoryVm
            {
                Id = item.Id,
                Time = item.StartedAt.ToLocalTime().ToString("MM-dd HH:mm"),
                Release = item.ReleaseName,
                Section = item.Section,
                Result = item.Result.ToString(),
                Sites = item.SiteNames,
                Files = item.FilesTransferred,
                Size = FormatSize(item.BytesTransferred),
                SkiplistResult = item.SkiplistResult,
                Duration = FormatDuration(item.CompletedAt - item.StartedAt),
                SkiplistTrace = item.SkiplistTrace
            });
        }
    }

    public void RefreshHistory()
    {
        RaceHistory.Clear();
        LoadHistory();
    }

    private static void ShowRaceDetail(RaceHistoryVm? item)
    {
        if (item == null) return;

        var trace = item.SkiplistTrace;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Release: {item.Release}");
        sb.AppendLine($"Section: {item.Section}");
        sb.AppendLine($"Result: {item.Result}");
        sb.AppendLine($"Skiplist: {item.SkiplistResult}");
        sb.AppendLine($"Sites: {item.Sites}");
        sb.AppendLine($"Files: {item.Files}  Size: {item.Size}  Duration: {item.Duration}");
        sb.AppendLine();

        if (trace != null && trace.Count > 0)
        {
            sb.AppendLine("=== Skiplist Rule Evaluation ===");
            sb.AppendLine();
            foreach (var t in trace)
            {
                var icon = t.IsMatch ? ">> " : "   ";
                sb.AppendLine($"{icon}[{t.Source}] {t.Action}: {t.Pattern} → {t.Result}");
            }
        }
        else
        {
            sb.AppendLine("(No skiplist trace available for this race)");
        }

        MessageBox.Show(sb.ToString(), $"Race Detail — {item.Release}",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
    };

    private static string FormatDuration(TimeSpan ts) => ts.TotalMinutes >= 1
        ? $"{(int)ts.TotalMinutes}m {ts.Seconds}s"
        : $"{ts.TotalSeconds:F0}s";

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        _refreshTimer.Stop();
        GC.SuppressFinalize(this);
    }
}

public class AutoRaceLogVm
{
    public string Time { get; set; } = "";
    public string Section { get; set; } = "";
    public string Release { get; set; } = "";
    public string Source { get; set; } = "";
    public string Result { get; set; } = "";
}

public class AutoRaceFeedItem
{
    public string Timestamp { get; set; } = "";  // HH:mm:ss
    public string Release { get; set; } = "";
    public string Section { get; set; } = "";
    public string Action { get; set; } = "";     // STARTED / DONE / PAUSED / FAILED
    public string Detail { get; set; } = "";
}

public class RaceHistoryVm
{
    public string Id { get; set; } = "";
    public string Time { get; set; } = "";
    public string Release { get; set; } = "";
    public string Section { get; set; } = "";
    public string Result { get; set; } = "";
    public string Sites { get; set; } = "";
    public int Files { get; set; }
    public string Size { get; set; } = "";
    public string Duration { get; set; } = "";
    public string SkiplistResult { get; set; } = "";
    public List<SkiplistTraceEntry>? SkiplistTrace { get; set; }
}
