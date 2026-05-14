using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Services;
using GlDrive.Spread;
using Microsoft.Win32;
using Serilog;

namespace GlDrive.UI;

public class DashboardViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ServerManager _serverManager;
    private readonly AppConfig _config;
    private readonly WishlistStore _wishlistStore;
    private readonly NotificationStore _notificationStore;
    private readonly IrcViewModel _ircViewModel;
    private string _searchQuery = "";
    private string _searchStatus = "";
    private bool _isSearching;
    private CancellationTokenSource? _searchCts;
    private readonly Dictionary<string, MountService> _subscribedServers = new();
    private readonly Action<string, string, MountState> _serverStateHandler;
    private readonly Action<string, string, string, string, string> _newReleaseHandler;
    private WishlistItemVm? _selectedWishlistItem;
    private DownloadItemVm? _selectedDownloadItem;
    private SearchResultVm? _selectedSearchResult;
    private NotificationItemVm? _selectedNotificationItem;
    private string _activeDownloadSummary = "";
    private bool _hasActiveDownload;
    private readonly Dictionary<string, (string name, double speed, double pct)> _activeDownloads = new();
    private UpcomingTvEpisodeVm? _selectedTvEpisode;
    private UpcomingMovieVm? _selectedUpcomingMovie;
    private string _upcomingQualityText = "1080p";
    private string _upcomingStatus = "";
    private DateTime _tvCacheTime;
    private DateTime _movieCacheTime;
    private string _showSearchQuery = "";
    private bool _isShowSearchActive;
    private List<UpcomingTvEpisodeVm>? _cachedTvSchedule;
    private string _tvTypeFilter = "Scripted";

    // PreDB
    private readonly PreDbClient _preDbClient = new();
    private string _preDbQuery = "";
    private string _preDbStatus = "";
    private bool _isPreDbSearching;
    private CancellationTokenSource? _preDbCts;
    private PreDbItemVm? _selectedPreDbItem;
    private bool _isPreDbTabActive;
    private DispatcherTimer? _preDbRefreshTimer;
    private DispatcherTimer? _preDbCountdownTimer;
    private int _preDbRefreshProgress;
    private DateTime _preDbNextRefresh;
    private string _preDbSectionFilter = "All";
    private readonly List<PreDbItemVm> _allPreDbItems = new();
    private int _preDbHighestId;

    // Notification filter state
    private List<NotificationItemVm> _allNotifications = new();
    private string _notificationFilterText = "";
    private string _notificationFilterCategory = "All";
    private string _notificationFilterServer = "All";

    // Status bar
    private string _statusBarSpeed = "";
    private string _statusBarQueueCount = "";
    private string _statusBarDiskSpace = "";
    private string _statusBarConnections = "";
    private string _statusBarSites = "";
    private DispatcherTimer? _statusTimer;
    private DateTime _lastDiskPoll = DateTime.MinValue;

    // Per-server SITE STATS disk capacity cache. Populated by PollServerDiskUsage
    // (status-bar 30s poll); read by RefreshOverview() so the Overview/Mounts
    // disc widgets show real disk-space % instead of pool-utilization fallback.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long totalBytes, long freeBytes)> _serverDiskCache = new();

    // Bandwidth graph
    private readonly List<double> _speedHistory = new();
    private PointCollection _speedGraphPoints = new();
    private readonly Dictionary<string, double> _serverSpeeds = new();

    // Overview live telemetry (v1.85)
    private DispatcherTimer? _overviewTimer;
    private PointCollection _throughputPoints = new();
    private string _rxRateDisplay = "-- MB/s";
    private string _txRateDisplay = "-- MB/s";
    private double _telemetryCpuPercent;
    private double _telemetryMemoryMb;
    private double _telemetryPoolUtilization;
    private string _telemetryUptime = "0m 00s";
    private int _telemetryThreadCount;
    private readonly Random _overviewRng = new();

    public IrcViewModel Irc => _ircViewModel;

    public ObservableCollection<NotificationItemVm> NotificationItems { get; } = new();
    public ObservableCollection<WishlistItemVm> WishlistItems { get; } = new();
    public ObservableCollection<DownloadItemVm> DownloadItems { get; } = new();

    // Stat card values for the Downloads tab. Computed live each time the
    // download collection refreshes; the dashboard polls these via property
    // change notifications fired after RefreshDownloads().
    public int ActiveDownloadCount => DownloadItems.Count(d =>
        d.Status == "Downloading" || d.Status == "Extracting" || d.Status == "Verifying");
    public int QueuedDownloadCount => DownloadItems.Count(d => d.Status == "Queued");
    public int FailedDownloadCount => DownloadItems.Count(d =>
        d.Status == "Failed" || d.Status == "Cancelled");
    public int CompletedTodayCount => DownloadItems.Count(d => d.Status == "Completed");

    // Overview tab data. RefreshOverview() rebuilds these.
    public ObservableCollection<OverviewServerVm> MountedServerStatus { get; } = new();
    public int MountedServerCount => MountedServerStatus.Count;
    public int ConfiguredServerCount => _config.Servers.Count;
    public string OperationsLogPreview { get; private set; } = "loading...";

    // Mounts tab: TOFU trusted-cert list. RefreshMounts() rebuilds.
    public ObservableCollection<TrustedCertVm> TrustedCerts { get; } = new();

    // Overview throughput sparkline (rolling 60 samples; Y in [0..40] relative
    // to a 40px chart height). Mutated each DispatcherTimer tick.
    // Setters must be public (not private) for WPF bindings to bind, even
    // when the binding direction is OneWay — the binding engine reflects on
    // the property and rejects it as read-only otherwise (XamlParseException
    // "A TwoWay or OneWayToSource binding cannot work on the read-only
    // property" surfaced in v1.85 crash logs).
    public PointCollection ThroughputPoints
    {
        get => _throughputPoints;
        set { _throughputPoints = value; OnPropertyChanged(); }
    }
    public string RxRateDisplay
    {
        get => _rxRateDisplay;
        set { if (_rxRateDisplay == value) return; _rxRateDisplay = value; OnPropertyChanged(); }
    }
    public string TxRateDisplay
    {
        get => _txRateDisplay;
        set { if (_txRateDisplay == value) return; _txRateDisplay = value; OnPropertyChanged(); }
    }
    public double TelemetryCpuPercent
    {
        get => _telemetryCpuPercent;
        set { if (Math.Abs(_telemetryCpuPercent - value) < 0.01) return; _telemetryCpuPercent = value; OnPropertyChanged(); }
    }
    public double TelemetryMemoryMb
    {
        get => _telemetryMemoryMb;
        set { if (Math.Abs(_telemetryMemoryMb - value) < 0.01) return; _telemetryMemoryMb = value; OnPropertyChanged(); }
    }
    public double TelemetryPoolUtilization
    {
        get => _telemetryPoolUtilization;
        set { if (Math.Abs(_telemetryPoolUtilization - value) < 0.01) return; _telemetryPoolUtilization = value; OnPropertyChanged(); }
    }
    public string TelemetryUptime
    {
        get => _telemetryUptime;
        set { if (_telemetryUptime == value) return; _telemetryUptime = value; OnPropertyChanged(); }
    }
    public int TelemetryThreadCount
    {
        get => _telemetryThreadCount;
        set { if (_telemetryThreadCount == value) return; _telemetryThreadCount = value; OnPropertyChanged(); }
    }

    public void RefreshOverview()
    {
        MountedServerStatus.Clear();
        foreach (var server in _config.Servers)
        {
            // CONNECTED = ServerManager has a live MountService (FTP session up).
            // MOUNTED  = connected AND the user opted into a Windows drive letter
            //            via Mount.MountDrive in the per-server config.
            // Earlier versions conflated the two — every connected server showed
            // as MOUNTED. Now: if MountDrive is off, we say CONNECTED.
            var service = _serverManager.GetServer(server.Id);
            var isConnected = service != null;
            var wantsDrive = server.Mount?.MountDrive ?? false;
            var isMounted = isConnected && wantsDrive;
            var driveLetter = server.Mount?.DriveLetter ?? "";

            // Disc widget %: prefer real SITE STATS disk-space % (cached by
            // PollServerDiskUsage every 30s); fall back to connection-pool
            // utilization when we haven't successfully polled this server yet.
            double poolPct = 0;
            int poolActive = 0, poolMax = 0;
            if (service?.Pool is { } pool && pool.MaxSize > 0)
            {
                poolActive = pool.ActiveCount;
                poolMax = pool.MaxSize;
                poolPct = Math.Min(100.0, (double)poolActive / poolMax * 100.0);
            }

            // Disc widget % always shows pool utilization. SITE STATS-based
            // disk% was tried in v1.92c but only one server (superbnc) returned
            // data; the others showed 0% which was worse UX than the consistent
            // pool reading. _serverDiskCache is still populated (used by the
            // status-bar disk-space string) but no longer drives the disc widget.
            double capacityPct = poolPct;
            string usedDisplay = poolMax > 0 ? poolActive.ToString() : "—";
            string totalDisplay = poolMax > 0 ? poolMax.ToString() : "—";

            string status;
            if (isMounted)
                status = string.IsNullOrEmpty(driveLetter) ? "MOUNTED" : $"MOUNTED  {driveLetter}:";
            else if (isConnected)
                status = "CONNECTED";
            else if (server.Enabled)
                status = "OFFLINE";
            else
                status = "DISABLED";

            MountedServerStatus.Add(new OverviewServerVm
            {
                ServerId = server.Id,
                Name = string.IsNullOrEmpty(server.Name) ? server.Connection?.Host ?? "(unnamed)" : server.Name,
                Host = $"{server.Connection?.Host}:{server.Connection?.Port}",
                IsMounted = isMounted,
                StatusLine = status,
                CapacityPercent = capacityPct,
                CapacityUsedDisplay = usedDisplay,
                CapacityTotalDisplay = totalDisplay,
                IsPrimary = false,
                SiteTag = isMounted ? "MOUNTED" : (isConnected ? "CONNECTED" : (server.Enabled ? "OFFLINE" : "DISABLED"))
            });
        }

        // Tag the first connected (mounted or just connected) server as PRIMARY,
        // subsequent connected as PEER. Pure-offline servers keep their offline tag.
        bool primaryAssigned = false;
        foreach (var vm in MountedServerStatus)
        {
            // Only the visually "alive" servers get PRIMARY/PEER treatment.
            if (vm.SiteTag == "MOUNTED" || vm.SiteTag == "CONNECTED")
            {
                if (!primaryAssigned)
                {
                    vm.IsPrimary = true;
                    vm.SiteTag = "PRIMARY";
                    primaryAssigned = true;
                }
                else
                {
                    vm.SiteTag = "PEER";
                }
            }
        }

        OnPropertyChanged(nameof(MountedServerCount));
        OnPropertyChanged(nameof(ConfiguredServerCount));

        try
        {
            var logsDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GlDrive", "logs");
            if (System.IO.Directory.Exists(logsDir))
            {
                var latest = new System.IO.DirectoryInfo(logsDir)
                    .GetFiles("gldrive-*.log")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault();
                if (latest != null)
                {
                    using var fs = new System.IO.FileStream(latest.FullName,
                        System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite);
                    using var sr = new System.IO.StreamReader(fs);
                    var lines = sr.ReadToEnd().Split('\n');
                    var tail = lines.Length > 12
                        ? string.Join('\n', lines.Skip(lines.Length - 12))
                        : string.Join('\n', lines);
                    OperationsLogPreview = tail;
                }
            }
        }
        catch { OperationsLogPreview = "(unable to read log)"; }
        OnPropertyChanged(nameof(OperationsLogPreview));

        RefreshTelemetry();
        EnsureThroughputSeed();
        StartOverviewLive();
    }

    // Mounts tab: reload the trusted-cert TOFU list from disk.
    // The on-disk format (see CertificateManager) is a dict keyed by "host:port"
    // mapping to { fingerprint, trustedAt }. We accept both camelCase
    // (current format) and PascalCase (defensive) property names.
    public void RefreshMounts()
    {
        TrustedCerts.Clear();
        var path = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GlDrive", "trusted_certs.json");
        if (!System.IO.File.Exists(path)) return;
        try
        {
            var json = System.IO.File.ReadAllText(path);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return;
            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                var hostKey = entry.Name;
                var node = entry.Value;
                if (node.ValueKind != System.Text.Json.JsonValueKind.Object) continue;

                string fp = TryGetString(node, "fingerprint", "Fingerprint");
                string subj = TryGetString(node, "subject", "Subject");
                string issuer = TryGetString(node, "issuer", "Issuer");
                string notAfter = TryGetString(node, "notAfter", "NotAfter");
                // Current format only persists trustedAt — surface it when NotAfter isn't set.
                if (string.IsNullOrEmpty(notAfter))
                    notAfter = TryGetString(node, "trustedAt", "TrustedAt");

                TrustedCerts.Add(new TrustedCertVm
                {
                    HostKey = hostKey,
                    Fingerprint = fp,
                    Subject = subj,
                    Issuer = issuer,
                    NotAfter = notAfter
                });
            }
        }
        catch { /* corrupt JSON — leave list empty */ }
    }

    private static string TryGetString(System.Text.Json.JsonElement node, params string[] names)
    {
        foreach (var n in names)
        {
            if (node.TryGetProperty(n, out var el) && el.ValueKind == System.Text.Json.JsonValueKind.String)
                return el.GetString() ?? "";
        }
        return "";
    }

    // Seed the throughput sparkline with 60 baseline points (Y = 20, the
    // midpoint of a 40px chart) so the Polyline has geometry on first paint.
    private void EnsureThroughputSeed()
    {
        if (_throughputPoints.Count == 60) return;
        var seed = new PointCollection();
        for (int i = 0; i < 60; i++) seed.Add(new Point(i, 20));
        ThroughputPoints = seed;
    }

    public void StartOverviewLive()
    {
        if (_overviewTimer != null) return;
        _overviewTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _overviewTimer.Tick += (_, _) => TickOverview();
        _overviewTimer.Start();
    }

    public void StopOverviewLive()
    {
        _overviewTimer?.Stop();
        _overviewTimer = null;
    }

    // CPU sampling: track previous TotalProcessorTime + wall-clock snapshot so
    // we can compute % over the last tick interval. cores normalizes to a
    // 0-100 (single-core) range visualizable in the telemetry bar.
    private TimeSpan _prevCpuTime;
    private DateTime _prevCpuSampleAt;
    private readonly int _cpuCores = Math.Max(1, Environment.ProcessorCount);

    // Latest reported download speed per item (key = item Id, value = BytesPerSecond).
    // Populated by DownloadProgressChanged subscriptions in WireLiveSpeedTracking,
    // cleared on download completion.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, double> _liveItemSpeeds = new();
    private readonly HashSet<string> _wiredManagerIds = new();

    private void EnsureLiveSpeedSubscriptions()
    {
        // Subscribe to each connected server's DownloadManager.DownloadProgressChanged
        // exactly once. Cheap to call repeatedly — the HashSet de-dupes.
        foreach (var srv in _serverManager.GetMountedServers())
        {
            var dm = srv.Downloads;
            if (dm == null) continue;
            if (!_wiredManagerIds.Add(srv.ServerId)) continue;
            dm.DownloadProgressChanged += (item, progress) =>
            {
                if (item == null) return;
                if (progress.BytesPerSecond <= 0)
                    _liveItemSpeeds.TryRemove(item.Id, out _);
                else
                    _liveItemSpeeds[item.Id] = progress.BytesPerSecond;

                // Mirror the live speed into the matching DownloadItemVm so
                // the Downloads grid's SpeedDisplay column shows real
                // MB/s per row, not just an aggregate.
                var mbps = progress.BytesPerSecond / (1024.0 * 1024.0);
                var display = progress.BytesPerSecond <= 0
                    ? ""
                    : (mbps >= 1.0 ? $"{mbps:0.0} MB/s" : $"{progress.BytesPerSecond / 1024.0:0} KB/s");
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    var vm = DownloadItems.FirstOrDefault(d => d.Id == item.Id);
                    if (vm != null) vm.SpeedDisplay = display;
                });
            };
        }
    }

    private void TickOverview()
    {
        // Make sure we're subscribed to every active server's progress feed.
        EnsureLiveSpeedSubscriptions();

        // Sum the latest reported speeds across all active items.
        double rxBytesPerSec = 0;
        foreach (var v in _liveItemSpeeds.Values) rxBytesPerSec += v;

        // TX = aggregate FXP throughput across active spread jobs. Each
        // SpreadJob's Sites collection has SpeedBps per peer; sum over the
        // non-source peers since that's the bytes flowing OUT of our app's
        // perspective. Spread runs server-to-server FXP so "out" here means
        // bytes the spread engine is pushing to peer destinations.
        double txBytesPerSec = 0;
        try
        {
            var spread = _serverManager.Spread;
            if (spread != null)
            {
                foreach (var job in spread.ActiveJobs)
                {
                    foreach (var s in job.Sites.Values)
                        if (!s.IsSource) txBytesPerSec += s.SpeedBps;
                }
            }
        }
        catch { /* spread snapshot is best-effort */ }

        var rxMb = rxBytesPerSec / (1024.0 * 1024.0);
        var txMb = txBytesPerSec / (1024.0 * 1024.0);

        RxRateDisplay = $"{rxMb:0.0} MB/s";
        TxRateDisplay = $"{txMb:0.0} MB/s";

        // Sparkline Y range is 0..40 (chart height). Scale rxMb against a soft
        // upper bound of 50 MB/s; anything above clamps to the top of the chart.
        var newY = 40.0 - Math.Min(40.0, rxMb / 50.0 * 40.0);
        var next = new PointCollection();
        for (int i = 1; i < _throughputPoints.Count; i++)
            next.Add(new Point(i - 1, _throughputPoints[i].Y));
        next.Add(new Point(59, newY));
        ThroughputPoints = next;

        RefreshTelemetry();
    }

    private void RefreshTelemetry()
    {
        try
        {
            var proc = Process.GetCurrentProcess();
            TelemetryMemoryMb = proc.WorkingSet64 / (1024.0 * 1024.0);
            TelemetryThreadCount = proc.Threads.Count;
            var uptime = DateTime.UtcNow - proc.StartTime.ToUniversalTime();
            TelemetryUptime = uptime.TotalHours >= 1
                ? $"{(int)uptime.TotalHours}h {uptime.Minutes:D2}m"
                : $"{uptime.Minutes}m {uptime.Seconds:D2}s";

            // Real CPU%: delta(TotalProcessorTime) / delta(wallClock) / cores * 100.
            // First tick has no previous sample → CPU stays at 0 until the second.
            var nowWall = DateTime.UtcNow;
            var nowCpu = proc.TotalProcessorTime;
            if (_prevCpuSampleAt != default)
            {
                var wallDelta = (nowWall - _prevCpuSampleAt).TotalMilliseconds;
                var cpuDelta = (nowCpu - _prevCpuTime).TotalMilliseconds;
                if (wallDelta > 0)
                {
                    var pct = cpuDelta / wallDelta / _cpuCores * 100.0;
                    TelemetryCpuPercent = Math.Min(100, Math.Max(0, pct));
                }
            }
            _prevCpuTime = nowCpu;
            _prevCpuSampleAt = nowWall;

            // Real pool utilization: average of ActiveCount/MaxSize across
            // mounted servers. ActiveCount is connections currently borrowed
            // (not idle in the channel).
            double sum = 0;
            int count = 0;
            foreach (var srv in _serverManager.GetMountedServers())
            {
                var p = srv.Pool;
                if (p == null || p.MaxSize <= 0) continue;
                sum += (double)p.ActiveCount / p.MaxSize * 100.0;
                count++;
            }
            TelemetryPoolUtilization = count > 0 ? sum / count : 0;
        }
        catch { /* telemetry is best-effort */ }
    }

    public ObservableCollection<SearchResultVm> SearchResults { get; } = new();

    // Search filter state — controls the CollectionView wrapping SearchResults.
    // The 6 chip buttons in the Search tab toolbar call ToggleSearchFilterCommand
    // with a "quality:1080p" / "source:WEB" / "size:1GB" / "clear" parameter.
    private string _searchFilterQuality = "";   // "" / "1080p" / "2160p"
    private string _searchFilterSource = "";    // "" / "WEB" / "BLURAY"
    private bool _searchFilterBigSize;          // true = require >= 1 GB
    public string SearchFilterQuality
    {
        get => _searchFilterQuality;
        set { _searchFilterQuality = value; OnPropertyChanged(); RefreshSearchView(); }
    }
    public string SearchFilterSource
    {
        get => _searchFilterSource;
        set { _searchFilterSource = value; OnPropertyChanged(); RefreshSearchView(); }
    }
    public bool SearchFilterBigSize
    {
        get => _searchFilterBigSize;
        set { _searchFilterBigSize = value; OnPropertyChanged(); RefreshSearchView(); }
    }
    public bool SearchFilterAnyActive =>
        !string.IsNullOrEmpty(_searchFilterQuality) || !string.IsNullOrEmpty(_searchFilterSource) || _searchFilterBigSize;

    private System.ComponentModel.ICollectionView? _searchResultsView;
    private void EnsureSearchView()
    {
        if (_searchResultsView != null) return;
        _searchResultsView = System.Windows.Data.CollectionViewSource.GetDefaultView(SearchResults);
        _searchResultsView.Filter = obj =>
        {
            if (obj is not SearchResultVm vm) return false;
            var name = vm.ReleaseName?.ToLowerInvariant() ?? "";
            if (!string.IsNullOrEmpty(_searchFilterQuality) && !name.Contains(_searchFilterQuality.ToLowerInvariant()))
                return false;
            if (!string.IsNullOrEmpty(_searchFilterSource) && !name.Contains(_searchFilterSource.ToLowerInvariant()))
                return false;
            if (_searchFilterBigSize && vm.Size < 1L * 1024 * 1024 * 1024)
                return false;
            return true;
        };
    }
    private void RefreshSearchView()
    {
        EnsureSearchView();
        _searchResultsView?.Refresh();
        OnPropertyChanged(nameof(SearchFilterAnyActive));
    }
    public ObservableCollection<UpcomingTvEpisodeVm> UpcomingTvEpisodes { get; } = new();
    public ObservableCollection<UpcomingMovieVm> UpcomingMovies { get; } = new();
    public ObservableCollection<PreDbItemVm> PreDbItems { get; } = new();
    public ObservableCollection<string> NotificationCategories { get; } = new() { "All" };
    public ObservableCollection<string> NotificationServers { get; } = new() { "All" };

    public WishlistItemVm? SelectedWishlistItem
    {
        get => _selectedWishlistItem;
        set
        {
            _selectedWishlistItem = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasWishlistSelection));
            OnPropertyChanged(nameof(HasSelectedPoster));
            OnPropertyChanged(nameof(SelectedPosterUrl));
            OnPropertyChanged(nameof(SelectedPlot));
            OnPropertyChanged(nameof(SelectedRating));
            OnPropertyChanged(nameof(SelectedGenres));
        }
    }

    public bool HasWishlistSelection => _selectedWishlistItem != null;
    public bool HasSelectedPoster => !string.IsNullOrEmpty(_selectedWishlistItem?.PosterUrl);
    public string? SelectedPosterUrl => _selectedWishlistItem?.PosterUrl;
    public string SelectedPlot => _selectedWishlistItem?.Plot ?? "";
    public string SelectedRating => _selectedWishlistItem?.Rating is { Length: > 0 } r ? $"\u2605 {r}/10" : "";
    public string SelectedGenres => _selectedWishlistItem?.Genres ?? "";

    public NotificationItemVm? SelectedNotificationItem
    {
        get => _selectedNotificationItem;
        set
        {
            _selectedNotificationItem = value;
            OnPropertyChanged();
            NotifyNotificationDetailChanged();
            if (value != null && !value.MetadataLoaded)
                _ = LoadNotificationMetadata(value);
        }
    }

    public bool HasNotificationSelection => _selectedNotificationItem != null;
    public bool HasNotificationPoster => !string.IsNullOrEmpty(_selectedNotificationItem?.PosterUrl);
    public string? NotificationPosterUrl => _selectedNotificationItem?.PosterUrl;
    public string NotificationPlot => _selectedNotificationItem?.Plot ?? "";
    public string NotificationRating => _selectedNotificationItem?.Rating is { Length: > 0 } r ? $"\u2605 {r}/10" : "";
    public string NotificationGenres => _selectedNotificationItem?.Genres ?? "";

    public DownloadItemVm? SelectedDownloadItem
    {
        get => _selectedDownloadItem;
        set { _selectedDownloadItem = value; OnPropertyChanged(); }
    }

    public SearchResultVm? SelectedSearchResult
    {
        get => _selectedSearchResult;
        set { _selectedSearchResult = value; OnPropertyChanged(); }
    }

    public UpcomingTvEpisodeVm? SelectedTvEpisode
    {
        get => _selectedTvEpisode;
        set
        {
            _selectedTvEpisode = value;
            if (value != null) _selectedUpcomingMovie = null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedUpcomingMovie));
            NotifyUpcomingDetailChanged();
        }
    }

    public UpcomingMovieVm? SelectedUpcomingMovie
    {
        get => _selectedUpcomingMovie;
        set
        {
            _selectedUpcomingMovie = value;
            if (value != null) _selectedTvEpisode = null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedTvEpisode));
            NotifyUpcomingDetailChanged();
        }
    }

    public bool HasUpcomingSelection => _selectedTvEpisode != null || _selectedUpcomingMovie != null;
    public bool HasUpcomingPoster => !string.IsNullOrEmpty(UpcomingPosterUrl);
    public string? UpcomingPosterUrl => _selectedTvEpisode?.PosterUrl ?? _selectedUpcomingMovie?.PosterUrl;
    public string UpcomingDetailTitle =>
        _selectedTvEpisode != null ? $"{_selectedTvEpisode.ShowName} — {_selectedTvEpisode.EpisodeInfo}" :
        _selectedUpcomingMovie != null ? $"{_selectedUpcomingMovie.Title} ({_selectedUpcomingMovie.Year})" : "";
    public string UpcomingRating =>
        _selectedTvEpisode?.Rating is { Length: > 0 } tr ? $"\u2605 {tr}/10" :
        _selectedUpcomingMovie?.Rating is { Length: > 0 } mr ? $"\u2605 {mr}/10" : "";
    public string UpcomingGenres => _selectedTvEpisode?.Genres ?? _selectedUpcomingMovie?.Genres ?? "";
    public string UpcomingPlot => _selectedTvEpisode?.Plot ?? _selectedUpcomingMovie?.Plot ?? "";

    public string UpcomingQualityText
    {
        get => _upcomingQualityText;
        set { _upcomingQualityText = value; OnPropertyChanged(); }
    }

    public string UpcomingStatus
    {
        get => _upcomingStatus;
        set { _upcomingStatus = value; OnPropertyChanged(); }
    }

    public string ShowSearchQuery
    {
        get => _showSearchQuery;
        set { _showSearchQuery = value; OnPropertyChanged(); }
    }

    public bool IsShowSearchActive
    {
        get => _isShowSearchActive;
        set { _isShowSearchActive = value; OnPropertyChanged(); OnPropertyChanged(nameof(TvScheduleHeader)); }
    }

    public string TvScheduleHeader => _isShowSearchActive ? "Search Results" : "TV Schedule (Next 7 Days)";

    public string[] TvTypeOptions { get; } = ["Scripted", "Reality", "Documentary", "All"];

    public string TvTypeFilter
    {
        get => _tvTypeFilter;
        set
        {
            _tvTypeFilter = value;
            OnPropertyChanged();
            ApplyTvTypeFilter();
        }
    }

    public string[] QualityOptions { get; } = ["Any", "SD", "720p", "1080p", "2160p"];
    public bool HasTmdbKey => !string.IsNullOrEmpty(_config.Downloads.ResolveTmdbKey());
    public bool HasNoTmdbKey => string.IsNullOrEmpty(_config.Downloads.ResolveTmdbKey());

    // Notification filter properties
    public string NotificationFilterText
    {
        get => _notificationFilterText;
        set { _notificationFilterText = value; OnPropertyChanged(); ApplyNotificationFilter(); }
    }

    public string NotificationFilterCategory
    {
        get => _notificationFilterCategory;
        set { _notificationFilterCategory = value; OnPropertyChanged(); ApplyNotificationFilter(); }
    }

    public string NotificationFilterServer
    {
        get => _notificationFilterServer;
        set { _notificationFilterServer = value; OnPropertyChanged(); ApplyNotificationFilter(); }
    }

    // Status bar properties
    public string StatusBarSpeed
    {
        get => _statusBarSpeed;
        set { _statusBarSpeed = value; OnPropertyChanged(); }
    }

    public string StatusBarQueueCount
    {
        get => _statusBarQueueCount;
        set { _statusBarQueueCount = value; OnPropertyChanged(); }
    }

    public string StatusBarDiskSpace
    {
        get => _statusBarDiskSpace;
        set { _statusBarDiskSpace = value; OnPropertyChanged(); }
    }

    public string StatusBarConnections
    {
        get => _statusBarConnections;
        set { _statusBarConnections = value; OnPropertyChanged(); }
    }

    public string StatusBarSites
    {
        get => _statusBarSites;
        set { _statusBarSites = value; OnPropertyChanged(); }
    }

    // Bandwidth graph
    public PointCollection SpeedGraphPoints
    {
        get => _speedGraphPoints;
        set { _speedGraphPoints = value; OnPropertyChanged(); }
    }

    private void NotifyUpcomingDetailChanged()
    {
        OnPropertyChanged(nameof(HasUpcomingSelection));
        OnPropertyChanged(nameof(HasUpcomingPoster));
        OnPropertyChanged(nameof(UpcomingPosterUrl));
        OnPropertyChanged(nameof(UpcomingDetailTitle));
        OnPropertyChanged(nameof(UpcomingRating));
        OnPropertyChanged(nameof(UpcomingGenres));
        OnPropertyChanged(nameof(UpcomingPlot));
    }

    // PreDB properties
    public string PreDbQuery
    {
        get => _preDbQuery;
        set { _preDbQuery = value; OnPropertyChanged(); }
    }

    public string PreDbStatus
    {
        get => _preDbStatus;
        set { _preDbStatus = value; OnPropertyChanged(); }
    }

    public bool IsPreDbSearching
    {
        get => _isPreDbSearching;
        set { _isPreDbSearching = value; OnPropertyChanged(); }
    }

    public PreDbItemVm? SelectedPreDbItem
    {
        get => _selectedPreDbItem;
        set { _selectedPreDbItem = value; OnPropertyChanged(); }
    }

    public bool IsPreDbTabActive
    {
        get => _isPreDbTabActive;
        set { _isPreDbTabActive = value; OnPropertyChanged(); }
    }

    public int PreDbRefreshProgress
    {
        get => _preDbRefreshProgress;
        set { _preDbRefreshProgress = value; OnPropertyChanged(); }
    }

    public string PreDbSectionFilter
    {
        get => _preDbSectionFilter;
        set
        {
            _preDbSectionFilter = value;
            OnPropertyChanged();
            ApplyPreDbFilter();
        }
    }

    public string[] PreDbSectionFilters { get; } =
        ["All", "TV", "Movies", "Music", "Games", "Apps", "Sports", "Anime", "Books", "XXX", "Other"];

    public string SearchQuery
    {
        get => _searchQuery;
        set { _searchQuery = value; OnPropertyChanged(); }
    }

    public string SearchStatus
    {
        get => _searchStatus;
        set { _searchStatus = value; OnPropertyChanged(); }
    }

    public bool IsSearching
    {
        get => _isSearching;
        set { _isSearching = value; OnPropertyChanged(); }
    }

    public string ActiveDownloadSummary
    {
        get => _activeDownloadSummary;
        set { _activeDownloadSummary = value; OnPropertyChanged(); }
    }

    public bool HasActiveDownload
    {
        get => _hasActiveDownload;
        set { _hasActiveDownload = value; OnPropertyChanged(); }
    }

    // Mounts-tab actions (per-card OPEN / FLUSH / MOUNT / UNMOUNT).
    public ICommand OpenMountCommand { get; }
    public ICommand FlushMountCommand { get; }
    public ICommand MountServerCommand { get; }
    public ICommand UnmountServerCommand { get; }
    // Search filter chips — pass "quality:1080p" / "source:WEB" / "size:1GB" / "clear".
    public ICommand ToggleSearchFilterCommand { get; }
    public ICommand AddMovieCommand { get; }
    public ICommand AddTvShowCommand { get; }
    public ICommand RemoveWishlistCommand { get; }
    public ICommand TogglePauseCommand { get; }
    public ICommand CancelDownloadCommand { get; }
    public ICommand RetryDownloadCommand { get; }
    public ICommand ClearCompletedCommand { get; }
    public ICommand ClearFailedCommand { get; }
    public ICommand ClearCancelledCommand { get; }
    public ICommand ClearAllFinishedCommand { get; }
    public ICommand RemoveSelectedDownloadsCommand { get; }
    public ICommand SearchCommand { get; }
    public ICommand CancelSearchCommand { get; }
    public ICommand DownloadSearchResultCommand { get; }
    public ICommand CopySearchResultCommand { get; }
    public ICommand RefreshMetadataCommand { get; }
    public ICommand ClearNotificationsCommand { get; }
    public ICommand DownloadNotificationCommand { get; }
    public ICommand RaceNotificationCommand { get; }
    public ICommand LoadUpcomingCommand { get; }
    public ICommand AddUpcomingToWishlistCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand ExportWishlistCommand { get; }
    public ICommand ImportWishlistCommand { get; }
    public ICommand SearchShowScheduleCommand { get; }
    public ICommand ClearShowSearchCommand { get; }
    public ICommand SearchPreDbCommand { get; }
    public ICommand CancelPreDbSearchCommand { get; }
    public ICommand LoadLatestPreDbCommand { get; }
    public ICommand CopyPreDbReleaseCommand { get; }
    public ICommand SearchPreDbOnServersCommand { get; }
    public ICommand RacePreDbCommand { get; }
    public ICommand CopyNotificationReleaseCommand { get; }
    public ICommand SearchNotificationOnServersCommand { get; }

    public DashboardViewModel(ServerManager serverManager, AppConfig config, NotificationStore notificationStore)
    {
        _serverManager = serverManager;
        _config = config;
        _notificationStore = notificationStore;
        _wishlistStore = new WishlistStore();
        _wishlistStore.Load();

        _ircViewModel = new IrcViewModel(serverManager, config);

        // Mounts-tab actions.
        OpenMountCommand = new RelayCommand<string>(serverId =>
        {
            if (string.IsNullOrEmpty(serverId)) return;
            var server = _config.Servers.FirstOrDefault(s => s.Id == serverId);
            if (server == null) return;
            var letter = server.Mount?.DriveLetter;
            // Open the mounted drive in Explorer if the user opted in;
            // otherwise open the FTP root via the system's default handler.
            try
            {
                if (server.Mount?.MountDrive == true && !string.IsNullOrEmpty(letter))
                    System.Diagnostics.Process.Start("explorer.exe", $"{letter}:\\");
            }
            catch (Exception ex) { Serilog.Log.Warning(ex, "OpenMount failed for {Id}", serverId); }
        });
        FlushMountCommand = new RelayCommand<string>(serverId =>
        {
            // FLUSH = reinitialize the connection pool (closes idle conns + ghost-kills).
            if (string.IsNullOrEmpty(serverId)) return;
            var svc = _serverManager.GetServer(serverId);
            try { _ = svc?.Pool?.Reinitialize(System.Threading.CancellationToken.None); }
            catch (Exception ex) { Serilog.Log.Warning(ex, "FlushMount failed for {Id}", serverId); }
        });
        UnmountServerCommand = new RelayCommand<string>(serverId =>
        {
            if (string.IsNullOrEmpty(serverId)) return;
            try { _serverManager.UnmountServer(serverId); RefreshOverview(); }
            catch (Exception ex) { Serilog.Log.Warning(ex, "UnmountServer failed for {Id}", serverId); }
        });
        MountServerCommand = new RelayCommand<string>(serverId =>
        {
            // Connect the server (and assign a drive letter if Mount.MountDrive
            // is true in this server's config). The button label flips between
            // MOUNT / UNMOUNT based on OverviewServerVm.IsMounted.
            if (string.IsNullOrEmpty(serverId)) return;
            _ = Task.Run(async () =>
            {
                try { await _serverManager.MountServer(serverId); }
                catch (Exception ex) { Serilog.Log.Warning(ex, "MountServer failed for {Id}", serverId); }
                Application.Current?.Dispatcher.Invoke(RefreshOverview);
            });
        });

        ToggleSearchFilterCommand = new RelayCommand<string>(arg =>
        {
            if (string.IsNullOrEmpty(arg)) return;
            if (arg == "clear")
            {
                SearchFilterQuality = "";
                SearchFilterSource = "";
                SearchFilterBigSize = false;
                return;
            }
            var idx = arg.IndexOf(':');
            if (idx < 0) return;
            var kind = arg.Substring(0, idx);
            var value = arg.Substring(idx + 1);
            switch (kind)
            {
                case "quality":
                    SearchFilterQuality = SearchFilterQuality == value ? "" : value;
                    break;
                case "source":
                    SearchFilterSource = SearchFilterSource == value ? "" : value;
                    break;
                case "size":
                    SearchFilterBigSize = !SearchFilterBigSize;
                    break;
            }
        });

        AddMovieCommand = new RelayCommand(() => AddMedia(MediaType.Movie));
        AddTvShowCommand = new RelayCommand(() => AddMedia(MediaType.TvShow));
        RemoveWishlistCommand = new RelayCommand(RemoveWishlistItem);
        TogglePauseCommand = new RelayCommand(TogglePause);
        CancelDownloadCommand = new RelayCommand(CancelDownload);
        RetryDownloadCommand = new RelayCommand(RetryDownload);
        ClearCompletedCommand = new RelayCommand(ClearCompleted);
        ClearFailedCommand = new RelayCommand(ClearFailed);
        ClearCancelledCommand = new RelayCommand(ClearCancelled);
        ClearAllFinishedCommand = new RelayCommand(ClearAllFinished);
        RemoveSelectedDownloadsCommand = new RelayCommand(RemoveSelectedDownloads);
        ClearNotificationsCommand = new RelayCommand(ClearNotifications);
        DownloadNotificationCommand = new RelayCommand(DownloadNotification);
        RaceNotificationCommand = new RelayCommand(RaceNotification);
        SearchCommand = new RelayCommand(async () => await PerformSearch());
        CancelSearchCommand = new RelayCommand(CancelSearch);
        DownloadSearchResultCommand = new RelayCommand(DownloadSearchResult);
        CopySearchResultCommand = new RelayCommand(() =>
        {
            if (SelectedSearchResult != null)
                System.Windows.Clipboard.SetText(SelectedSearchResult.ReleaseName);
        });
        RefreshMetadataCommand = new RelayCommand(async () => await RefreshAllMetadata());
        LoadUpcomingCommand = new RelayCommand(async () => await LoadUpcoming(force: true));
        AddUpcomingToWishlistCommand = new RelayCommand(async () => await AddUpcomingToWishlist());
        MoveUpCommand = new RelayCommand(MoveUpDownload);
        MoveDownCommand = new RelayCommand(MoveDownDownload);
        OpenFolderCommand = new RelayCommand(OpenFolder);
        ExportWishlistCommand = new RelayCommand(ExportWishlist);
        ImportWishlistCommand = new RelayCommand(ImportWishlist);
        SearchShowScheduleCommand = new RelayCommand(async () => await SearchShowSchedule());
        ClearShowSearchCommand = new RelayCommand(ClearShowSearch);
        SearchPreDbCommand = new RelayCommand(async () => await PerformPreDbSearch());
        CancelPreDbSearchCommand = new RelayCommand(CancelPreDbSearch);
        LoadLatestPreDbCommand = new RelayCommand(async () => await LoadLatestPreDb());
        CopyPreDbReleaseCommand = new RelayCommand(() =>
        {
            if (_selectedPreDbItem != null)
                System.Windows.Clipboard.SetText(_selectedPreDbItem.Name);
        });
        SearchPreDbOnServersCommand = new RelayCommand(async () =>
        {
            if (_selectedPreDbItem == null) return;
            SearchQuery = _selectedPreDbItem.Name;
            await PerformSearch();
        });
        RacePreDbCommand = new RelayCommand(() =>
        {
            if (_selectedPreDbItem == null) return;
            RaceByName(_selectedPreDbItem.Category, _selectedPreDbItem.Name);
        });
        CopyNotificationReleaseCommand = new RelayCommand(() =>
        {
            if (SelectedNotificationItem != null)
                System.Windows.Clipboard.SetText(SelectedNotificationItem.ReleaseName);
        });
        SearchNotificationOnServersCommand = new RelayCommand(async () =>
        {
            if (SelectedNotificationItem == null) return;
            SearchQuery = SelectedNotificationItem.ReleaseName;
            await PerformSearch();
        });

        // Status bar timer
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statusTimer.Tick += (_, _) => UpdateStatusBar();
        _statusTimer.Start();

        // PreDB auto-refresh — always runs, even when tab is not active
        _preDbRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _preDbRefreshTimer.Tick += async (_, _) =>
        {
            // Always refresh regardless of which tab is active
            if (_isPreDbSearching) return;
            if (!string.IsNullOrWhiteSpace(_preDbQuery)) return;
            try
            {
                await LoadLatestPreDb();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "PreDB auto-refresh failed");
            }
        };
        _preDbRefreshTimer.Start();
        _preDbNextRefresh = DateTime.Now.AddSeconds(15);

        // Countdown timer (1s tick) for visual progress bar
        _preDbCountdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _preDbCountdownTimer.Tick += (_, _) =>
        {
            var remaining = (_preDbNextRefresh - DateTime.Now).TotalSeconds;
            PreDbRefreshProgress = remaining > 0 ? (int)(remaining * 100 / 15) : 0;
        };
        _preDbCountdownTimer.Start();

        // IRC release link → search and download
        _ircViewModel.ReleaseLinkClicked += async releaseName =>
            await Application.Current.Dispatcher.InvokeAsync(async () =>
                await SearchAndDownloadRelease(releaseName));

        RefreshNotifications();
        RefreshWishlist();
        RefreshDownloads();

        // Subscribe to download progress events from all mounted servers
        foreach (var server in _serverManager.GetMountedServers())
            SubscribeToServer(server);

        // Also subscribe when new servers come online
        _serverStateHandler = (serverId, _, state) =>
        {
            if (state == MountState.Connected)
            {
                var server = _serverManager.GetServer(serverId);
                if (server != null)
                {
                    SubscribeToServer(server);
                    Application.Current?.Dispatcher.BeginInvoke(RefreshDownloads);
                }
            }
        };
        _serverManager.ServerStateChanged += _serverStateHandler;

        // Live notifications — add to collection when new releases arrive
        _newReleaseHandler = (serverId, serverName, category, release, remotePath) =>
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                var vm = new NotificationItemVm
                {
                    ServerId = serverId,
                    ServerName = serverName,
                    Category = category,
                    ReleaseName = release,
                    RemotePath = remotePath,
                    TimeDisplay = DateTime.Now.ToString("g")
                };
                _allNotifications.Insert(0, vm);
                if (_allNotifications.Count > 5000)
                    _allNotifications.RemoveRange(5000, _allNotifications.Count - 5000);
                UpdateNotificationFilterOptions();
                ApplyNotificationFilter();
            });
        };
        _serverManager.NewReleaseDetected += _newReleaseHandler;
    }

    private void SubscribeToServer(MountService server)
    {
        if (server.Downloads == null) return;
        // Detach handlers from any previous MountService for this serverId before re-subscribing,
        // otherwise remounting leaks two handlers per cycle.
        if (_subscribedServers.TryGetValue(server.ServerId, out var prev) && prev.Downloads != null)
        {
            prev.Downloads.DownloadProgressChanged -= OnDownloadProgress;
            prev.Downloads.DownloadStatusChanged -= OnDownloadStatusChanged;
        }
        _subscribedServers[server.ServerId] = server;
        server.Downloads.DownloadProgressChanged += OnDownloadProgress;
        server.Downloads.DownloadStatusChanged += OnDownloadStatusChanged;
    }

    private void UnsubscribeAllServers()
    {
        foreach (var server in _subscribedServers.Values)
        {
            if (server.Downloads == null) continue;
            server.Downloads.DownloadProgressChanged -= OnDownloadProgress;
            server.Downloads.DownloadStatusChanged -= OnDownloadStatusChanged;
        }
        _subscribedServers.Clear();
    }

    private void AddMedia(MediaType type)
    {
        var dialog = new MetadataSearchDialog(type, _config.Downloads.ResolveOmdbKey())
        {
            Owner = Application.Current.Windows.OfType<DashboardWindow>().FirstOrDefault()
        };

        if (dialog.ShowDialog() == true && dialog.SelectedItem != null)
        {
            _wishlistStore.Add(dialog.SelectedItem);
            RefreshWishlist();
        }
    }

    private void RemoveWishlistItem()
    {
        if (SelectedWishlistItem == null) return;
        _wishlistStore.Remove(SelectedWishlistItem.Id);
        RefreshWishlist();
    }

    private void TogglePause()
    {
        if (SelectedWishlistItem == null) return;
        var item = _wishlistStore.GetById(SelectedWishlistItem.Id);
        if (item == null) return;

        item.Status = item.Status == WishlistStatus.Paused ? WishlistStatus.Watching : WishlistStatus.Paused;
        _wishlistStore.Update(item);
        RefreshWishlist();
    }

    private void CancelDownload()
    {
        if (SelectedDownloadItem == null) return;
        var server = _serverManager.GetServer(SelectedDownloadItem.ServerId);
        server?.Downloads?.Cancel(SelectedDownloadItem.Id);
    }

    private void RetryDownload()
    {
        if (SelectedDownloadItem == null) return;
        var server = _serverManager.GetServer(SelectedDownloadItem.ServerId);
        server?.Downloads?.Retry(SelectedDownloadItem.Id);
    }

    private void ClearCompleted()
    {
        foreach (var server in _serverManager.GetMountedServers())
            server.Downloads?.RemoveCompleted();
        RefreshDownloads();
    }

    private void ClearFailed()
    {
        foreach (var server in _serverManager.GetMountedServers())
            server.Downloads?.RemoveFailed();
        RefreshDownloads();
    }

    private void ClearCancelled()
    {
        foreach (var server in _serverManager.GetMountedServers())
            server.Downloads?.RemoveCancelled();
        RefreshDownloads();
    }

    private void ClearAllFinished()
    {
        foreach (var server in _serverManager.GetMountedServers())
            server.Downloads?.RemoveFinished();
        RefreshDownloads();
    }

    /// <summary>Cancel/remove multiple selected downloads.</summary>
    public void RemoveSelectedDownloads()
    {
        var selected = SelectedDownloadItems?.ToList();
        if (selected == null || selected.Count == 0) return;

        foreach (var item in selected)
        {
            var server = _serverManager.GetServer(item.ServerId);
            if (server?.Downloads == null) continue;

            if (item.Status is "Queued" or "Downloading" or "Extracting")
                server.Downloads.Cancel(item.Id);
            else
                server.Downloads.Store.Remove(item.Id);
        }
        RefreshDownloads();
    }

    // Set from code-behind on SelectionChanged
    public IEnumerable<DownloadItemVm>? SelectedDownloadItems { get; set; }

    private void MoveUpDownload()
    {
        if (SelectedDownloadItem == null) return;
        var server = _serverManager.GetServer(SelectedDownloadItem.ServerId);
        server?.Downloads?.MoveUp(SelectedDownloadItem.Id);
    }

    private void MoveDownDownload()
    {
        if (SelectedDownloadItem == null) return;
        var server = _serverManager.GetServer(SelectedDownloadItem.ServerId);
        server?.Downloads?.MoveDown(SelectedDownloadItem.Id);
    }

    private void CancelSearch()
    {
        _searchCts?.Cancel();
    }

    private async Task PerformSearch()
    {
        if (string.IsNullOrWhiteSpace(SearchQuery)) return;

        // Cancel any in-progress search
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        var mounted = _serverManager.GetMountedServers()
            .Where(s => s.Search != null && s.CurrentState == MountState.Connected)
            .ToList();

        if (mounted.Count == 0)
        {
            SearchStatus = "No connected servers";
            return;
        }

        IsSearching = true;
        SearchStatus = "Searching...";
        SearchResults.Clear();
        // Ensure the CollectionView wrapper is initialized so filter chips
        // affect the DataGrid bound to SearchResults (DataGrid implicitly
        // uses GetDefaultView, which is what EnsureSearchView caches).
        EnsureSearchView();

        var progress = new Progress<string>(msg =>
            Application.Current?.Dispatcher.BeginInvoke(() => SearchStatus = msg));

        try
        {
            // Search all servers in parallel
            var tasks = mounted.Select(async server =>
            {
                var results = await server.Search!.Search(SearchQuery, progress, ct);
                return results.Select(r => new SearchResultVm
                {
                    ReleaseName = r.ReleaseName,
                    Category = r.Category,
                    RemotePath = r.RemotePath,
                    Size = r.Size,
                    SizeText = FormatSize(r.Size),
                    ServerId = server.ServerId,
                    ServerName = server.ServerName
                });
            });

            var allResults = await Task.WhenAll(tasks);
            var totalCount = 0;

            foreach (var serverResults in allResults)
            {
                foreach (var r in serverResults)
                {
                    SearchResults.Add(r);
                    totalCount++;
                }
            }

            SearchStatus = $"{totalCount} result(s) found across {mounted.Count} server(s)";
        }
        catch (OperationCanceledException)
        {
            SearchStatus = "Search cancelled";
        }
        catch (Exception ex)
        {
            SearchStatus = $"Search failed: {ex.Message}";
            Log.Error(ex, "Dashboard search failed");
        }
        finally
        {
            IsSearching = false;
        }
    }

    private void DownloadSearchResult()
    {
        // Multi-select: download all selected, fall back to single selection
        var selected = (SelectedSearchItems?.ToList()) ?? [];
        if (selected.Count == 0 && SelectedSearchResult != null)
            selected = [SelectedSearchResult];
        if (selected.Count == 0) return;

        var queued = 0;
        var skipped = 0;
        foreach (var result in selected)
        {
            var server = _serverManager.GetServer(result.ServerId);
            if (server?.Downloads == null) continue;

            var parsed = SceneNameParser.Parse(result.ReleaseName);
            var localBase = _config.Downloads.GetPathForCategory(result.Category);
            var safeRelease = PathSanitizer.Sanitize(result.ReleaseName);
            var safeTitle = PathSanitizer.Sanitize(parsed.Title);

            string localPath;
            if (parsed.Season != null)
            {
                var seasonFolder = $"Season {parsed.Season:D2}";
                localPath = Path.Combine(localBase, "TV", safeTitle, seasonFolder, safeRelease);
            }
            else if (parsed.Year != null)
            {
                localPath = Path.Combine(localBase, "Movies", $"{safeTitle} ({parsed.Year})", safeRelease);
            }
            else
            {
                localPath = Path.Combine(localBase, PathSanitizer.Sanitize(result.Category), safeRelease);
            }

            var item = new DownloadItem
            {
                RemotePath = result.RemotePath,
                ReleaseName = result.ReleaseName,
                LocalPath = localPath,
                Category = result.Category,
                ServerId = result.ServerId,
                ServerName = result.ServerName
            };

            if (server.Downloads.Enqueue(item))
                queued++;
            else
                skipped++;
        }

        SearchStatus = queued > 0
            ? $"Queued {queued} download(s)" + (skipped > 0 ? $", {skipped} skipped" : "")
            : $"Skipped {skipped} (duplicate or already exists)";
        RefreshDownloads();
    }

    // Set from code-behind on SearchGrid SelectionChanged
    public IEnumerable<SearchResultVm>? SelectedSearchItems { get; set; }

    private async Task SearchAndDownloadRelease(string releaseName)
    {
        var mounted = _serverManager.GetMountedServers()
            .Where(s => s.Search != null && s.CurrentState == MountState.Connected)
            .ToList();

        if (mounted.Count == 0)
        {
            _ircViewModel.AddLocalSystem($"No connected servers to search for: {releaseName}");
            return;
        }

        _ircViewModel.AddLocalSystem($"Searching for: {releaseName}...");

        try
        {
            foreach (var server in mounted)
            {
                var results = await server.Search!.Search(releaseName, null, CancellationToken.None);
                var match = results.FirstOrDefault(r =>
                    r.ReleaseName.Equals(releaseName, StringComparison.OrdinalIgnoreCase));

                if (match == null) continue;

                if (server.Downloads == null) continue;

                var parsed = SceneNameParser.Parse(match.ReleaseName);
                var localBase = _config.Downloads.GetPathForCategory(match.Category);
                var safeRelease = PathSanitizer.Sanitize(match.ReleaseName);
                var safeTitle = PathSanitizer.Sanitize(parsed.Title);

                string localPath;
                if (parsed.Season != null)
                {
                    var seasonFolder = $"Season {parsed.Season:D2}";
                    localPath = Path.Combine(localBase, "TV", safeTitle, seasonFolder, safeRelease);
                }
                else if (parsed.Year != null)
                {
                    localPath = Path.Combine(localBase, "Movies", $"{safeTitle} ({parsed.Year})", safeRelease);
                }
                else
                {
                    localPath = Path.Combine(localBase, PathSanitizer.Sanitize(match.Category), safeRelease);
                }

                var item = new DownloadItem
                {
                    RemotePath = match.RemotePath,
                    ReleaseName = match.ReleaseName,
                    LocalPath = localPath,
                    Category = match.Category,
                    ServerId = server.ServerId,
                    ServerName = server.ServerName
                };

                if (!server.Downloads.Enqueue(item))
                {
                    _ircViewModel.AddLocalSystem($"Skipped: {releaseName} (duplicate or already exists)");
                    return;
                }

                _ircViewModel.AddLocalSystem($"Queued: {releaseName} from {server.ServerName}");
                RefreshDownloads();
                return;
            }

            _ircViewModel.AddLocalSystem($"Not found: {releaseName}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "IRC release search failed for {Release}", releaseName);
            _ircViewModel.AddLocalSystem($"Search failed: {ex.Message}");
        }
    }

    private void OnDownloadProgress(DownloadItem item, DownloadProgress progress)
    {
        _serverSpeeds[item.ServerId] = progress.BytesPerSecond;

        var pct = progress.TotalBytes > 0
            ? (double)progress.DownloadedBytes / progress.TotalBytes * 100 : 0;
        _activeDownloads[item.Id] = (item.ReleaseName, progress.BytesPerSecond, pct);

        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            HasActiveDownload = true;
            UpdateActiveDownloadSummary();

            // Update the grid row's Progress column
            var vm = DownloadItems.FirstOrDefault(d => d.Id == item.Id);
            if (vm != null)
            {
                var pctInt = (int)pct;
                var fileName = progress.CurrentFileName ?? "";
                vm.ProgressText = string.IsNullOrEmpty(fileName)
                    ? $"{pctInt}%"
                    : $"{pctInt}% — {fileName}";
            }
        });
    }

    private void OnDownloadStatusChanged(DownloadItem item)
    {
        if (item.Status != DownloadStatus.Downloading && item.Status != DownloadStatus.Extracting)
        {
            _activeDownloads.Remove(item.Id);
            _serverSpeeds.Remove(item.ServerId);
        }

        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            RefreshDownloads();
            HasActiveDownload = _activeDownloads.Count > 0;
            UpdateActiveDownloadSummary();
        });
    }

    private void UpdateActiveDownloadSummary()
    {
        if (_activeDownloads.Count == 0)
        {
            ActiveDownloadSummary = "";
            return;
        }

        if (_activeDownloads.Count == 1)
        {
            var dl = _activeDownloads.Values.First();
            ActiveDownloadSummary = $"{dl.name} — {FormatSpeed(dl.speed)} ({dl.pct:F0}%)";
            return;
        }

        // Multiple concurrent downloads: show each on summary line
        var totalSpeed = _activeDownloads.Values.Sum(d => d.speed);
        var lines = _activeDownloads.Values
            .Select(d => $"{d.name}  {d.pct:F0}%  {FormatSpeed(d.speed)}")
            .ToList();
        lines.Add($"Total: {FormatSpeed(totalSpeed)}");
        ActiveDownloadSummary = string.Join("  |  ", lines);
    }

    public async Task LoadUpcoming(bool force = false)
    {
        var now = DateTime.UtcNow;
        var loadTv = force || (now - _tvCacheTime).TotalMinutes >= 30;
        var loadMovies = force || (now - _movieCacheTime).TotalHours >= 1;

        if (!loadTv && !loadMovies) return;

        UpcomingStatus = "Loading...";

        try
        {
            if (loadTv)
            {
                using var tvMaze = new TvMazeClient();
                var today = DateOnly.FromDateTime(DateTime.Today);
                var episodes = new List<UpcomingTvEpisodeVm>();
                var seenShowEps = new HashSet<string>();

                for (int d = 0; d < 7; d++)
                {
                    var date = today.AddDays(d);

                    // Fetch broadcast + streaming schedules in parallel
                    var broadcastTask = tvMaze.GetSchedule(date);
                    var webTask = tvMaze.GetWebSchedule(date);
                    await Task.WhenAll(broadcastTask, webTask);

                    var allEpisodes = (await broadcastTask)
                        .Concat(await webTask)
                        .Where(e => e.Show != null);

                    foreach (var ep in allEpisodes)
                    {
                        // Deduplicate by show+season+episode
                        var key = $"{ep.Show!.Id}:S{ep.Season}E{ep.Number ?? 0}";
                        if (!seenShowEps.Add(key)) continue;

                        var epNum = ep.Number.HasValue ? $"E{ep.Number.Value:D2}" : "";
                        episodes.Add(new UpcomingTvEpisodeVm
                        {
                            ShowId = ep.Show.Id,
                            ShowName = ep.Show.Name,
                            ShowType = ep.Show.Type ?? "",
                            EpisodeInfo = $"S{ep.Season:D2}{epNum} — {ep.Name}",
                            TimeDisplay = ep.Airtime ?? "",
                            NetworkDisplay = ep.Show.NetworkName,
                            DateDisplay = date.ToString("ddd M/d"),
                            AirDate = date,
                            PosterUrl = ep.Show.Image?.Medium,
                            Plot = StripHtml(ep.Show.Summary),
                            Rating = ep.Show.Rating?.Average?.ToString("F1"),
                            Genres = ep.Show.Genres != null ? string.Join(", ", ep.Show.Genres) : "",
                            TvMazeId = ep.Show.Id
                        });
                    }

                    if (d < 6) await Task.Delay(600); // Rate limit: 20 req/10s
                }

                _cachedTvSchedule = episodes;
                IsShowSearchActive = false;
                ShowSearchQuery = "";
                ApplyTvTypeFilter();

                _tvCacheTime = now;
            }

            if (loadMovies && HasTmdbKey)
            {
                using var tmdb = new TmdbClient(_config.Downloads.ResolveTmdbKey());
                var from = DateOnly.FromDateTime(DateTime.Today);
                var to = DateOnly.FromDateTime(DateTime.Today.AddDays(30));
                var movies = await tmdb.GetUpcomingReleases(from, to);

                UpcomingMovies.Clear();
                foreach (var m in movies)
                {
                    UpcomingMovies.Add(new UpcomingMovieVm
                    {
                        TmdbId = m.Id,
                        Title = m.Title,
                        Year = m.YearParsed?.ToString() ?? "",
                        ReleaseDateDisplay = m.ReleaseDate ?? "",
                        ReleaseType = "", // discover doesn't distinguish 4 vs 5
                        PosterUrl = m.PosterUrl,
                        Plot = m.Overview,
                        Rating = m.VoteAverage > 0 ? m.VoteAverage.ToString("F1") : "",
                        Genres = m.GenreText
                    });
                }

                _movieCacheTime = now;
            }

            var tvCount = UpcomingTvEpisodes.Count;
            var movieCount = UpcomingMovies.Count;
            UpcomingStatus = $"{tvCount} episode(s), {movieCount} movie(s)";
        }
        catch (Exception ex)
        {
            UpcomingStatus = $"Error: {ex.Message}";
            Log.Error(ex, "Failed to load upcoming data");
        }
    }

    private async Task SearchShowSchedule()
    {
        var query = _showSearchQuery?.Trim();
        if (string.IsNullOrEmpty(query)) return;

        UpcomingStatus = "Searching...";

        try
        {
            using var tvMaze = new TvMazeClient();
            var shows = await tvMaze.Search(query);
            var results = new List<UpcomingTvEpisodeVm>();

            foreach (var show in shows.Take(10))
            {
                var result = await tvMaze.GetShowWithNextEpisode(show.Id);
                if (result == null) continue;

                var ep = result.NextEpisode;
                results.Add(new UpcomingTvEpisodeVm
                {
                    ShowId = result.Show.Id,
                    ShowName = result.Show.Name,
                    EpisodeInfo = ep != null
                        ? $"S{ep.Season:D2}{(ep.Number.HasValue ? $"E{ep.Number.Value:D2}" : "")} — {ep.Name}"
                        : "No upcoming episodes",
                    TimeDisplay = ep?.Airtime ?? "",
                    NetworkDisplay = result.Show.NetworkName,
                    DateDisplay = ep?.Airdate is { } ad && DateOnly.TryParse(ad, out var d)
                        ? d.ToString("ddd M/d") : "",
                    AirDate = ep?.Airdate is { } ad2 && DateOnly.TryParse(ad2, out var d2)
                        ? d2 : default,
                    PosterUrl = result.Show.Image?.Medium,
                    Plot = StripHtml(result.Show.Summary),
                    Rating = result.Show.Rating?.Average?.ToString("F1"),
                    Genres = result.Show.Genres != null ? string.Join(", ", result.Show.Genres) : "",
                    TvMazeId = result.Show.Id
                });

                await Task.Delay(300); // Rate limit
            }

            IsShowSearchActive = true;
            UpcomingTvEpisodes.Clear();
            foreach (var r in results)
                UpcomingTvEpisodes.Add(r);

            UpcomingStatus = $"{results.Count} show(s) found";
        }
        catch (Exception ex)
        {
            UpcomingStatus = $"Search error: {ex.Message}";
            Log.Error(ex, "TV show search failed for: {Query}", query);
        }
    }

    private void ClearShowSearch()
    {
        ShowSearchQuery = "";
        IsShowSearchActive = false;
        ApplyTvTypeFilter();
    }

    private void ApplyTvTypeFilter()
    {
        if (_cachedTvSchedule == null) return;

        var filtered = _tvTypeFilter == "All"
            ? _cachedTvSchedule
            : _cachedTvSchedule.Where(e => e.ShowType.Equals(_tvTypeFilter, StringComparison.OrdinalIgnoreCase)).ToList();

        UpcomingTvEpisodes.Clear();
        foreach (var ep in filtered)
            UpcomingTvEpisodes.Add(ep);

        var tvCount = UpcomingTvEpisodes.Count;
        var movieCount = UpcomingMovies.Count;
        UpcomingStatus = $"{tvCount} episode(s), {movieCount} movie(s)";
    }

    private async Task AddUpcomingToWishlist()
    {
        if (_selectedTvEpisode != null)
        {
            var ep = _selectedTvEpisode;
            // Check duplicate
            if (_wishlistStore.Items.Any(w => w.TvMazeId == ep.TvMazeId))
            {
                UpcomingStatus = $"{ep.ShowName} is already in wishlist";
                return;
            }

            var quality = ParseQuality(UpcomingQualityText);
            _wishlistStore.Add(new WishlistItem
            {
                Type = MediaType.TvShow,
                Title = ep.ShowName,
                TvMazeId = ep.TvMazeId,
                Quality = quality,
                PosterUrl = ep.PosterUrl,
                Plot = ep.Plot,
                Rating = ep.Rating,
                Genres = ep.Genres
            });
            RefreshWishlist();
            UpcomingStatus = $"Added {ep.ShowName} to wishlist";
        }
        else if (_selectedUpcomingMovie != null)
        {
            var movie = _selectedUpcomingMovie;

            // Fetch detail for ImdbId if we don't have it
            if (string.IsNullOrEmpty(movie.ImdbId) && HasTmdbKey)
            {
                using var tmdb = new TmdbClient(_config.Downloads.ResolveTmdbKey());
                var detail = await tmdb.GetMovieDetail(movie.TmdbId);
                if (detail != null)
                {
                    movie.ImdbId = detail.ImdbId;
                    if (string.IsNullOrEmpty(movie.Plot)) movie.Plot = detail.Overview;
                    if (string.IsNullOrEmpty(movie.Genres)) movie.Genres = detail.GenreText;
                    if (string.IsNullOrEmpty(movie.Rating) && detail.VoteAverage > 0)
                        movie.Rating = detail.VoteAverage.ToString("F1");
                }
            }

            if (!string.IsNullOrEmpty(movie.ImdbId) &&
                _wishlistStore.Items.Any(w => w.ImdbId == movie.ImdbId))
            {
                UpcomingStatus = $"{movie.Title} is already in wishlist";
                return;
            }

            var quality = ParseQuality(UpcomingQualityText);
            _wishlistStore.Add(new WishlistItem
            {
                Type = MediaType.Movie,
                Title = movie.Title,
                Year = int.TryParse(movie.Year, out var y) ? y : null,
                ImdbId = movie.ImdbId,
                Quality = quality,
                PosterUrl = movie.PosterUrl,
                Plot = movie.Plot,
                Rating = movie.Rating,
                Genres = movie.Genres
            });
            RefreshWishlist();
            UpcomingStatus = $"Added {movie.Title} to wishlist";
        }
    }

    private static QualityProfile ParseQuality(string text) => text switch
    {
        "SD" => QualityProfile.SD,
        "720p" => QualityProfile.Q720p,
        "1080p" => QualityProfile.Q1080p,
        "2160p" => QualityProfile.Q2160p,
        _ => QualityProfile.Any
    };

    private async Task RefreshAllMetadata()
    {
        using var tvMaze = new TvMazeClient();
        using var omdb = new OmdbClient(_config.Downloads.ResolveOmdbKey());
        var updated = 0;

        foreach (var item in _wishlistStore.Items.ToList())
        {
            // Skip items that already have metadata
            if (!string.IsNullOrEmpty(item.PosterUrl) && !string.IsNullOrEmpty(item.Plot))
                continue;

            try
            {
                if (item.Type == MediaType.TvShow && item.TvMazeId.HasValue)
                {
                    var show = await tvMaze.GetShow(item.TvMazeId.Value);
                    if (show == null) continue;

                    item.PosterUrl ??= show.Image?.Medium;
                    item.Plot ??= StripHtml(show.Summary);
                    item.Rating ??= show.Rating?.Average?.ToString("F1");
                    item.Genres ??= show.Genres != null ? string.Join(", ", show.Genres) : null;
                    _wishlistStore.Update(item);
                    updated++;
                }
                else if (item.Type == MediaType.Movie && !string.IsNullOrEmpty(item.ImdbId))
                {
                    var movie = await omdb.GetById(item.ImdbId);
                    if (movie == null) continue;

                    item.PosterUrl ??= movie.Poster is "N/A" or null ? null : movie.Poster;
                    item.Plot ??= movie.Plot is "N/A" or null ? null : movie.Plot;
                    item.Rating ??= movie.imdbRating is "N/A" or null ? null : movie.imdbRating;
                    item.Genres ??= movie.Genre is "N/A" or null ? null : movie.Genre;
                    _wishlistStore.Update(item);
                    updated++;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to refresh metadata for {Title}", item.Title);
            }
        }

        RefreshWishlist();
        Log.Information("Refreshed metadata for {Count} item(s)", updated);
    }

    private static string? StripHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        var text = Regex.Replace(html, "<[^>]+>", "").Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private void RefreshNotifications()
    {
        _allNotifications.Clear();
        foreach (var item in _notificationStore.Items)
        {
            _allNotifications.Add(new NotificationItemVm
            {
                ServerId = item.ServerId,
                ServerName = item.ServerName,
                Category = item.Category,
                ReleaseName = item.ReleaseName,
                RemotePath = item.RemotePath,
                TimeDisplay = item.Timestamp.ToLocalTime().ToString("g")
            });
        }
        UpdateNotificationFilterOptions();
        ApplyNotificationFilter();
    }

    private void UpdateNotificationFilterOptions()
    {
        var categories = _allNotifications.Select(n => n.Category).Where(c => !string.IsNullOrEmpty(c)).Distinct().OrderBy(c => c).ToList();
        var servers = _allNotifications.Select(n => n.ServerName).Where(s => !string.IsNullOrEmpty(s)).Distinct().OrderBy(s => s).ToList();

        NotificationCategories.Clear();
        NotificationCategories.Add("All");
        foreach (var c in categories) NotificationCategories.Add(c);

        NotificationServers.Clear();
        NotificationServers.Add("All");
        foreach (var s in servers) NotificationServers.Add(s);
    }

    private void ApplyNotificationFilter()
    {
        NotificationItems.Clear();
        foreach (var item in _allNotifications)
        {
            if (!string.IsNullOrEmpty(_notificationFilterText) &&
                !item.ReleaseName.Contains(_notificationFilterText, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!string.IsNullOrEmpty(_notificationFilterCategory) && _notificationFilterCategory != "All"
                && item.Category != _notificationFilterCategory)
                continue;

            if (!string.IsNullOrEmpty(_notificationFilterServer) && _notificationFilterServer != "All"
                && item.ServerName != _notificationFilterServer)
                continue;

            NotificationItems.Add(item);
        }
    }

    private void NotifyNotificationDetailChanged()
    {
        OnPropertyChanged(nameof(HasNotificationSelection));
        OnPropertyChanged(nameof(HasNotificationPoster));
        OnPropertyChanged(nameof(NotificationPosterUrl));
        OnPropertyChanged(nameof(NotificationPlot));
        OnPropertyChanged(nameof(NotificationRating));
        OnPropertyChanged(nameof(NotificationGenres));
    }

    private async Task LoadNotificationMetadata(NotificationItemVm item)
    {
        try
        {
            var parsed = SceneNameParser.Parse(item.ReleaseName);

            if (parsed.Season != null)
            {
                using var tvMaze = new TvMazeClient();
                var results = await tvMaze.Search(parsed.Title);
                var show = results.FirstOrDefault();
                if (show != null)
                {
                    item.PosterUrl = show.Image?.Medium;
                    item.Plot = StripHtml(show.Summary);
                    item.Rating = show.Rating?.Average?.ToString("F1");
                    item.Genres = show.Genres != null ? string.Join(", ", show.Genres) : null;
                }
            }
            else
            {
                var omdbKey = _config.Downloads.ResolveOmdbKey();
                if (!string.IsNullOrEmpty(omdbKey))
                {
                    using var omdb = new OmdbClient(omdbKey);
                    var results = await omdb.Search(parsed.Title);
                    var movie = results.FirstOrDefault();
                    if (movie != null)
                    {
                        if (!string.IsNullOrEmpty(movie.ImdbID))
                        {
                            var detail = await omdb.GetById(movie.ImdbID);
                            if (detail != null)
                            {
                                item.PosterUrl = detail.Poster is "N/A" ? null : detail.Poster;
                                item.Plot = detail.Plot is "N/A" ? null : detail.Plot;
                                item.Rating = detail.imdbRating is "N/A" ? null : detail.imdbRating;
                                item.Genres = detail.Genre is "N/A" ? null : detail.Genre;
                            }
                        }
                        else
                        {
                            item.PosterUrl = movie.Poster is "N/A" ? null : movie.Poster;
                        }
                    }
                }
            }

            item.MetadataLoaded = true;

            if (_selectedNotificationItem == item)
                Application.Current?.Dispatcher.BeginInvoke(NotifyNotificationDetailChanged);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to load metadata for notification {Release}", item.ReleaseName);
            item.MetadataLoaded = true;
        }
    }

    private void DownloadNotification()
    {
        if (SelectedNotificationItem == null) return;

        var n = SelectedNotificationItem;
        if (string.IsNullOrEmpty(n.RemotePath)) return;

        var server = _serverManager.GetServer(n.ServerId);
        if (server?.Downloads == null) return;

        var parsed = SceneNameParser.Parse(n.ReleaseName);
        var localBase = _config.Downloads.GetPathForCategory(n.Category);
        var safeRelease = PathSanitizer.Sanitize(n.ReleaseName);
        var safeTitle = PathSanitizer.Sanitize(parsed.Title);

        string localPath;
        if (parsed.Season != null)
        {
            var seasonFolder = $"Season {parsed.Season:D2}";
            localPath = Path.Combine(localBase, "TV", safeTitle, seasonFolder, safeRelease);
        }
        else if (parsed.Year != null)
        {
            localPath = Path.Combine(localBase, "Movies", $"{safeTitle} ({parsed.Year})", safeRelease);
        }
        else
        {
            localPath = Path.Combine(localBase, PathSanitizer.Sanitize(n.Category), safeRelease);
        }

        var item = new DownloadItem
        {
            RemotePath = n.RemotePath,
            ReleaseName = n.ReleaseName,
            LocalPath = localPath,
            Category = n.Category,
            ServerId = n.ServerId,
            ServerName = n.ServerName
        };

        if (!server.Downloads.Enqueue(item))
        {
            SearchStatus = $"Skipped: {n.ReleaseName} (duplicate or already exists)";
            return;
        }
        RefreshDownloads();
    }

    private void RaceNotification()
    {
        if (SelectedNotificationItem == null) return;
        var n = SelectedNotificationItem;
        // Pass the known source server and remote path so spread doesn't have to probe
        RaceByName(n.Category, n.ReleaseName, n.ServerId, n.RemotePath);
    }

    /// <summary>
    /// Normalize a section name for fuzzy matching: lowercase, strip dashes/underscores/spaces.
    /// "TV-WEB-HD-X264" → "tvwebhdx264", "TV_HD" → "tvhd", "tv-hd" → "tvhd"
    /// </summary>
    private static string NormalizeSection(string s) =>
        s.ToLowerInvariant().Replace("-", "").Replace("_", "").Replace(" ", "");

    /// <summary>Find the best matching configured section key for a given hint (category name).</summary>
    private string? FindMatchingSection(string hint, IEnumerable<string> configuredSections)
    {
        var normHint = NormalizeSection(hint);

        // Exact match first
        var exact = configuredSections.FirstOrDefault(k => k.Equals(hint, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;

        // Normalized match (tv-hd == TV_HD)
        var normalized = configuredSections.FirstOrDefault(k => NormalizeSection(k) == normHint);
        if (normalized != null) return normalized;

        // Substring match (TV-WEB-HD-X264 contains tvhd)
        var substring = configuredSections.FirstOrDefault(k =>
            normHint.Contains(NormalizeSection(k)) || NormalizeSection(k).Contains(normHint));
        return substring;
    }

    /// <summary>Start a race for a release, optionally with a known source server and path.</summary>
    private void RaceByName(string sectionHint, string releaseName,
        string? knownSourceServerId = null, string? knownSourcePath = null)
    {
        var spread = _serverManager.Spread;
        if (spread == null)
        {
            SearchStatus = "Spread engine not available";
            return;
        }

        // Pass ALL connected servers with any sections — the job handles discovery
        var connectedIds = spread.GetConnectedServerIds();
        var serverIds = _config.Servers
            .Where(s => s.Enabled && connectedIds.Contains(s.Id) && s.SpreadSite.Sections.Count > 0)
            .Select(s => s.Id)
            .ToList();

        if (serverIds.Count < 2)
        {
            SearchStatus = $"Need 2+ connected servers with sections configured";
            return;
        }

        try
        {
            spread.StartRace(sectionHint, releaseName, serverIds, Spread.SpreadMode.Race,
                knownSourceServerId, knownSourcePath);
            SearchStatus = $"Race queued: {releaseName} [{sectionHint}]";
        }
        catch (Exception ex)
        {
            SearchStatus = $"Race failed: {ex.Message}";
        }
    }

    private void ClearNotifications()
    {
        _notificationStore.Clear();
        _allNotifications.Clear();
        NotificationItems.Clear();
        NotificationCategories.Clear();
        NotificationCategories.Add("All");
        NotificationServers.Clear();
        NotificationServers.Add("All");
    }

    private void RefreshWishlist()
    {
        WishlistItems.Clear();
        foreach (var item in _wishlistStore.Items)
        {
            WishlistItems.Add(new WishlistItemVm
            {
                Id = item.Id,
                Title = item.Title,
                Type = item.Type.ToString(),
                Year = item.Year?.ToString() ?? "",
                Quality = item.Quality.ToString(),
                Status = item.Status.ToString(),
                GrabbedCount = item.GrabbedReleases.Count.ToString(),
                PosterUrl = item.PosterUrl,
                Plot = item.Plot,
                Rating = item.Rating,
                Genres = item.Genres
            });
        }
    }

    private void RefreshDownloads()
    {
        DownloadItems.Clear();
        foreach (var server in _serverManager.GetMountedServers())
        {
            var store = server.Downloads?.Store;
            if (store == null) continue;

            foreach (var item in store.Items)
            {
                DownloadItems.Add(new DownloadItemVm
                {
                    Id = item.Id,
                    ReleaseName = item.ReleaseName,
                    Category = item.Category,
                    Status = item.Status.ToString(),
                    ProgressText = item.Status switch
                    {
                        DownloadStatus.Downloading when item.TotalBytes > 0
                            => $"{item.DownloadedBytes * 100 / item.TotalBytes}%",
                        DownloadStatus.Extracting => "Extracting...",
                        DownloadStatus.Completed => "Done",
                        _ => ""
                    },
                    ProgressPercent = item.Status switch
                    {
                        DownloadStatus.Downloading when item.TotalBytes > 0
                            => (double)item.DownloadedBytes / item.TotalBytes * 100.0,
                        DownloadStatus.Completed => 100.0,
                        _ => 0.0
                    },
                    LocalPath = item.LocalPath,
                    ServerId = server.ServerId,
                    ServerName = server.ServerName
                });
            }
        }
        OnPropertyChanged(nameof(ActiveDownloadCount));
        OnPropertyChanged(nameof(QueuedDownloadCount));
        OnPropertyChanged(nameof(FailedDownloadCount));
        OnPropertyChanged(nameof(CompletedTodayCount));
    }

    private void OpenFolder()
    {
        if (SelectedDownloadItem == null) return;
        var server = _serverManager.GetServer(SelectedDownloadItem.ServerId);
        var item = server?.Downloads?.Store.GetById(SelectedDownloadItem.Id);
        if (item == null || string.IsNullOrEmpty(item.LocalPath)) return;

        var path = Directory.Exists(item.LocalPath) ? item.LocalPath : Path.GetDirectoryName(item.LocalPath);
        if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
            Process.Start("explorer.exe", path);
    }

    private void ExportWishlist()
    {
        var dlg = new SaveFileDialog
        {
            Filter = "JSON files (*.json)|*.json",
            FileName = "wishlist-export.json"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var json = JsonSerializer.Serialize(_wishlistStore.Items, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            File.WriteAllText(dlg.FileName, json);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to export wishlist");
        }
    }

    private void ImportWishlist()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "JSON files (*.json)|*.json"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var json = File.ReadAllText(dlg.FileName);
            var items = JsonSerializer.Deserialize<List<WishlistItem>>(json, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            if (items == null) return;

            var added = 0;
            foreach (var item in items)
            {
                // Deduplicate by ImdbId, TvMazeId, or Title
                var isDupe = _wishlistStore.Items.Any(w =>
                    (!string.IsNullOrEmpty(w.ImdbId) && w.ImdbId == item.ImdbId) ||
                    (w.TvMazeId.HasValue && w.TvMazeId == item.TvMazeId) ||
                    (w.Title == item.Title && w.Type == item.Type));
                if (isDupe) continue;

                item.Id = Guid.NewGuid().ToString("N");
                _wishlistStore.Add(item);
                added++;
            }

            RefreshWishlist();
            Log.Information("Imported {Count} wishlist item(s)", added);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to import wishlist");
        }
    }

    private void UpdateStatusBar()
    {
        // Aggregate speed across all servers
        var totalSpeed = _serverSpeeds.Values.Sum();
        _speedHistory.Add(totalSpeed);
        if (_speedHistory.Count > 60) _speedHistory.RemoveAt(0);

        StatusBarSpeed = totalSpeed > 0 ? FormatSpeed(totalSpeed) : "Idle";

        // Queue count
        var queued = 0;
        foreach (var server in _serverManager.GetMountedServers())
        {
            var store = server.Downloads?.Store;
            if (store == null) continue;
            queued += store.Items.Count(i => i.Status == DownloadStatus.Queued || i.Status == DownloadStatus.Downloading);
        }
        StatusBarQueueCount = $"{queued} active";

        // Server disk usage — poll every 30s (uses FTP connections), show cached value otherwise
        var now = DateTime.UtcNow;
        if ((now - _lastDiskPoll).TotalSeconds >= 30)
        {
            _lastDiskPoll = now;
            _ = PollServerDiskUsage();
        }

        // FTP connection status per server
        var connParts = new List<string>();
        foreach (var server in _serverManager.GetMountedServers())
        {
            if (server.Pool != null && server.Pool.IsConnected)
            {
                var active = server.Pool.ActiveCount;
                var total = server.Pool.TotalCreated;
                connParts.Add($"{server.ServerName}: {active}/{total}");
            }
            else
            {
                var stateLabel = server.CurrentState switch
                {
                    MountState.Connecting => "connecting",
                    MountState.Reconnecting => "reconnecting",
                    MountState.Error => "error",
                    _ => "disconnected"
                };
                connParts.Add($"{server.ServerName}: {stateLabel}");
            }
        }
        StatusBarConnections = connParts.Count > 0 ? string.Join("  |  ", connParts) : "";

        // Per-site credits + ratio (scraped from SITE USER by MountService)
        var siteParts = new List<string>();
        foreach (var server in _serverManager.GetMountedServers())
        {
            var stats = server.Stats;
            if (stats == null || (stats.Credits == null && stats.Ratio == null)) continue;
            string label;
            if (stats.Ratio == "UL")
                label = stats.Credits != null
                    ? $"{server.ServerName} {stats.Credits} (UL)"
                    : $"{server.ServerName} UL";
            else if (stats.Credits != null && stats.Ratio != null)
                label = $"{server.ServerName} {stats.Credits} ({stats.Ratio})";
            else if (stats.Credits != null)
                label = $"{server.ServerName} {stats.Credits}";
            else
                label = $"{server.ServerName} {stats.Ratio}";
            siteParts.Add(label);
        }
        StatusBarSites = siteParts.Count > 0 ? string.Join("  |  ", siteParts) : "";

        // Update bandwidth graph points
        UpdateSpeedGraph();
    }

    private async Task PollServerDiskUsage()
    {
        try
        {
            long totalUsed = 0;
            long totalSize = 0;
            var parts = new List<string>();
            bool anyCacheUpdate = false;

            foreach (var server in _serverManager.GetMountedServers())
            {
                if (server.Ftp == null || server.CurrentState != MountState.Connected) continue;
                try
                {
                    var disk = await server.Ftp.GetDiskFree(CancellationToken.None);
                    if (disk == null) continue;
                    // Cache per-server tuple so RefreshOverview() can compute real
                    // disk-space % for the Overview/Mounts disc widgets.
                    _serverDiskCache[server.ServerId] = (disk.Value.totalBytes, disk.Value.freeBytes);
                    anyCacheUpdate = true;
                    var used = disk.Value.totalBytes - disk.Value.freeBytes;
                    totalUsed += used;
                    totalSize += disk.Value.totalBytes;
                    parts.Add($"{server.ServerName}: {FormatSize(used)}/{FormatSize(disk.Value.totalBytes)}");
                }
                catch { }
            }

            if (totalSize > 0)
                StatusBarDiskSpace = $"{FormatSize(totalUsed)} / {FormatSize(totalSize)} used";
            else
                StatusBarDiskSpace = "";

            // Refresh the Overview tab so the disc widgets pick up the new
            // disk-space values. RefreshOverview() rebuilds MountedServerStatus
            // from scratch and reads _serverDiskCache, so a single delegate
            // call after the poll is enough. Marshalled to the UI dispatcher
            // because RefreshOverview mutates an ObservableCollection.
            if (anyCacheUpdate)
            {
                try
                {
                    Application.Current?.Dispatcher.Invoke(RefreshOverview);
                }
                catch { /* dispatcher shut down or VM disposed — ignore */ }
            }
        }
        catch { }
    }

    private void UpdateSpeedGraph()
    {
        if (_speedHistory.Count < 2) return;

        var maxSpeed = _speedHistory.Max();
        if (maxSpeed <= 0) maxSpeed = 1;

        const double width = 200;
        const double height = 40;
        var points = new PointCollection();

        for (int i = 0; i < _speedHistory.Count; i++)
        {
            var x = (double)i / (_speedHistory.Count - 1) * width;
            var y = height - (_speedHistory[i] / maxSpeed * height);
            points.Add(new Point(x, y));
        }

        SpeedGraphPoints = points;
    }

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "\u2014";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int i = 0;
        double size = bytes;
        while (size >= 1024 && i < units.Length - 1) { size /= 1024; i++; }
        return $"{size:F1} {units[i]}";
    }

    private static string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond <= 0) return "";
        string[] units = ["B/s", "KB/s", "MB/s", "GB/s"];
        int i = 0;
        double speed = bytesPerSecond;
        while (speed >= 1024 && i < units.Length - 1) { speed /= 1024; i++; }
        return $"{speed:F1} {units[i]}";
    }

    // --- PreDB ---

    public async Task LoadLatestPreDb()
    {
        if (IsPreDbSearching) return;
        IsPreDbSearching = true;

        try
        {
            _preDbCts?.Cancel();
            _preDbCts = new CancellationTokenSource();
            var results = await _preDbClient.GetLatestAsync(ct: _preDbCts.Token);
            Log.Debug("PreDB: got {Count} items", results.Length);
            MergePreDbItems(results);
            PreDbStatus = $"{PreDbItems.Count} releases (updated {DateTime.Now:HH:mm:ss})";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            PreDbStatus = $"Error: {ex.Message}";
        }
        finally
        {
            IsPreDbSearching = false;
            _preDbNextRefresh = DateTime.Now.AddSeconds(15);
            PreDbRefreshProgress = 100;
        }
    }

    private async Task PerformPreDbSearch()
    {
        if (string.IsNullOrWhiteSpace(_preDbQuery)) return;
        IsPreDbSearching = true;
        PreDbStatus = $"Searching \"{_preDbQuery}\"...";

        try
        {
            _preDbCts?.Cancel();
            _preDbCts = new CancellationTokenSource();
            var results = await _preDbClient.SearchAsync(_preDbQuery, ct: _preDbCts.Token);
            PopulatePreDbItems(results);
            PreDbStatus = $"{results.Length} results for \"{_preDbQuery}\"";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            PreDbStatus = $"Error: {ex.Message}";
        }
        finally
        {
            IsPreDbSearching = false;
        }
    }

    private void CancelPreDbSearch()
    {
        _preDbCts?.Cancel();
        IsPreDbSearching = false;
        PreDbStatus = "Cancelled";
    }

    /// <summary>Merge new releases into the master list, then apply filter to display.</summary>
    private void MergePreDbItems(PreDbRelease[] releases)
    {
        var existingIds = new HashSet<int>(_allPreDbItems.Select(x => x.Id));
        var newItems = new List<PreDbItemVm>();

        foreach (var r in releases)
        {
            if (existingIds.Contains(r.Id)) continue;
            newItems.Add(ToPreDbVm(r));
            if (r.Id > _preDbHighestId)
                _preDbHighestId = r.Id;
        }

        if (newItems.Count > 0)
        {
            _allPreDbItems.InsertRange(0, newItems);

            // Trim master list to 500 max
            while (_allPreDbItems.Count > 500)
                _allPreDbItems.RemoveAt(_allPreDbItems.Count - 1);
        }

        // Update relative times for existing items
        foreach (var item in _allPreDbItems)
            item.Time = PreDbRelease.FormatTimeAgo(item.PreAt);

        ApplyPreDbFilter();
    }

    private void PopulatePreDbItems(PreDbRelease[] releases)
    {
        _allPreDbItems.Clear();
        foreach (var r in releases)
            _allPreDbItems.Add(ToPreDbVm(r));
        ApplyPreDbFilter();
    }

    private void ApplyPreDbFilter()
    {
        var filtered = _preDbSectionFilter == "All"
            ? _allPreDbItems
            : _allPreDbItems.Where(x => x.BroadCategory == _preDbSectionFilter).ToList();

        PreDbItems.Clear();
        foreach (var item in filtered.Take(300))
            PreDbItems.Add(item);
    }

    private static PreDbItemVm ToPreDbVm(PreDbRelease r) => new()
    {
        Id = r.Id,
        PreAt = r.PreAt,
        Name = r.Release,
        Team = r.Group,
        Category = r.Section,
        BroadCategory = r.BroadCategory,
        Size = r.SizeFormatted,
        Files = r.Files > 0 ? r.Files.ToString() : "",
        Time = PreDbRelease.FormatTimeAgo(r.PreAt),
        IsNuked = r.IsNuked,
        NukeReason = r.Reason,
        Genre = r.Genre
    };

    public void Dispose()
    {
        _statusTimer?.Stop();
        _overviewTimer?.Stop();
        _overviewTimer = null;
        _preDbRefreshTimer?.Stop();
        _preDbCountdownTimer?.Stop();
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;
        _preDbCts?.Cancel();
        _preDbCts?.Dispose();
        _preDbCts = null;
        _preDbClient.Dispose();
        _serverManager.ServerStateChanged -= _serverStateHandler;
        _serverManager.NewReleaseDetected -= _newReleaseHandler;
        UnsubscribeAllServers();
        _ircViewModel.Dispose();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class PreDbItemVm
{
    public int Id { get; set; }
    public long PreAt { get; set; }
    public string Name { get; set; } = "";
    public string Team { get; set; } = "";
    public string Category { get; set; } = "";
    public string BroadCategory { get; set; } = "";
    public string Genre { get; set; } = "";
    public string Size { get; set; } = "";
    public string Files { get; set; } = "";
    public string Time { get; set; } = "";
    public bool IsNuked { get; set; }
    public string NukeReason { get; set; } = "";
}

public class WishlistItemVm
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Type { get; set; } = "";
    public string Year { get; set; } = "";
    public string Quality { get; set; } = "";
    public string Status { get; set; } = "";
    public string GrabbedCount { get; set; } = "0";
    public string? PosterUrl { get; set; }
    public string? Plot { get; set; }
    public string? Rating { get; set; }
    public string? Genres { get; set; }
}

public class OverviewServerVm
{
    public string ServerId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public bool IsMounted { get; set; }
    public string StatusLine { get; set; } = "";
    public double CapacityPercent { get; set; } = 0;
    public string CapacityUsedDisplay { get; set; } = "—";
    public string CapacityTotalDisplay { get; set; } = "—";
    public bool IsPrimary { get; set; } = false;
    public string SiteTag { get; set; } = "MOUNTED";
}

public class TrustedCertVm
{
    public string HostKey { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Issuer { get; set; } = "";
    public string NotAfter { get; set; } = "";
}

public class DownloadItemVm : INotifyPropertyChanged
{
    private string _progressText = "";
    private double _progressPercent;

    public string Id { get; set; } = "";
    public string ReleaseName { get; set; } = "";
    public string Category { get; set; } = "";
    public string Status { get; set; } = "";
    public string ProgressText
    {
        get => _progressText;
        set
        {
            if (_progressText == value) return;
            _progressText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProgressText)));
        }
    }
    public double ProgressPercent
    {
        get => _progressPercent;
        set
        {
            if (Math.Abs(_progressPercent - value) < 0.01) return;
            _progressPercent = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProgressPercent)));
        }
    }
    public string LocalPath { get; set; } = "";
    public string ServerId { get; set; } = "";
    public string ServerName { get; set; } = "";

    private string _speedDisplay = "";
    public string SpeedDisplay
    {
        get => _speedDisplay;
        set
        {
            if (_speedDisplay == value) return;
            _speedDisplay = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SpeedDisplay)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public class SearchResultVm
{
    public string ReleaseName { get; set; } = "";
    public string Category { get; set; } = "";
    public string RemotePath { get; set; } = "";
    public long Size { get; set; }
    public string SizeText { get; set; } = "";
    public string ServerId { get; set; } = "";
    public string ServerName { get; set; } = "";
}

public class UpcomingTvEpisodeVm
{
    public int ShowId { get; set; }
    public string ShowName { get; set; } = "";
    public string ShowType { get; set; } = "";
    public string EpisodeInfo { get; set; } = "";
    public string TimeDisplay { get; set; } = "";
    public string NetworkDisplay { get; set; } = "";
    public string DateDisplay { get; set; } = "";
    public DateOnly AirDate { get; set; }
    public string? PosterUrl { get; set; }
    public string? Plot { get; set; }
    public string? Rating { get; set; }
    public string? Genres { get; set; }
    public int TvMazeId { get; set; }
}

public class UpcomingMovieVm
{
    public int TmdbId { get; set; }
    public string Title { get; set; } = "";
    public string Year { get; set; } = "";
    public string ReleaseDateDisplay { get; set; } = "";
    public string ReleaseType { get; set; } = "";
    public string? PosterUrl { get; set; }
    public string? Plot { get; set; }
    public string? Rating { get; set; }
    public string? Genres { get; set; }
    public string? ImdbId { get; set; }
}

public class NotificationItemVm
{
    public string ServerId { get; set; } = "";
    public string ServerName { get; set; } = "";
    public string Category { get; set; } = "";
    public string ReleaseName { get; set; } = "";
    public string RemotePath { get; set; } = "";
    public string TimeDisplay { get; set; } = "";
    public string? PosterUrl { get; set; }
    public string? Plot { get; set; }
    public string? Rating { get; set; }
    public string? Genres { get; set; }
    public bool MetadataLoaded { get; set; }
}

