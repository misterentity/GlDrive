using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using GlDrive.Config;
using GlDrive.Downloads;
using Serilog;

namespace GlDrive.UI;

/// <summary>Upcoming tab: TVMaze schedule, TMDB movie releases and add-to-wishlist.
/// Wishlist refresh and the shared release-date gate stay with the dashboard and are
/// passed in explicitly.</summary>
public sealed class UpcomingViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly AppConfig _config;
    private readonly WishlistStore _wishlistStore;
    private readonly Action _wishlistChanged;
    private volatile bool _disposed;
    private UpcomingTvEpisodeVm? _selectedTvEpisode;
    private UpcomingMovieVm? _selectedUpcomingMovie;
    private string? _upcomingQualityText;
    private string _upcomingStatus = "";
    private DateTime _tvCacheTime;
    private DateTime _movieCacheTime;
    private string _showSearchQuery = "";
    private bool _isShowSearchActive;
    private List<UpcomingTvEpisodeVm>? _cachedTvSchedule;
    private string _tvTypeFilter = "Scripted";

    /// <param name="wishlistChanged">Rebuilds the dashboard's wishlist rows after an add.</param>
    /// <param name="refreshReleaseDates">Runs the dashboard's single-flight release-date refresh
    /// for <see cref="SelectedUpcomingMovie"/> (shared with the Wishlist tab).</param>
    public UpcomingViewModel(AppConfig config, WishlistStore wishlistStore, Action wishlistChanged, Func<Task> refreshReleaseDates)
    {
        _config = config;
        _wishlistStore = wishlistStore;
        _wishlistChanged = wishlistChanged;
        RefreshUpcomingCalendarCommand = new RelayCommand(async () => await refreshReleaseDates());
        LoadUpcomingCommand = new RelayCommand(async () => await LoadUpcoming(force: true));
        AddUpcomingToWishlistCommand = new RelayCommand(async () => await AddUpcomingToWishlist());
        SearchShowScheduleCommand = new RelayCommand(async () => await SearchShowSchedule());
        ClearShowSearchCommand = new RelayCommand(ClearShowSearch);
    }

    public ICommand RefreshUpcomingCalendarCommand { get; }
    public ICommand LoadUpcomingCommand { get; }
    public ICommand AddUpcomingToWishlistCommand { get; }
    public ICommand SearchShowScheduleCommand { get; }
    public ICommand ClearShowSearchCommand { get; }

    public ObservableCollection<UpcomingTvEpisodeVm> UpcomingTvEpisodes { get; } = new();
    public ObservableCollection<UpcomingMovieVm> UpcomingMovies { get; } = new();

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
        _selectedTvEpisode?.Rating is { Length: > 0 } tr ? $"★ {tr}/10" :
        _selectedUpcomingMovie?.Rating is { Length: > 0 } mr ? $"★ {mr}/10" : "";
    public string UpcomingGenres => _selectedTvEpisode?.Genres ?? _selectedUpcomingMovie?.Genres ?? "";
    public string UpcomingPlot => _selectedTvEpisode?.Plot ?? _selectedUpcomingMovie?.Plot ?? "";

    public string UpcomingQualityText
    {
        get => _upcomingQualityText ?? WishlistQuality.Display(_config.Downloads.QualityDefault);
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
                var tvMaze = MetadataClients.TvMaze;
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
                            Plot = TvMazeClient.StripHtml(ep.Show.Summary),
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
                var tmdb = MetadataClients.Tmdb;
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
            var tvMaze = MetadataClients.TvMaze;
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
                    Plot = TvMazeClient.StripHtml(result.Show.Summary),
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

            var quality = WishlistQuality.Parse(UpcomingQualityText);
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
            _wishlistChanged();
            UpcomingStatus = $"Added {ep.ShowName} to wishlist";
        }
        else if (_selectedUpcomingMovie != null)
        {
            var movie = _selectedUpcomingMovie;

            // Fetch detail for ImdbId if we don't have it
            if (string.IsNullOrEmpty(movie.ImdbId) && HasTmdbKey)
            {
                var tmdb = MetadataClients.Tmdb;
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

            var quality = WishlistQuality.Parse(UpcomingQualityText);
            _wishlistStore.Add(new WishlistItem
            {
                Type = MediaType.Movie,
                Title = movie.Title,
                Year = int.TryParse(movie.Year, out var y) ? y : null,
                ImdbId = movie.ImdbId,
                TmdbId = movie.TmdbId,
                ReleaseDates = movie.ReleaseDates,
                Quality = quality,
                PosterUrl = movie.PosterUrl,
                Plot = movie.Plot,
                Rating = movie.Rating,
                Genres = movie.Genres
            });
            _wishlistChanged();
            UpcomingStatus = $"Added {movie.Title} to wishlist";
        }
    }

    /// <summary>Looks up TMDB release dates for <paramref name="movie"/>, keeping saved dates
    /// when the lookup is unavailable. Returns the status line the caller shows.</summary>
    internal async Task<string> RefreshReleaseDates(UpcomingMovieVm movie, CancellationToken ct)
    {
        var client = MetadataClients.Tmdb;
        var dates = await client.GetReleaseDates(movie.TmdbId, _config.Downloads.MovieCalendarRegion, ct);
        ct.ThrowIfCancellationRequested();
        if (dates.Status != "unavailable") movie.ReleaseDates = dates;
        return dates.Status == "unavailable" ? "Release date lookup failed; saved dates are retained." :
            $"{movie.Title}: {dates.ReleaseType} ({dates.Region})";
    }

    public void Dispose() => _disposed = true;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        if (!_disposed) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
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

public class UpcomingMovieVm : INotifyPropertyChanged
{
    public int TmdbId { get; set; }
    public string Title { get; set; } = "";
    public string Year { get; set; } = "";
    public string ReleaseDateDisplay { get; set; } = "";
    private MovieReleaseDates? _releaseDates;
    public MovieReleaseDates? ReleaseDates
    {
        get => _releaseDates;
        set
        {
            _releaseDates = value;
            foreach (var property in new[] { nameof(ReleaseDates), nameof(ReleaseType), nameof(TheatricalDate), nameof(DigitalDate), nameof(PhysicalDate) })
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        }
    }
    public string ReleaseType => ReleaseDates?.ReleaseType ?? "Unknown";
    public string TheatricalDate => MovieReleaseDates.Display(ReleaseDates?.Theatrical);
    public string DigitalDate => MovieReleaseDates.Display(ReleaseDates?.Digital);
    public string PhysicalDate => MovieReleaseDates.Display(ReleaseDates?.Physical);
    public event PropertyChangedEventHandler? PropertyChanged;
    public string? PosterUrl { get; set; }
    public string? Plot { get; set; }
    public string? Rating { get; set; }
    public string? Genres { get; set; }
    public string? ImdbId { get; set; }
}
