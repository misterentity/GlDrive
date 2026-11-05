using System.Diagnostics;
using System.Windows;
using System.Windows.Media.Imaging;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Library;
using Serilog;

namespace GlDrive.UI;

/// <summary>What a release from IRC is: TMDB movie/show (and episode) details, the scene
/// name broken down, and whether it is already in Plex or on disk.</summary>
public partial class ReleaseInfoWindow : Window
{
    private readonly string _release;
    private readonly CancellationTokenSource _cts = new();
    private ReleaseInfo? _info;

    public ReleaseInfoWindow(string release, AppConfig config, Func<LibraryIndexSnapshot> library)
    {
        _release = release;
        InitializeComponent();
        Title = $"Release info — {release}";
        ReleaseText.Text = ">> " + release;
        Closed += (_, _) => _cts.Cancel();
        Loaded += async (_, _) => await LoadAsync(config, library);
    }

    private async Task LoadAsync(AppConfig config, Func<LibraryIndexSnapshot> library)
    {
        StatusText.Text = "Looking up on TMDB…";
        try
        {
            var tmdb = MetadataClients.Tmdb;
            _info = await ReleaseInfoLookup.LookupAsync(_release, tmdb, _cts.Token);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            Log.Warning(ex, "Release info lookup failed for {Release}", _release);
            _info = new ReleaseInfo(_release, SceneNameParser.Parse(_release), false, "error");
        }
        Render(_info, library);
    }

    private void Render(ReleaseInfo info, Func<LibraryIndexSnapshot> library)
    {
        var p = info.Parsed;
        StatusText.Text = info.Status switch
        {
            "found" => "From TMDB.",
            "no-key" => "No TMDB API key — add one in Settings to look releases up.",
            "not-found" => "TMDB has no movie or TV match for this name.",
            _ => "The TMDB lookup failed. Try again later."
        };

        TitleText.Text = info.Title != null
            ? info.Year is { } y && !info.IsTv ? $"{info.Title} ({y})" : info.Title
            : string.IsNullOrWhiteSpace(p.Title) ? _release : p.Title;

        var meta = new List<string> { info.IsTv ? "TV" : "Movie" };
        if (info.IsTv && info.Year is { } first) meta.Add($"since {first}");
        if (info.Genres != null) meta.Add(info.Genres);
        if (info.RuntimeMinutes is { } runtime) meta.Add($"{runtime / 60}h {runtime % 60:00}m");
        if (info.Rating is { } rating) meta.Add($"★ {rating:0.0}/10");
        if (info.ShowStatus != null) meta.Add(info.ShowStatus);
        MetaText.Text = string.Join("  ·  ", meta);

        if (info.IsTv && p.Season is { } season)
        {
            var label = p.Episode is { } ep ? $"S{season:00}E{ep:00}" : $"Season {season}";
            var episode = info.EpisodeTitle != null ? $"{label} — {info.EpisodeTitle}" : label;
            if (info.EpisodeAirDate != null) episode += $"  (aired {info.EpisodeAirDate})";
            EpisodeText.Text = episode;
            EpisodeText.Visibility = Visibility.Visible;
        }
        OverviewText.Text = info.EpisodeOverview ?? info.Overview ?? "";

        var release = new List<string>();
        if (p.Quality != QualityProfile.Any) release.Add(ReleaseInfoLookup.QualityLabel(p.Quality));
        if (p.Source != null) release.Add(p.Source);
        if (p.IsSeasonPack) release.Add("season pack");
        if (p.Group != null) release.Add($"group {p.Group}");
        ReleaseDetailText.Text = release.Count > 0 ? string.Join("  ·  ", release) : "Nothing recognised in the name.";

        try { LibraryText.Text = ReleaseInfoLookup.LibraryStatus(library(), info); }
        catch { LibraryText.Text = "Library unavailable."; }

        TmdbButton.Visibility = info.TmdbUrl != null ? Visibility.Visible : Visibility.Collapsed;
        ImdbButton.Visibility = info.ImdbUrl != null ? Visibility.Visible : Visibility.Collapsed;

        if (info.PosterUrl != null && Uri.TryCreate(info.PosterUrl, UriKind.Absolute, out var poster) && poster.Scheme == Uri.UriSchemeHttps)
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = poster;
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();
                PosterImage.Source = image;
            }
            catch (Exception ex) { Log.Debug(ex, "Poster load failed for {Release}", _release); }
        }
    }

    private static void Open(string? url)
    {
        if (url == null) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warning(ex, "Could not open {Url}", url); }
    }

    private void Tmdb_Click(object sender, RoutedEventArgs e) => Open(_info?.TmdbUrl);
    private void Imdb_Click(object sender, RoutedEventArgs e) => Open(_info?.ImdbUrl);
    private void CopyName_Click(object sender, RoutedEventArgs e) => ClipboardHelper.TrySetText(_release);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
