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

    public ObservableCollection<WishlistItemVm> WishlistItems { get; } = new();
    public ObservableCollection<DownloadItemVm> DownloadItems { get; } = new();
    public ObservableCollection<SearchResultVm> SearchResults { get; } = new();

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
        var localBase = _config.Downloads.LocalPath;

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
