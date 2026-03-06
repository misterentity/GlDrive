using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using FluentFTP;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Player;
using GlDrive.Services;
using LibVLCSharp.Shared;
using Serilog;

namespace GlDrive.UI;

public class PlayerViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ServerManager _serverManager;
    private readonly AppConfig _config;
    private MediaStreamServer? _streamServer;
    private LibVLC? _libVLC;
    private MediaPlayer? _mediaPlayer;
    private bool _vlcInitialized;

    private string _playerStatus = "";
    private bool _isLoading;
    private bool _isPlaying;
    private bool _isBuffering;
    private double _bufferProgress;
    private MediaCardVm? _selectedMovie;
    private MediaCardVm? _selectedTvShow;
    private PlayerSearchResultVm? _selectedFtpResult;
    private double _volume = 80;
    private double _position;
    private string _timeDisplay = "00:00 / 00:00";

    public ObservableCollection<MediaCardVm> TrendingMovies { get; } = new();
    public ObservableCollection<MediaCardVm> TrendingTvShows { get; } = new();
    public ObservableCollection<PlayerSearchResultVm> FtpResults { get; } = new();

    public MediaCardVm? SelectedMovie
    {
        get => _selectedMovie;
        set
        {
            _selectedMovie = value;
            if (value != null) _selectedTvShow = null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedTvShow));
            OnPropertyChanged(nameof(SelectedMedia));
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(DetailTitle));
            OnPropertyChanged(nameof(DetailYear));
            OnPropertyChanged(nameof(DetailRating));
            OnPropertyChanged(nameof(DetailGenres));
            OnPropertyChanged(nameof(DetailPlot));
            OnPropertyChanged(nameof(DetailPosterUrl));
            OnPropertyChanged(nameof(HasDetailPoster));
        }
    }

    public MediaCardVm? SelectedTvShow
    {
        get => _selectedTvShow;
        set
        {
            _selectedTvShow = value;
            if (value != null) _selectedMovie = null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedMovie));
            OnPropertyChanged(nameof(SelectedMedia));
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(DetailTitle));
            OnPropertyChanged(nameof(DetailYear));
            OnPropertyChanged(nameof(DetailRating));
            OnPropertyChanged(nameof(DetailGenres));
            OnPropertyChanged(nameof(DetailPlot));
            OnPropertyChanged(nameof(DetailPosterUrl));
            OnPropertyChanged(nameof(HasDetailPoster));
        }
    }

    public MediaCardVm? SelectedMedia => _selectedMovie ?? _selectedTvShow;
    public bool HasSelection => SelectedMedia != null;
    public string DetailTitle => SelectedMedia?.Title ?? "";
    public string DetailYear => SelectedMedia?.Year ?? "";
    public string DetailRating => SelectedMedia?.VoteAverage > 0 ? $"\u2605 {SelectedMedia.VoteAverage:F1}/10" : "";
    public string DetailGenres => SelectedMedia?.Genres ?? "";
    public string DetailPlot => SelectedMedia?.Plot ?? "";
    public string? DetailPosterUrl => SelectedMedia?.PosterUrl;
    public bool HasDetailPoster => !string.IsNullOrEmpty(DetailPosterUrl);

    public PlayerSearchResultVm? SelectedFtpResult
    {
        get => _selectedFtpResult;
        set { _selectedFtpResult = value; OnPropertyChanged(); }
    }

    public string PlayerStatus
    {
        get => _playerStatus;
        set { _playerStatus = value; OnPropertyChanged(); }
    }

    public bool IsLoading
    {
        get => _isLoading;
        set { _isLoading = value; OnPropertyChanged(); }
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        set { _isPlaying = value; OnPropertyChanged(); }
    }

    public bool IsBuffering
    {
        get => _isBuffering;
        set { _isBuffering = value; OnPropertyChanged(); }
    }

    public double BufferProgress
    {
        get => _bufferProgress;
        set { _bufferProgress = value; OnPropertyChanged(); }
    }

    public double Volume
    {
        get => _volume;
        set
        {
            _volume = value;
            OnPropertyChanged();
            if (_mediaPlayer != null) _mediaPlayer.Volume = (int)value;
        }
    }

    public double Position
    {
        get => _position;
        set { _position = value; OnPropertyChanged(); }
    }

    public string TimeDisplay
    {
        get => _timeDisplay;
        set { _timeDisplay = value; OnPropertyChanged(); }
    }

    public MediaPlayer? Player => _mediaPlayer;

    public ICommand SearchAndPlayCommand { get; }
    public ICommand PlayResultCommand { get; }
    public ICommand PlayPauseCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand RefreshTrendingCommand { get; }

    public PlayerViewModel(ServerManager serverManager, AppConfig config)
    {
        _serverManager = serverManager;
        _config = config;

        SearchAndPlayCommand = new RelayCommand(async () => await SearchAndPlay());
        PlayResultCommand = new RelayCommand(async () => await PlaySelectedResult());
        PlayPauseCommand = new RelayCommand(TogglePlayPause);
        StopCommand = new RelayCommand(StopPlayback);
        RefreshTrendingCommand = new RelayCommand(async () => await LoadTrending());
    }

    public void InitVLC()
    {
        if (_vlcInitialized) return;
        try
        {
            Core.Initialize();
            _libVLC = new LibVLC(
                "--no-video-title-show",
                "--network-caching=10000",
                "--file-caching=5000",
                "--live-caching=5000",
                "--http-reconnect"
            );
            _mediaPlayer = new MediaPlayer(_libVLC);
            _mediaPlayer.PositionChanged += (_, e) =>
            {
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    _position = e.Position * 100;
                    OnPropertyChanged(nameof(Position));
                    UpdateTimeDisplay();
                });
            };
            _mediaPlayer.Buffering += (_, e) =>
            {
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    BufferProgress = e.Cache;
                    IsBuffering = e.Cache < 100;
                    if (e.Cache < 100)
                        PlayerStatus = $"Buffering... {e.Cache:F0}%";
                });
            };
            _mediaPlayer.Opening += (_, _) =>
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    IsBuffering = true;
                    PlayerStatus = "Connecting to stream...";
                });
            _mediaPlayer.Playing += (_, _) =>
                Application.Current?.Dispatcher.Invoke(() => { IsPlaying = true; IsBuffering = false; });
            _mediaPlayer.Stopped += (_, _) =>
                Application.Current?.Dispatcher.Invoke(() => { IsPlaying = false; IsBuffering = false; PlayerStatus = "Stopped"; });
            _mediaPlayer.EndReached += (_, _) =>
                Application.Current?.Dispatcher.Invoke(() => { IsPlaying = false; IsBuffering = false; PlayerStatus = "Finished"; });
            _mediaPlayer.EncounteredError += (_, _) =>
                Application.Current?.Dispatcher.Invoke(() => { IsPlaying = false; IsBuffering = false; PlayerStatus = "Playback error"; });
            _mediaPlayer.Volume = (int)_volume;

            _streamServer = new MediaStreamServer(_serverManager);
            _streamServer.Start();

            _vlcInitialized = true;
            OnPropertyChanged(nameof(Player));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize LibVLC");
            PlayerStatus = "Failed to initialize video player";
        }
    }

    public async Task LoadTrending()
    {
        if (string.IsNullOrEmpty(_config.Downloads.TmdbApiKey))
        {
            PlayerStatus = "Configure TMDb API key in Settings \u2192 Downloads to see trending content";
            return;
        }

        IsLoading = true;
        PlayerStatus = "Loading trending content...";

        try
        {
            using var tmdb = new TmdbClient(_config.Downloads.TmdbApiKey);

            var movies = await tmdb.GetTrendingMovies();
            TrendingMovies.Clear();
            foreach (var m in movies.Take(20))
            {
                TrendingMovies.Add(new MediaCardVm
                {
                    Title = m.Title,
                    Year = m.YearParsed?.ToString() ?? "",
                    PosterUrl = m.PosterUrl,
                    Plot = m.Overview ?? "",
                    Genres = m.GenreText,
                    VoteAverage = m.VoteAverage,
                    SearchTitle = m.Title,
                    MediaType = "Movie"
                });
            }

            var shows = await tmdb.GetTrendingTvShows();
            TrendingTvShows.Clear();
            foreach (var s in shows.Take(20))
            {
                TrendingTvShows.Add(new MediaCardVm
                {
                    Title = s.Name,
                    Year = s.YearParsed?.ToString() ?? "",
                    PosterUrl = s.PosterUrl,
                    Plot = s.Overview ?? "",
                    Genres = s.GenreText,
                    VoteAverage = s.VoteAverage,
                    SearchTitle = s.Name,
                    MediaType = "TV"
                });
            }

            PlayerStatus = $"{TrendingMovies.Count} movies, {TrendingTvShows.Count} TV shows";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load trending");
            PlayerStatus = "Failed to load trending content";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task SearchAndPlay()
    {
        var media = SelectedMedia;
        if (media == null) return;

        var mounted = _serverManager.GetMountedServers()
            .Where(s => s.Search != null && s.CurrentState == MountState.Connected)
            .ToList();

        if (mounted.Count == 0)
        {
            PlayerStatus = "No connected servers";
            return;
        }

        IsLoading = true;
        FtpResults.Clear();
        PlayerStatus = $"Searching for \"{media.SearchTitle}\"...";

        try
        {
            var tasks = mounted.Select(async server =>
            {
                var results = await server.Search!.Search(media.SearchTitle);
                return results.Select(r => new PlayerSearchResultVm
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
            foreach (var serverResults in allResults)
                foreach (var r in serverResults)
                    FtpResults.Add(r);

            PlayerStatus = FtpResults.Count > 0
                ? $"{FtpResults.Count} result(s) found — select one and click Play"
                : $"No results found for \"{media.SearchTitle}\"";

            // Auto-select and play the first result
            if (FtpResults.Count > 0)
            {
                SelectedFtpResult = FtpResults[0];
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Player search failed");
            PlayerStatus = "Search failed";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task PlaySelectedResult()
    {
        if (_selectedFtpResult == null || _streamServer == null || _mediaPlayer == null) return;

        var result = _selectedFtpResult;
        var server = _serverManager.GetServer(result.ServerId);
        if (server?.Pool == null) return;

        IsLoading = true;
        PlayerStatus = $"Loading {result.ReleaseName}...";

        try
        {
            // List files in the release directory
            var files = await server.Search!.GetReleaseFiles(result.RemotePath);

            // Find a direct video file first
            var videoFile = files
                .Where(f => IsVideoFile(f.Name))
                .OrderByDescending(f => f.Size)
                .FirstOrDefault();

            if (videoFile != null)
            {
                // Direct video file — stream via HTTP
                await PlayFromFtp(result.ServerId, videoFile.FullName);
                return;
            }

            // Check for RAR files
            var rarFile = files
                .Where(f => f.Name.EndsWith(".rar", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.Name)
                .FirstOrDefault();

            if (rarFile != null)
            {
                // RAR content — download, extract, play
                await PlayFromRar(server, result.RemotePath, files);
                return;
            }

            PlayerStatus = "No playable content found in this release";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to play {Release}", result.ReleaseName);
            PlayerStatus = $"Failed to play: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task PlayFromFtp(string serverId, string remotePath)
    {
        var url = $"{_streamServer!.BaseUrl}stream?server={Uri.EscapeDataString(serverId)}&path={Uri.EscapeDataString(remotePath)}";
        Log.Information("Playing FTP stream: {Url}", url);

        var media = new Media(_libVLC!, new Uri(url));
        media.AddOption(":network-caching=10000");
        media.AddOption(":file-caching=5000");
        media.AddOption(":http-reconnect");

        IsBuffering = true;
        PlayerStatus = "Connecting to stream...";

        // Stop current playback first (on threadpool to avoid deadlock)
        await Task.Run(() => _mediaPlayer!.Stop());
        _mediaPlayer!.Play(media);

        var fileName = Path.GetFileName(remotePath);
        PlayerStatus = $"Buffering: {fileName}...";
    }

    private async Task PlayFromRar(MountService server, string releasePath, List<FtpListItem> files)
    {
        // Stream RAR extraction via HTTP — volumes download on-demand, VLC plays immediately
        var url = $"{_streamServer!.BaseUrl}rar-stream?server={Uri.EscapeDataString(server.ServerId)}&path={Uri.EscapeDataString(releasePath)}";
        Log.Information("Playing RAR stream: {Url}", url);

        var media = new Media(_libVLC!, new Uri(url));
        media.AddOption(":network-caching=15000");
        media.AddOption(":file-caching=5000");
        media.AddOption(":http-reconnect");

        IsBuffering = true;
        PlayerStatus = "Downloading & extracting RAR...";

        await Task.Run(() => _mediaPlayer!.Stop());
        _mediaPlayer!.Play(media);

        PlayerStatus = $"Buffering: {Path.GetFileName(releasePath)}...";
    }

    private void TogglePlayPause()
    {
        if (_mediaPlayer == null) return;
        if (_mediaPlayer.IsPlaying)
            _mediaPlayer.Pause();
        else
            _mediaPlayer.Play();
    }

    private void StopPlayback()
    {
        if (_mediaPlayer == null) return;
        Task.Run(() => _mediaPlayer.Stop());
    }

    public void SeekTo(double percent)
    {
        if (_mediaPlayer == null || !_mediaPlayer.IsPlaying) return;
        _mediaPlayer.Position = (float)(percent / 100.0);
    }

    private void UpdateTimeDisplay()
    {
        if (_mediaPlayer == null) return;
        var current = TimeSpan.FromMilliseconds(_mediaPlayer.Time);
        var total = TimeSpan.FromMilliseconds(_mediaPlayer.Length);
        TimeDisplay = $"{current:hh\\:mm\\:ss} / {total:hh\\:mm\\:ss}";
    }

    private static bool IsVideoFile(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext is ".mkv" or ".avi" or ".mp4" or ".m4v" or ".wmv" or ".mov"
            or ".mpg" or ".mpeg" or ".ts" or ".vob" or ".flv" or ".webm";
    }

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F0} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        _mediaPlayer?.Stop();
        _mediaPlayer?.Dispose();
        _libVLC?.Dispose();
        _streamServer?.Dispose();

        // Clean up temp files
        var tempDir = Path.Combine(Path.GetTempPath(), "GlDrive", "player");
        try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }

        GC.SuppressFinalize(this);
    }
}

public class MediaCardVm
{
    public string Title { get; set; } = "";
    public string Year { get; set; } = "";
    public string? PosterUrl { get; set; }
    public string Plot { get; set; } = "";
    public string Genres { get; set; } = "";
    public double VoteAverage { get; set; }
    public string SearchTitle { get; set; } = "";
    public string MediaType { get; set; } = "";

    public string RatingDisplay => VoteAverage > 0 ? $"\u2605 {VoteAverage:F1}" : "";
    public string TitleWithYear => !string.IsNullOrEmpty(Year) ? $"{Title} ({Year})" : Title;
}

public class PlayerSearchResultVm
{
    public string ReleaseName { get; set; } = "";
    public string Category { get; set; } = "";
    public string RemotePath { get; set; } = "";
    public long Size { get; set; }
    public string SizeText { get; set; } = "";
    public string ServerId { get; set; } = "";
    public string ServerName { get; set; } = "";
}
