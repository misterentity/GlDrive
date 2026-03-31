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

    public ObservableCollection<SpreadJobVm> SpreadJobs { get; } = new();
    public ObservableCollection<SpreadFileVm> SpreadFileTransfers { get; } = new();
    public ObservableCollection<SpreadScoreVm> SpreadScoreboard { get; } = new();
    public ObservableCollection<string> SpreadSections { get; } = new();
    public ObservableCollection<AutoRaceLogVm> AutoRaceLog { get; } = new();

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
    public ICommand OpenSettingsCommand { get; }

    public bool HasSections => SpreadSections.Count > 0;
    public bool NeedSetup => SpreadSections.Count == 0;

    public SpreadViewModel(ServerManager serverManager, AppConfig config)
    {
        _serverManager = serverManager;
        _config = config;

        StartRaceCommand = new RelayCommand(StartRace);
        StopJobCommand = new RelayCommand(StopJob);
        OpenSettingsCommand = new RelayCommand(() => _openSettingsAction?.Invoke());

        RefreshSections();

        // Subscribe to auto-race detection events from both notification polling and IRC announces
        _serverManager.NewReleaseDetected += (serverId, serverName, category, release, remotePath) =>
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
                AddAutoRaceLog(category, release, serverName, "Detected"));
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

        var connectedIds = spread.GetConnectedServerIds();
        Log.Information("Spread pools connected: {Ids}", string.Join(", ", connectedIds));

        // Find servers with this section AND a connected spread pool
        var allWithSection = _config.Servers
            .Where(s => s.Enabled && s.SpreadSite.Sections.ContainsKey(SelectedSection))
            .ToList();

        var serverIds = allWithSection
            .Select(s => s.Id)
            .Where(id => connectedIds.Contains(id))
            .ToList();

        if (allWithSection.Count == 0)
        {
            SpreadStatus = $"No servers have section \"{SelectedSection}\" configured";
            return;
        }

        if (serverIds.Count == 0)
        {
            var names = string.Join(", ", allWithSection.Select(s => s.Name));
            SpreadStatus = $"Servers with [{SelectedSection}] ({names}) have no spread pool connected — check FTP connections";
            return;
        }

        if (serverIds.Count < 2)
        {
            var connectedName = _config.Servers.First(s => s.Id == serverIds[0]).Name;
            var allNames = string.Join(", ", allWithSection.Select(s => s.Name));
            SpreadStatus = $"Need 2+ connected servers for [{SelectedSection}] — only {connectedName} is connected (configured: {allNames})";
            return;
        }

        try
        {
            var names = serverIds.Select(id => _config.Servers.First(s => s.Id == id).Name);
            spread.StartRace(SelectedSection, SpreadReleaseName, serverIds, SpreadMode.Race);
            SpreadStatus = $"Race started: {SpreadReleaseName} [{SelectedSection}] on {string.Join(", ", names)}";
            Log.Information("Manual race started: {Release} [{Section}] on {Servers}",
                SpreadReleaseName, SelectedSection, string.Join(", ", names));
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
                SpreadJobs.RemoveAt(i);
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
        }

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
                AddAutoRaceLog(section, release, "", result));
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
