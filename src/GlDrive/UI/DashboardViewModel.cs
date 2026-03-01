using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Services;
using Serilog;

namespace GlDrive.UI;

public class DashboardViewModel : INotifyPropertyChanged
{
    private readonly ServerManager _serverManager;
    private readonly AppConfig _config;
    private readonly WishlistStore _wishlistStore;
    private string _searchQuery = "";
    private string _searchStatus = "";
    private WishlistItemVm? _selectedWishlistItem;
    private DownloadItemVm? _selectedDownloadItem;
    private SearchResultVm? _selectedSearchResult;
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

    public ObservableCollection<WishlistItemVm> WishlistItems { get; } = new();
    public ObservableCollection<DownloadItemVm> DownloadItems { get; } = new();
    public ObservableCollection<SearchResultVm> SearchResults { get; } = new();
    public ObservableCollection<UpcomingTvEpisodeVm> UpcomingTvEpisodes { get; } = new();
    public ObservableCollection<UpcomingMovieVm> UpcomingMovies { get; } = new();

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

    public string[] QualityOptions { get; } = ["Any", "SD", "720p", "1080p", "2160p"];
    public bool HasTmdbKey => !string.IsNullOrEmpty(_config.Downloads.TmdbApiKey);
    public bool HasNoTmdbKey => string.IsNullOrEmpty(_config.Downloads.TmdbApiKey);

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
    public ICommand DownloadSearchResultCommand { get; }
    public ICommand RefreshMetadataCommand { get; }
    public ICommand LoadUpcomingCommand { get; }
    public ICommand AddUpcomingToWishlistCommand { get; }

    public DashboardViewModel(ServerManager serverManager, AppConfig config)
    {
        _serverManager = serverManager;
        _config = config;

        _wishlistStore = new WishlistStore();
        _wishlistStore.Load();

        AddMovieCommand = new RelayCommand(() => AddMedia(MediaType.Movie));
        AddTvShowCommand = new RelayCommand(() => AddMedia(MediaType.TvShow));
        RemoveWishlistCommand = new RelayCommand(RemoveWishlistItem);
        TogglePauseCommand = new RelayCommand(TogglePause);
        CancelDownloadCommand = new RelayCommand(CancelDownload);
        RetryDownloadCommand = new RelayCommand(RetryDownload);
        ClearCompletedCommand = new RelayCommand(ClearCompleted);
        SearchCommand = new RelayCommand(async () => await PerformSearch());
        DownloadSearchResultCommand = new RelayCommand(DownloadSearchResult);
        RefreshMetadataCommand = new RelayCommand(async () => await RefreshAllMetadata());
        LoadUpcomingCommand = new RelayCommand(async () => await LoadUpcoming(force: true));
        AddUpcomingToWishlistCommand = new RelayCommand(async () => await AddUpcomingToWishlist());

        RefreshWishlist();
        RefreshDownloads();

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
    }

    private void SubscribeToServer(MountService server)
    {
        if (server.Downloads == null) return;
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

    private async Task PerformSearch()
    {
        if (string.IsNullOrWhiteSpace(SearchQuery)) return;

        var mounted = _serverManager.GetMountedServers()
            .Where(s => s.Search != null && s.CurrentState == MountState.Connected)
            .ToList();

        if (mounted.Count == 0)
        {
            SearchStatus = "No connected servers";
            return;
        }

        SearchStatus = "Searching...";
        SearchResults.Clear();

        try
        {
            // Search all servers in parallel
            var tasks = mounted.Select(async server =>
            {
                var results = await server.Search!.Search(SearchQuery);
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
        catch (Exception ex)
        {
            SearchStatus = $"Search failed: {ex.Message}";
            Log.Error(ex, "Dashboard search failed");
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

        server.Downloads.Enqueue(item);
        RefreshDownloads();
    }

    private void OnDownloadProgress(DownloadItem item, DownloadProgress progress)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            HasActiveDownload = true;
            ActiveDownloadName = item.ReleaseName;
            ActiveDownloadSpeed = FormatSpeed(progress.BytesPerSecond);
            ActiveDownloadPercent = progress.TotalBytes > 0
                ? (double)progress.DownloadedBytes / progress.TotalBytes * 100
                : 0;
        });
    }

    private void OnDownloadStatusChanged(DownloadItem item)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            RefreshDownloads();
            if (item.Status != DownloadStatus.Downloading)
            {
                HasActiveDownload = false;
                ActiveDownloadPercent = 0;
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
                    ProgressText = item.Status == DownloadStatus.Downloading && item.TotalBytes > 0
                        ? $"{item.DownloadedBytes * 100 / item.TotalBytes}%"
                        : item.Status == DownloadStatus.Completed ? "Done" : "",
                    ServerId = server.ServerId,
                    ServerName = server.ServerName
                });
            }
        }
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

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
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

public class DownloadItemVm
{
    public string Id { get; set; } = "";
    public string ReleaseName { get; set; } = "";
    public string Category { get; set; } = "";
    public string Status { get; set; } = "";
    public string ProgressText { get; set; } = "";
    public string ServerId { get; set; } = "";
    public string ServerName { get; set; } = "";
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
