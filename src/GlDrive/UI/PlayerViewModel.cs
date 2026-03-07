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
    private PlayerResumeStore? _resumeStore;
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
    private string _searchText = "";
    private string _currentReleaseName = "";
    private bool _showLibrary;
    private int _audioTrackIndex;
    private int _subtitleTrackIndex = -1;

    // TV episode state
    private int _selectedTvId;
    private string _selectedTvName = "";
    private TmdbSeasonSummary? _selectedSeason;
    private TmdbEpisode? _selectedEpisode;

    // Auto-play next episode
    private List<TmdbEpisode>? _episodePlaylist;
    private int _playlistIndex = -1;

    public ObservableCollection<MediaCardVm> TrendingMovies { get; } = new();
    public ObservableCollection<MediaCardVm> TrendingTvShows { get; } = new();
    public ObservableCollection<PlayerSearchResultVm> FtpResults { get; } = new();
    public ObservableCollection<LibraryItemVm> LibraryItems { get; } = new();
    public ObservableCollection<MediaCardVm> SearchResults { get; } = new();
    public ObservableCollection<TmdbSeasonSummary> Seasons { get; } = new();
    public ObservableCollection<TmdbEpisode> Episodes { get; } = new();
    public ObservableCollection<TrackInfo> AudioTracks { get; } = new();
    public ObservableCollection<TrackInfo> SubtitleTracks { get; } = new();

    public MediaCardVm? SelectedMovie
    {
        get => _selectedMovie;
        set
        {
            _selectedMovie = value;
            if (value != null) { _selectedTvShow = null; ClearEpisodePicker(); SwitchToNowPlaying?.Invoke(); }
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedTvShow));
            NotifyDetailChanged();
        }
    }

    public MediaCardVm? SelectedTvShow
    {
        get => _selectedTvShow;
        set
        {
            _selectedTvShow = value;
            if (value != null)
            {
                _selectedMovie = null;
                if (value.TmdbId > 0) _ = LoadTvSeasons(value.TmdbId, value.Title);
                SwitchToNowPlaying?.Invoke();
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedMovie));
            NotifyDetailChanged();
        }
    }

    private void NotifyDetailChanged()
    {
        OnPropertyChanged(nameof(SelectedMedia));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(DetailTitle));
        OnPropertyChanged(nameof(DetailYear));
        OnPropertyChanged(nameof(DetailRating));
        OnPropertyChanged(nameof(DetailGenres));
        OnPropertyChanged(nameof(DetailPlot));
        OnPropertyChanged(nameof(DetailPosterUrl));
        OnPropertyChanged(nameof(HasDetailPoster));
        OnPropertyChanged(nameof(ShowEpisodePicker));
        OnPropertyChanged(nameof(HasNoSelection));
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
    public bool ShowEpisodePicker => _selectedTvShow != null && Seasons.Count > 0;

    public TmdbSeasonSummary? SelectedSeason
    {
        get => _selectedSeason;
        set
        {
            _selectedSeason = value;
            OnPropertyChanged();
            if (value != null) _ = LoadEpisodes(_selectedTvId, value.SeasonNumber);
        }
    }

    public TmdbEpisode? SelectedEpisode
    {
        get => _selectedEpisode;
        set { _selectedEpisode = value; OnPropertyChanged(); }
    }

    public PlayerSearchResultVm? SelectedFtpResult
    {
        get => _selectedFtpResult;
        set { _selectedFtpResult = value; OnPropertyChanged(); }
    }

    public string SearchText
    {
        get => _searchText;
        set { _searchText = value; OnPropertyChanged(); }
    }

    public bool ShowLibrary
    {
        get => _showLibrary;
        set { _showLibrary = value; OnPropertyChanged(); if (value) LoadLibrary(); }
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

    public int AudioTrackIndex
    {
        get => _audioTrackIndex;
        set
        {
            _audioTrackIndex = value;
            OnPropertyChanged();
            if (_mediaPlayer != null && value >= 0 && value < AudioTracks.Count)
                _mediaPlayer.SetAudioTrack(AudioTracks[value].Id);
        }
    }

    public int SubtitleTrackIndex
    {
        get => _subtitleTrackIndex;
        set
        {
            _subtitleTrackIndex = value;
            OnPropertyChanged();
            if (_mediaPlayer != null)
            {
                if (value < 0 || value >= SubtitleTracks.Count)
                    _mediaPlayer.SetSpu(-1); // disable
                else
                    _mediaPlayer.SetSpu(SubtitleTracks[value].Id);
            }
        }
    }

    public MediaPlayer? Player => _mediaPlayer;

    public ICommand SearchAndPlayCommand { get; }
    public ICommand PlayResultCommand { get; }
    public ICommand PlayPauseCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand RefreshTrendingCommand { get; }
    public ICommand SearchCommand { get; }
    public ICommand ToggleLibraryCommand { get; }
    public ICommand PlayLibraryItemCommand { get; }
    public ICommand PlayEpisodeCommand { get; }
    public ICommand SeekForwardCommand { get; }
    public ICommand SeekBackwardCommand { get; }
    public ICommand ClearSearchCommand { get; }

    public bool HasSearchResults => SearchResults.Count > 0;
    public bool HasFtpResults => FtpResults.Count > 0;
    public bool HasNoSelection => !HasSelection;

    public PlayerViewModel(ServerManager serverManager, AppConfig config)
    {
        _serverManager = serverManager;
        _config = config;

        SearchAndPlayCommand = new RelayCommand(async () => await SearchAndPlay());
        PlayResultCommand = new RelayCommand(async () => await PlaySelectedResult());
        PlayPauseCommand = new RelayCommand(TogglePlayPause);
        StopCommand = new RelayCommand(StopPlayback);
        RefreshTrendingCommand = new RelayCommand(async () => await LoadTrending());
        SearchCommand = new RelayCommand(async () => await SearchTmdb());
        ToggleLibraryCommand = new RelayCommand(() => ShowLibrary = !ShowLibrary);
        PlayLibraryItemCommand = new RelayCommand<LibraryItemVm>(async item => { if (item != null) await PlayLocalFile(item.FilePath); });
        PlayEpisodeCommand = new RelayCommand<TmdbEpisode>(async ep => { if (ep != null) await PlayEpisode(ep); });
        SeekForwardCommand = new RelayCommand(() => SeekRelative(10));
        SeekBackwardCommand = new RelayCommand(() => SeekRelative(-10));
        ClearSearchCommand = new RelayCommand(() => { SearchResults.Clear(); OnPropertyChanged(nameof(HasSearchResults)); });
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
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    _position = e.Position * 100;
                    OnPropertyChanged(nameof(Position));
                    UpdateTimeDisplay();
                });
            };
            _mediaPlayer.Buffering += (_, e) =>
            {
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    BufferProgress = e.Cache;
                    IsBuffering = e.Cache < 100;
                    if (e.Cache < 100)
                        PlayerStatus = $"Buffering... {e.Cache:F0}%";
                });
            };
            _mediaPlayer.Opening += (_, _) =>
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    IsBuffering = true;
                    PlayerStatus = "Connecting to stream...";
                });
            _mediaPlayer.Playing += (_, _) =>
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    IsPlaying = true;
                    IsBuffering = false;
                    UpdateTrackLists();
                    // Resume from saved position
                    if (_resumeStore != null && !string.IsNullOrEmpty(_currentReleaseName))
                    {
                        var savedPos = _resumeStore.GetPosition(_currentReleaseName);
                        if (savedPos > 2)
                        {
                            _mediaPlayer!.Position = (float)(savedPos / 100.0);
                            PlayerStatus = $"Resumed from {savedPos:F0}%";
                        }
                    }
                });
            _mediaPlayer.Stopped += (_, _) =>
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    SaveCurrentPosition();
                    IsPlaying = false;
                    IsBuffering = false;
                    PlayerStatus = "Stopped";
                    AudioTracks.Clear();
                    SubtitleTracks.Clear();
                });
            _mediaPlayer.EndReached += (_, _) =>
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (!string.IsNullOrEmpty(_currentReleaseName))
                        _resumeStore?.ClearPosition(_currentReleaseName);
                    IsPlaying = false;
                    IsBuffering = false;
                    PlayerStatus = "Finished";
                    AudioTracks.Clear();
                    SubtitleTracks.Clear();
                    // Auto-play next episode
                    _ = PlayNextEpisode();
                });
            _mediaPlayer.EncounteredError += (_, _) =>
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    IsPlaying = false;
                    IsBuffering = false;
                    PlayerStatus = "Playback error";
                });
            _mediaPlayer.Volume = (int)_volume;

            _streamServer = new MediaStreamServer(_serverManager, _config);
            _streamServer.Start();
            _resumeStore = new PlayerResumeStore(_streamServer.LibraryPath);

            _vlcInitialized = true;
            OnPropertyChanged(nameof(Player));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize LibVLC");
            PlayerStatus = "Failed to initialize video player";
        }
    }

    private void UpdateTrackLists()
    {
        if (_mediaPlayer == null) return;

        AudioTracks.Clear();
        var audioDesc = _mediaPlayer.AudioTrackDescription;
        foreach (var t in audioDesc)
        {
            if (t.Id == -1) continue; // skip "Disable"
            AudioTracks.Add(new TrackInfo { Id = t.Id, Name = t.Name ?? $"Track {t.Id}" });
        }
        if (AudioTracks.Count > 0) AudioTrackIndex = 0;

        SubtitleTracks.Clear();
        var spuDesc = _mediaPlayer.SpuDescription;
        foreach (var t in spuDesc)
        {
            if (t.Id == -1) continue;
            SubtitleTracks.Add(new TrackInfo { Id = t.Id, Name = t.Name ?? $"Sub {t.Id}" });
        }
        OnPropertyChanged(nameof(AudioTracks));
        OnPropertyChanged(nameof(SubtitleTracks));
    }

    private void SaveCurrentPosition()
    {
        if (_mediaPlayer != null && !string.IsNullOrEmpty(_currentReleaseName))
        {
            var pos = _mediaPlayer.Position * 100;
            _resumeStore?.SavePosition(_currentReleaseName, pos);
        }
    }

    // ── Search TMDB ──
    private async Task SearchTmdb()
    {
        if (string.IsNullOrWhiteSpace(_searchText)) return;
        if (string.IsNullOrEmpty(_config.Downloads.TmdbApiKey))
        {
            // No TMDB key — search FTP directly
            await SearchFtpDirect(_searchText);
            return;
        }

        IsLoading = true;
        SearchResults.Clear();
        PlayerStatus = $"Searching TMDb for \"{_searchText}\"...";

        try
        {
            using var tmdb = new TmdbClient(_config.Downloads.TmdbApiKey);
            var results = await tmdb.SearchMulti(_searchText);

            foreach (var r in results.Take(20))
            {
                SearchResults.Add(new MediaCardVm
                {
                    Title = r.DisplayTitle,
                    Year = r.YearParsed?.ToString() ?? "",
                    PosterUrl = r.PosterUrl,
                    Plot = r.Overview ?? "",
                    VoteAverage = r.VoteAverage,
                    SearchTitle = r.DisplayTitle,
                    MediaType = r.MediaType == "tv" ? "TV" : "Movie",
                    TmdbId = r.Id
                });
            }

            OnPropertyChanged(nameof(HasSearchResults));
            PlayerStatus = SearchResults.Count > 0
                ? $"{SearchResults.Count} result(s) from TMDb"
                : $"No results for \"{_searchText}\"";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "TMDb search failed");
            PlayerStatus = "Search failed";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task SearchFtpDirect(string query)
    {
        var mounted = _serverManager.GetMountedServers()
            .Where(s => s.Search != null && s.CurrentState == MountState.Connected)
            .ToList();

        if (mounted.Count == 0) { PlayerStatus = "No connected servers"; return; }

        IsLoading = true;
        FtpResults.Clear();
        OnPropertyChanged(nameof(HasFtpResults));
        PlayerStatus = $"Searching FTP for \"{query}\"...";

        try
        {
            var tasks = mounted.Select(server => Task.Run(async () =>
            {
                var results = await server.Search!.Search(query);
                return results.Select(r => new PlayerSearchResultVm
                {
                    ReleaseName = r.ReleaseName, Category = r.Category,
                    RemotePath = r.RemotePath, Size = r.Size,
                    SizeText = FormatSize(r.Size), ServerId = server.ServerId,
                    ServerName = server.ServerName
                });
            }));

            var allResults = await Task.WhenAll(tasks);
            foreach (var sr in allResults)
                foreach (var r in sr) FtpResults.Add(r);

            OnPropertyChanged(nameof(HasFtpResults));
            PlayerStatus = FtpResults.Count > 0
                ? $"{FtpResults.Count} result(s) found"
                : $"No results for \"{query}\"";

            if (FtpResults.Count > 0) SelectedFtpResult = FtpResults[0];
        }
        catch (Exception ex) { Log.Warning(ex, "FTP search failed"); PlayerStatus = "Search failed"; }
        finally { IsLoading = false; }
    }

    // ── TV episode picker ──
    private async Task LoadTvSeasons(int tvId, string tvName)
    {
        if (string.IsNullOrEmpty(_config.Downloads.TmdbApiKey)) return;

        _selectedTvId = tvId;
        _selectedTvName = tvName;
        Seasons.Clear();
        Episodes.Clear();

        try
        {
            using var tmdb = new TmdbClient(_config.Downloads.TmdbApiKey);
            var detail = await tmdb.GetTvDetail(tvId);
            if (detail?.Seasons == null) return;

            foreach (var s in detail.Seasons.Where(s => s.SeasonNumber > 0))
                Seasons.Add(s);

            OnPropertyChanged(nameof(ShowEpisodePicker));

            if (Seasons.Count > 0)
                SelectedSeason = Seasons[^1]; // select latest season
        }
        catch (Exception ex) { Log.Warning(ex, "Failed to load TV seasons"); }
    }

    private async Task LoadEpisodes(int tvId, int seasonNumber)
    {
        if (string.IsNullOrEmpty(_config.Downloads.TmdbApiKey)) return;

        Episodes.Clear();
        try
        {
            using var tmdb = new TmdbClient(_config.Downloads.TmdbApiKey);
            var season = await tmdb.GetTvSeason(tvId, seasonNumber);
            if (season?.Episodes == null) return;

            _episodePlaylist = season.Episodes.ToList();
            foreach (var ep in season.Episodes)
                Episodes.Add(ep);
        }
        catch (Exception ex) { Log.Warning(ex, "Failed to load episodes"); }
    }

    private void ClearEpisodePicker()
    {
        Seasons.Clear();
        Episodes.Clear();
        _selectedSeason = null;
        _selectedEpisode = null;
        _episodePlaylist = null;
        _playlistIndex = -1;
        OnPropertyChanged(nameof(ShowEpisodePicker));
    }

    private async Task PlayEpisode(TmdbEpisode episode)
    {
        if (_selectedSeason == null) return;
        _selectedEpisode = episode;
        _playlistIndex = _episodePlaylist?.IndexOf(episode) ?? -1;

        var query = episode.SearchQuery(_selectedTvName, _selectedSeason.SeasonNumber);
        SearchText = query;
        await SearchFtpDirect(query);

        // Auto-play first result
        if (FtpResults.Count > 0)
        {
            SelectedFtpResult = FtpResults[0];
            await PlaySelectedResult();
        }
    }

    private async Task PlayNextEpisode()
    {
        if (_episodePlaylist == null || _playlistIndex < 0) return;
        var nextIdx = _playlistIndex + 1;
        if (nextIdx >= _episodePlaylist.Count) return;

        PlayerStatus = "Auto-playing next episode...";
        await Task.Delay(2000); // brief pause between episodes
        await PlayEpisode(_episodePlaylist[nextIdx]);
    }

    // ── Library browser ──
    public void LoadLibrary()
    {
        LibraryItems.Clear();
        if (_streamServer == null) return;

        var libPath = _streamServer.LibraryPath;
        if (!Directory.Exists(libPath)) return;

        foreach (var dir in Directory.GetDirectories(libPath).OrderByDescending(d => Directory.GetLastWriteTime(d)))
        {
            var videos = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                .Where(f => IsVideoFile(f) || f.EndsWith(".rar", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (videos.Count == 0) continue;

            var largest = videos.OrderByDescending(f => new FileInfo(f).Length).First();
            var fi = new FileInfo(largest);
            var releaseName = Path.GetFileName(dir);
            var resumePos = _resumeStore?.GetPosition(releaseName) ?? 0;

            LibraryItems.Add(new LibraryItemVm
            {
                ReleaseName = releaseName,
                FilePath = largest,
                FileName = fi.Name,
                SizeText = FormatSize(fi.Length),
                ResumePercent = resumePos > 2 ? $"{resumePos:F0}%" : ""
            });
        }
    }

    // ── Trending ──
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
                    Title = m.Title, Year = m.YearParsed?.ToString() ?? "",
                    PosterUrl = m.PosterUrl, Plot = m.Overview ?? "",
                    Genres = m.GenreText, VoteAverage = m.VoteAverage,
                    SearchTitle = m.Title, MediaType = "Movie", TmdbId = m.Id
                });
            }

            var shows = await tmdb.GetTrendingTvShows();
            TrendingTvShows.Clear();
            foreach (var s in shows.Take(20))
            {
                TrendingTvShows.Add(new MediaCardVm
                {
                    Title = s.Name, Year = s.YearParsed?.ToString() ?? "",
                    PosterUrl = s.PosterUrl, Plot = s.Overview ?? "",
                    Genres = s.GenreText, VoteAverage = s.VoteAverage,
                    SearchTitle = s.Name, MediaType = "TV", TmdbId = s.Id
                });
            }

            PlayerStatus = $"{TrendingMovies.Count} movies, {TrendingTvShows.Count} TV shows";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load trending");
            PlayerStatus = "Failed to load trending content";
        }
        finally { IsLoading = false; }
    }

    // ── Search FTP for selected media ──
    private async Task SearchAndPlay()
    {
        var media = SelectedMedia;
        if (media == null) return;
        await SearchFtpDirect(media.SearchTitle);
    }

    private async Task PlaySelectedResult()
    {
        if (_selectedFtpResult == null || _streamServer == null || _mediaPlayer == null) return;

        var result = _selectedFtpResult;
        var server = _serverManager.GetServer(result.ServerId);
        if (server?.Pool == null) return;

        IsLoading = true;
        _currentReleaseName = result.ReleaseName;
        PlayerStatus = $"Loading {result.ReleaseName}...";

        try
        {
            // Check library cache first (fast, local I/O)
            var cached = _streamServer.FindCachedVideo(result.ReleaseName);
            if (cached != null)
            {
                IsLoading = false;
                PlayerStatus = $"Playing from library: {result.ReleaseName}";
                await PlayLocalFile(cached);
                return;
            }

            // Run FTP file listing on background thread to avoid blocking UI
            var files = await Task.Run(() => server.Search!.GetReleaseFiles(result.RemotePath));

            var videoFile = files
                .Where(f => IsVideoFile(f.Name))
                .OrderByDescending(f => f.Size)
                .FirstOrDefault();

            // Stop loading spinner — VLC buffering overlay takes over from here
            IsLoading = false;

            if (videoFile != null)
            {
                await PlayFromFtp(result.ServerId, videoFile.FullName, result.ReleaseName);
                return;
            }

            var rarFile = files
                .Where(f => f.Name.EndsWith(".rar", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.Name)
                .FirstOrDefault();

            if (rarFile != null)
            {
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
        finally { IsLoading = false; }
    }

    private async Task PlayFromFtp(string serverId, string remotePath, string releaseName = "")
    {
        var url = $"{_streamServer!.BaseUrl}stream?token={_streamServer.AuthToken}&server={Uri.EscapeDataString(serverId)}&path={Uri.EscapeDataString(remotePath)}&release={Uri.EscapeDataString(releaseName)}";
        Log.Information("Playing FTP stream: {Url}", url);

        SaveCurrentPosition();
        _currentReleaseName = releaseName;

        var media = new Media(_libVLC!, new Uri(url));
        media.AddOption(":network-caching=10000");
        media.AddOption(":file-caching=5000");
        media.AddOption(":http-reconnect");

        IsBuffering = true;
        PlayerStatus = "Connecting to stream...";

        await Task.Run(() => _mediaPlayer!.Stop());
        _mediaPlayer!.Play(media);

        PlayerStatus = $"Buffering: {Path.GetFileName(remotePath)}...";
    }

    private async Task PlayFromRar(MountService server, string releasePath, List<FtpListItem> files)
    {
        var releaseName = Path.GetFileName(releasePath);
        Log.Information("RAR background download+extract for {Path}", releasePath);

        SaveCurrentPosition();
        _currentReleaseName = releaseName;
        IsBuffering = true;
        BufferProgress = 0;
        PlayerStatus = "Preparing RAR download...";

        try
        {
            // Download all volumes, extract, and start VLC as soon as 10MB is extracted
            var playStarted = false;
            var localVideo = await Task.Run(async () =>
                await _streamServer!.DownloadAndExtractRar(server, releasePath, files,
                    onProgress: (msg, pct) =>
                    {
                        Application.Current?.Dispatcher.BeginInvoke(() =>
                        {
                            PlayerStatus = msg;
                            BufferProgress = pct;
                        });
                    },
                    onPlayReady: videoPath =>
                    {
                        Application.Current?.Dispatcher.BeginInvoke(async () =>
                        {
                            if (playStarted) return;
                            playStarted = true;
                            IsBuffering = false;
                            await PlayLocalFile(videoPath);
                        });
                    })
            );

            if (localVideo == null && !playStarted)
            {
                PlayerStatus = "No video found in RAR archive";
                IsBuffering = false;
                return;
            }

            if (playStarted)
                PlayerStatus = "Extraction complete";
            else
            {
                IsBuffering = false;
                await PlayLocalFile(localVideo!);
            }
        }
        catch (OperationCanceledException)
        {
            PlayerStatus = "Download cancelled";
            IsBuffering = false;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "RAR download failed for {Path}", releasePath);
            var msg = ex.Message;
            if (msg.Contains("actively refused"))
                msg = "Server refused connection — try again in a few minutes";
            else if (msg.Contains("No FTP connection available"))
                msg = "All FTP connections busy — pause other downloads and retry";
            PlayerStatus = msg;
            IsBuffering = false;
        }
    }

    private async Task PlayLocalFile(string localPath)
    {
        Log.Information("Playing from library: {Path}", localPath);

        SaveCurrentPosition();
        // Derive release name from parent directory
        _currentReleaseName = Path.GetFileName(Path.GetDirectoryName(localPath) ?? "");

        var media = new Media(_libVLC!, new Uri($"file:///{localPath.Replace('\\', '/')}"));

        await Task.Run(() => _mediaPlayer!.Stop());
        _mediaPlayer!.Play(media);

        PlayerStatus = $"Playing: {Path.GetFileName(localPath)} (from library)";
    }

    // ── Playback controls ──
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
        SaveCurrentPosition();
        Task.Run(() => _mediaPlayer.Stop());
    }

    public void SeekTo(double percent)
    {
        if (_mediaPlayer == null || !_mediaPlayer.IsPlaying) return;
        _mediaPlayer.Position = (float)(percent / 100.0);
    }

    public void SeekRelative(int seconds)
    {
        if (_mediaPlayer == null || !_mediaPlayer.IsPlaying) return;
        var lengthMs = _mediaPlayer.Length;
        if (lengthMs <= 0) return;
        var newTime = _mediaPlayer.Time + (seconds * 1000L);
        _mediaPlayer.Time = Math.Clamp(newTime, 0, lengthMs);
    }

    public void ToggleFullscreen()
    {
        FullscreenRequested?.Invoke();
    }

    public event Action? FullscreenRequested;
    public event Action? SwitchToNowPlaying;

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
        SaveCurrentPosition();
        _mediaPlayer?.Stop();
        _mediaPlayer?.Dispose();
        _libVLC?.Dispose();
        _streamServer?.Dispose();
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
    public int TmdbId { get; set; }

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

public class LibraryItemVm
{
    public string ReleaseName { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public string SizeText { get; set; } = "";
    public string ResumePercent { get; set; } = "";
}

public class TrackInfo
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public override string ToString() => Name;
}
