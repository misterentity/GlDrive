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
using Microsoft.Win32;
using Serilog;

namespace GlDrive.UI;

public class DashboardViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ServerManager _serverManager;
    private readonly AppConfig _config;
    private readonly WishlistStore _wishlistStore;
    private readonly NotificationStore _notificationStore;
    private readonly DownloadHistoryStore _historyStore;
    private readonly IrcViewModel _ircViewModel;
    private string _searchQuery = "";
    private string _searchStatus = "";
    private bool _isSearching;
    private CancellationTokenSource? _searchCts;
    private readonly HashSet<string> _subscribedServers = new();
    private WishlistItemVm? _selectedWishlistItem;
    private DownloadItemVm? _selectedDownloadItem;
    private SearchResultVm? _selectedSearchResult;
    private NotificationItemVm? _selectedNotificationItem;
    private string _activeDownloadName = "";
    private string _activeDownloadSpeed = "";
    private double _activeDownloadPercent;
    private bool _hasActiveDownload;
    private UpcomingTvEpisodeVm? _selectedTvEpisode;
    private UpcomingMovieVm? _selectedUpcomingMovie;
    private string _upcomingQualityText = "1080p";
    private string _upcomingStatus = "";
    private DateTime _tvCacheTime;
    private DateTime _movieCacheTime;
    private string _showSearchQuery = "";
    private bool _isShowSearchActive;
    private List<UpcomingTvEpisodeVm>? _cachedTvSchedule;

    // PreDB
    private readonly PreDbClient _preDbClient = new();
    private string _preDbQuery = "";
    private string _preDbStatus = "";
    private bool _isPreDbSearching;
    private CancellationTokenSource? _preDbCts;
    private PreDbItemVm? _selectedPreDbItem;

    // Notification filter state
    private List<NotificationItemVm> _allNotifications = new();
    private string _notificationFilterText = "";
    private string _notificationFilterCategory = "All";
    private string _notificationFilterServer = "All";

    // History
    private DownloadHistoryItemVm? _selectedHistoryItem;

    // Status bar
    private string _statusBarSpeed = "";
    private string _statusBarQueueCount = "";
    private string _statusBarDiskSpace = "";
    private DispatcherTimer? _statusTimer;

    // Bandwidth graph
    private readonly List<double> _speedHistory = new();
    private PointCollection _speedGraphPoints = new();
    private double _latestSpeed;

    public IrcViewModel Irc => _ircViewModel;

    public ObservableCollection<NotificationItemVm> NotificationItems { get; } = new();
    public ObservableCollection<WishlistItemVm> WishlistItems { get; } = new();
    public ObservableCollection<DownloadItemVm> DownloadItems { get; } = new();
    public ObservableCollection<SearchResultVm> SearchResults { get; } = new();
    public ObservableCollection<UpcomingTvEpisodeVm> UpcomingTvEpisodes { get; } = new();
    public ObservableCollection<UpcomingMovieVm> UpcomingMovies { get; } = new();
    public ObservableCollection<DownloadHistoryItemVm> HistoryItems { get; } = new();
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

    public string[] QualityOptions { get; } = ["Any", "SD", "720p", "1080p", "2160p"];
    public bool HasTmdbKey => !string.IsNullOrEmpty(_config.Downloads.TmdbApiKey);
    public bool HasNoTmdbKey => string.IsNullOrEmpty(_config.Downloads.TmdbApiKey);

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

    // History
    public DownloadHistoryItemVm? SelectedHistoryItem
    {
        get => _selectedHistoryItem;
        set { _selectedHistoryItem = value; OnPropertyChanged(); }
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

    public string ActiveDownloadName
    {
        get => _activeDownloadName;
        set { _activeDownloadName = value; OnPropertyChanged(); }
    }

    public string ActiveDownloadSpeed
    {
        get => _activeDownloadSpeed;
        set { _activeDownloadSpeed = value; OnPropertyChanged(); }
    }

    public double ActiveDownloadPercent
    {
        get => _activeDownloadPercent;
        set { _activeDownloadPercent = value; OnPropertyChanged(); }
    }

    public bool HasActiveDownload
    {
        get => _hasActiveDownload;
        set { _hasActiveDownload = value; OnPropertyChanged(); }
    }

    public ICommand AddMovieCommand { get; }
    public ICommand AddTvShowCommand { get; }
    public ICommand RemoveWishlistCommand { get; }
    public ICommand TogglePauseCommand { get; }
    public ICommand CancelDownloadCommand { get; }
    public ICommand RetryDownloadCommand { get; }
    public ICommand ClearCompletedCommand { get; }
    public ICommand SearchCommand { get; }
    public ICommand CancelSearchCommand { get; }
    public ICommand DownloadSearchResultCommand { get; }
    public ICommand RefreshMetadataCommand { get; }
    public ICommand ClearNotificationsCommand { get; }
    public ICommand DownloadNotificationCommand { get; }
    public ICommand LoadUpcomingCommand { get; }
    public ICommand AddUpcomingToWishlistCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand ClearHistoryCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand ExportWishlistCommand { get; }
    public ICommand ImportWishlistCommand { get; }
    public ICommand SearchShowScheduleCommand { get; }
    public ICommand ClearShowSearchCommand { get; }
    public ICommand SearchPreDbCommand { get; }
    public ICommand CancelPreDbSearchCommand { get; }
    public ICommand LoadLatestPreDbCommand { get; }

    public DashboardViewModel(ServerManager serverManager, AppConfig config, NotificationStore notificationStore)
    {
        _serverManager = serverManager;
        _config = config;
        _notificationStore = notificationStore;
        _historyStore = serverManager.HistoryStore;

        _wishlistStore = new WishlistStore();
        _wishlistStore.Load();

        _ircViewModel = new IrcViewModel(serverManager, config);

        AddMovieCommand = new RelayCommand(() => AddMedia(MediaType.Movie));
        AddTvShowCommand = new RelayCommand(() => AddMedia(MediaType.TvShow));
        RemoveWishlistCommand = new RelayCommand(RemoveWishlistItem);
        TogglePauseCommand = new RelayCommand(TogglePause);
        CancelDownloadCommand = new RelayCommand(CancelDownload);
        RetryDownloadCommand = new RelayCommand(RetryDownload);
        ClearCompletedCommand = new RelayCommand(ClearCompleted);
        ClearNotificationsCommand = new RelayCommand(ClearNotifications);
        DownloadNotificationCommand = new RelayCommand(DownloadNotification);
        SearchCommand = new RelayCommand(async () => await PerformSearch());
        CancelSearchCommand = new RelayCommand(CancelSearch);
        DownloadSearchResultCommand = new RelayCommand(DownloadSearchResult);
        RefreshMetadataCommand = new RelayCommand(async () => await RefreshAllMetadata());
        LoadUpcomingCommand = new RelayCommand(async () => await LoadUpcoming(force: true));
        AddUpcomingToWishlistCommand = new RelayCommand(async () => await AddUpcomingToWishlist());
        MoveUpCommand = new RelayCommand(MoveUpDownload);
        MoveDownCommand = new RelayCommand(MoveDownDownload);
        ClearHistoryCommand = new RelayCommand(ClearHistory);
        OpenFolderCommand = new RelayCommand(OpenFolder);
        ExportWishlistCommand = new RelayCommand(ExportWishlist);
        ImportWishlistCommand = new RelayCommand(ImportWishlist);
        SearchShowScheduleCommand = new RelayCommand(async () => await SearchShowSchedule());
        ClearShowSearchCommand = new RelayCommand(ClearShowSearch);
        SearchPreDbCommand = new RelayCommand(async () => await PerformPreDbSearch());
        CancelPreDbSearchCommand = new RelayCommand(CancelPreDbSearch);
        LoadLatestPreDbCommand = new RelayCommand(async () => await LoadLatestPreDb());

        // Status bar timer
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statusTimer.Tick += (_, _) => UpdateStatusBar();
        _statusTimer.Start();

        RefreshNotifications();
        RefreshWishlist();
        RefreshDownloads();
        RefreshHistory();

        // Subscribe to download progress events from all mounted servers
        foreach (var server in _serverManager.GetMountedServers())
            SubscribeToServer(server);

        // Also subscribe when new servers come online
        _serverManager.ServerStateChanged += (serverId, _, state) =>
        {
            if (state == MountState.Connected)
            {
                var server = _serverManager.GetServer(serverId);
                if (server != null)
                {
                    SubscribeToServer(server);
                    Application.Current?.Dispatcher.Invoke(RefreshDownloads);
                }
            }
        };

        // Live notifications — add to collection when new releases arrive
        _serverManager.NewReleaseDetected += (serverId, serverName, category, release, remotePath) =>
        {
            Application.Current?.Dispatcher.Invoke(() =>
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
                UpdateNotificationFilterOptions();
                ApplyNotificationFilter();
            });
        };
    }

    private void SubscribeToServer(MountService server)
    {
        if (server.Downloads == null) return;
        if (!_subscribedServers.Add(server.ServerId)) return;
        server.Downloads.DownloadProgressChanged += OnDownloadProgress;
        server.Downloads.DownloadStatusChanged += OnDownloadStatusChanged;
    }

    private void AddMedia(MediaType type)
    {
        var dialog = new MetadataSearchDialog(type, _config.Downloads.OmdbApiKey)
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

        var progress = new Progress<string>(msg =>
            Application.Current?.Dispatcher.Invoke(() => SearchStatus = msg));

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
        if (SelectedSearchResult == null) return;

        var result = SelectedSearchResult;
        var server = _serverManager.GetServer(result.ServerId);
        if (server?.Downloads == null) return;

        var parsed = SceneNameParser.Parse(result.ReleaseName);
        var localBase = _config.Downloads.GetPathForCategory(result.Category);

        string localPath;
        if (parsed.Season != null)
        {
            var seasonFolder = $"Season {parsed.Season:D2}";
            localPath = Path.Combine(localBase, "TV", parsed.Title, seasonFolder, result.ReleaseName);
        }
        else if (parsed.Year != null)
        {
            localPath = Path.Combine(localBase, "Movies", $"{parsed.Title} ({parsed.Year})", result.ReleaseName);
        }
        else
        {
            localPath = Path.Combine(localBase, result.Category, result.ReleaseName);
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

        if (!server.Downloads.Enqueue(item))
        {
            SearchStatus = $"Skipped: {result.ReleaseName} (duplicate or already exists)";
            return;
        }
        RefreshDownloads();
    }

    private void OnDownloadProgress(DownloadItem item, DownloadProgress progress)
    {
        _latestSpeed = progress.BytesPerSecond;
        Application.Current?.Dispatcher.Invoke(() =>
        {
            HasActiveDownload = true;
            ActiveDownloadName = item.ReleaseName;
            ActiveDownloadSpeed = FormatSpeed(progress.BytesPerSecond);
            ActiveDownloadPercent = progress.TotalBytes > 0
                ? (double)progress.DownloadedBytes / progress.TotalBytes * 100
                : 0;

            // Update the grid row's Progress column
            var vm = DownloadItems.FirstOrDefault(d => d.Id == item.Id);
            if (vm != null)
            {
                var pct = progress.TotalBytes > 0
                    ? (int)(progress.DownloadedBytes * 100 / progress.TotalBytes)
                    : 0;
                var fileName = progress.CurrentFileName ?? "";
                vm.ProgressText = string.IsNullOrEmpty(fileName)
                    ? $"{pct}%"
                    : $"{pct}% — {fileName}";
            }
        });
    }

    private void OnDownloadStatusChanged(DownloadItem item)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            RefreshDownloads();
            if (item.Status == DownloadStatus.Completed || item.Status == DownloadStatus.Failed)
                RefreshHistory();
            if (item.Status != DownloadStatus.Downloading && item.Status != DownloadStatus.Extracting)
            {
                HasActiveDownload = false;
                ActiveDownloadPercent = 0;
                _latestSpeed = 0;
            }
        });
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

                for (int d = 0; d < 7; d++)
                {
                    var date = today.AddDays(d);
                    var schedule = await tvMaze.GetSchedule(date);

                    foreach (var ep in schedule.Where(e => e.Show != null))
                    {
                        episodes.Add(new UpcomingTvEpisodeVm
                        {
                            ShowId = ep.Show!.Id,
                            ShowName = ep.Show.Name,
                            EpisodeInfo = $"S{ep.Season:D2}E{ep.Number:D2} — {ep.Name}",
                            TimeDisplay = ep.Airtime ?? "",
                            NetworkDisplay = ep.Show.Network?.Name ?? "",
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
                UpcomingTvEpisodes.Clear();
                foreach (var ep in episodes)
                    UpcomingTvEpisodes.Add(ep);

                _tvCacheTime = now;
            }

            if (loadMovies && HasTmdbKey)
            {
                using var tmdb = new TmdbClient(_config.Downloads.TmdbApiKey);
                var from = DateOnly.FromDateTime(DateTime.Today.AddDays(-7));
                var to = DateOnly.FromDateTime(DateTime.Today.AddDays(14));
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
                        ? $"S{ep.Season:D2}E{ep.Number:D2} — {ep.Name}"
                        : "No upcoming episodes",
                    TimeDisplay = ep?.Airtime ?? "",
                    NetworkDisplay = result.Show.Network?.Name ?? "",
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
        UpcomingTvEpisodes.Clear();
        if (_cachedTvSchedule != null)
        {
            foreach (var ep in _cachedTvSchedule)
                UpcomingTvEpisodes.Add(ep);
        }
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
                using var tmdb = new TmdbClient(_config.Downloads.TmdbApiKey);
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
        using var omdb = new OmdbClient(_config.Downloads.OmdbApiKey);
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
                var omdbKey = _config.Downloads.OmdbApiKey;
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
                Application.Current?.Dispatcher.Invoke(NotifyNotificationDetailChanged);
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

        string localPath;
        if (parsed.Season != null)
        {
            var seasonFolder = $"Season {parsed.Season:D2}";
            localPath = Path.Combine(localBase, "TV", parsed.Title, seasonFolder, n.ReleaseName);
        }
        else if (parsed.Year != null)
        {
            localPath = Path.Combine(localBase, "Movies", $"{parsed.Title} ({parsed.Year})", n.ReleaseName);
        }
        else
        {
            localPath = Path.Combine(localBase, n.Category, n.ReleaseName);
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
                    LocalPath = item.LocalPath,
                    ServerId = server.ServerId,
                    ServerName = server.ServerName
                });
            }
        }
    }

    private void RefreshHistory()
    {
        HistoryItems.Clear();
        foreach (var item in _historyStore.Items)
        {
            HistoryItems.Add(new DownloadHistoryItemVm
            {
                Id = item.Id,
                ReleaseName = item.ReleaseName,
                ServerName = item.ServerName,
                Category = item.Category,
                SizeText = FormatSize(item.TotalBytes),
                FinalStatus = item.FinalStatus,
                ErrorMessage = item.ErrorMessage,
                CompletedAt = item.CompletedAt.ToLocalTime().ToString("g")
            });
        }
    }

    private void ClearHistory()
    {
        _historyStore.Clear();
        HistoryItems.Clear();
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
        // Total speed from active downloads
        _speedHistory.Add(_latestSpeed);
        if (_speedHistory.Count > 60) _speedHistory.RemoveAt(0);

        StatusBarSpeed = _latestSpeed > 0 ? FormatSpeed(_latestSpeed) : "Idle";

        // Queue count
        var queued = 0;
        foreach (var server in _serverManager.GetMountedServers())
        {
            var store = server.Downloads?.Store;
            if (store == null) continue;
            queued += store.Items.Count(i => i.Status == DownloadStatus.Queued || i.Status == DownloadStatus.Downloading);
        }
        StatusBarQueueCount = $"{queued} active";

        // Disk space
        try
        {
            var dlPath = _config.Downloads.LocalPath;
            var root = Path.GetPathRoot(dlPath);
            if (!string.IsNullOrEmpty(root))
            {
                var drive = new DriveInfo(root);
                if (drive.IsReady)
                    StatusBarDiskSpace = $"{FormatSize(drive.AvailableFreeSpace)} free";
            }
        }
        catch { StatusBarDiskSpace = ""; }

        // Update bandwidth graph points
        UpdateSpeedGraph();
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
        PreDbStatus = "Loading latest releases...";

        try
        {
            _preDbCts?.Cancel();
            _preDbCts = new CancellationTokenSource();
            var results = await _preDbClient.GetLatestAsync(ct: _preDbCts.Token);
            PopulatePreDbItems(results);
            PreDbStatus = $"{results.Length} releases";
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

    private void PopulatePreDbItems(PreDbRelease[] releases)
    {
        PreDbItems.Clear();
        foreach (var r in releases)
        {
            PreDbItems.Add(new PreDbItemVm
            {
                Name = r.Name,
                Team = r.Team,
                Category = r.Cat,
                Genre = r.Genre,
                Size = r.SizeFormatted,
                Files = r.Files > 0 ? r.Files.ToString() : "",
                Time = r.PreTime.ToString("g"),
                IsNuked = r.Nuke != null,
                NukeReason = r.Nuke?.Reason ?? ""
            });
        }
    }

    public void Dispose()
    {
        _statusTimer?.Stop();
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;
        _preDbCts?.Cancel();
        _preDbCts?.Dispose();
        _preDbCts = null;
        _preDbClient.Dispose();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class PreDbItemVm
{
    public string Name { get; set; } = "";
    public string Team { get; set; } = "";
    public string Category { get; set; } = "";
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

public class DownloadItemVm : INotifyPropertyChanged
{
    private string _progressText = "";

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
    public string LocalPath { get; set; } = "";
    public string ServerId { get; set; } = "";
    public string ServerName { get; set; } = "";

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

public class DownloadHistoryItemVm
{
    public string Id { get; set; } = "";
    public string ReleaseName { get; set; } = "";
    public string ServerName { get; set; } = "";
    public string Category { get; set; } = "";
    public string SizeText { get; set; } = "";
    public string FinalStatus { get; set; } = "";
    public string? ErrorMessage { get; set; }
    public string CompletedAt { get; set; } = "";
}
