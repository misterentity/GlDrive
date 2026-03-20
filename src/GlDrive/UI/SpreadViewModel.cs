using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Threading;
using GlDrive.Config;
using GlDrive.Services;
using GlDrive.Spread;

namespace GlDrive.UI;

public class SpreadViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ServerManager _serverManager;
    private readonly AppConfig _config;
    private readonly DispatcherTimer _refreshTimer;
    private string _selectedSection = "";
    private string _spreadReleaseName = "";
    private SpreadJobVm? _selectedSpreadJob;

    public ObservableCollection<SpreadJobVm> SpreadJobs { get; } = new();
    public ObservableCollection<SpreadFileVm> SpreadFileTransfers { get; } = new();
    public ObservableCollection<SpreadScoreVm> SpreadScoreboard { get; } = new();
    public ObservableCollection<string> SpreadSections { get; } = new();

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

    public SpreadJobVm? SelectedSpreadJob
    {
        get => _selectedSpreadJob;
        set
        {
            _selectedSpreadJob = value;
            OnPropertyChanged();
            RefreshScoreboard();
        }
    }

    public ICommand StartRaceCommand { get; }
    public ICommand StopJobCommand { get; }

    public SpreadViewModel(ServerManager serverManager, AppConfig config)
    {
        _serverManager = serverManager;
        _config = config;

        StartRaceCommand = new RelayCommand(StartRace);
        StopJobCommand = new RelayCommand(StopJob);

        // Build union of all server sections
        RefreshSections();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refreshTimer.Tick += (_, _) => RefreshFromManager();
    }

    private void RefreshSections()
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
    }

    private void StartRace()
    {
        if (string.IsNullOrWhiteSpace(SpreadReleaseName) || string.IsNullOrWhiteSpace(SelectedSection))
            return;

        var spread = _serverManager.Spread;
        if (spread == null) return;

        // Find all servers with this section configured
        var serverIds = _config.Servers
            .Where(s => s.Enabled && s.SpreadSite.Sections.ContainsKey(SelectedSection))
            .Select(s => s.Id)
            .Where(id => spread.GetConnectedServerIds().Contains(id))
            .ToList();

        if (serverIds.Count < 2) return;

        spread.StartRace(SelectedSection, SpreadReleaseName, serverIds, SpreadMode.Race);
    }

    private void StopJob()
    {
        if (SelectedSpreadJob == null) return;
        _serverManager.Spread?.StopJob(SelectedSpreadJob.Id);
    }

    private void RefreshFromManager()
    {
        var spread = _serverManager.Spread;
        if (spread == null) return;

        var jobs = spread.ActiveJobs;

        // Update job list
        var existingIds = SpreadJobs.Select(j => j.Id).ToHashSet();
        var currentIds = jobs.Select(j => j.Id).ToHashSet();

        // Remove completed/gone jobs
        foreach (var vm in SpreadJobs.Where(j => !currentIds.Contains(j.Id)).ToList())
            SpreadJobs.Remove(vm);

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

            var totalFiles = job.Sites.Values.Max(s => s.FilesTotal);
            var avgOwned = totalFiles > 0
                ? job.Sites.Values.Where(s => !_config.Servers.First(sc => sc.Id == s.ServerId).SpreadSite.DownloadOnly)
                    .Average(s => s.FilesOwned * 100.0 / totalFiles)
                : 0;
            vm.ProgressPercent = avgOwned;
            vm.Status = job.State.ToString();
        }

        RefreshScoreboard();
    }

    private void RefreshScoreboard()
    {
        SpreadScoreboard.Clear();

        if (SelectedSpreadJob == null) return;

        var spread = _serverManager.Spread;
        if (spread == null) return;

        var job = spread.ActiveJobs.FirstOrDefault(j => j.Id == SelectedSpreadJob.Id);
        if (job == null) return;

        foreach (var (_, site) in job.Sites)
        {
            SpreadScoreboard.Add(new SpreadScoreVm
            {
                SiteName = site.ServerName,
                FilesOwned = site.FilesOwned,
                FilesTotal = site.FilesTotal,
                SpeedBps = site.SpeedBps,
                Status = site.IsComplete ? "Complete" : site.ActiveTransfers > 0 ? $"Transferring ({site.ActiveTransfers})" : "Waiting"
            });
        }
    }

    public void Activate() => _refreshTimer.Start();
    public void Deactivate() => _refreshTimer.Stop();

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        _refreshTimer.Stop();
        GC.SuppressFinalize(this);
    }
}
