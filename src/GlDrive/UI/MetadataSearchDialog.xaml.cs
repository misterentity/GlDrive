using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using GlDrive.Downloads;
using Serilog;

namespace GlDrive.UI;

public partial class MetadataSearchDialog : Window
{
    private readonly MediaType _mediaType;
    private readonly TvMazeClient? _tvMaze;
    private readonly OmdbClient? _omdb;
    private readonly List<MetadataResult> _results = new();

    public WishlistItem? SelectedItem { get; private set; }

    public MetadataSearchDialog(MediaType mediaType, string omdbApiKey)
    {
        InitializeComponent();
        _mediaType = mediaType;
        Title = mediaType == MediaType.Movie ? "Search Movies (OMDB)" : "Search TV Shows (TVMaze)";

        if (mediaType == MediaType.TvShow)
            _tvMaze = new TvMazeClient();
        else
            _omdb = new OmdbClient(omdbApiKey);
    }

    private async void Search_Click(object sender, RoutedEventArgs e) => await DoSearch();
    private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return) await DoSearch();
    }

    private async Task DoSearch()
    {
        var query = SearchBox.Text.Trim();
        if (string.IsNullOrEmpty(query)) return;

        _results.Clear();
        ResultsList.Items.Clear();
        DetailPanel.Visibility = Visibility.Collapsed;

        try
        {
            if (_mediaType == MediaType.TvShow && _tvMaze != null)
            {
                var shows = await _tvMaze.Search(query);
                foreach (var show in shows)
                {
                    var summary = StripHtml(show.Summary);
                    var rating = show.Rating?.Average?.ToString("F1");
                    var genres = show.Genres != null ? string.Join(", ", show.Genres) : null;

                    _results.Add(new MetadataResult
                    {
                        Title = show.Name,
                        Year = show.PremieredYear,
                        TvMazeId = show.Id,
                        Type = MediaType.TvShow,
                        PosterUrl = show.Image?.Medium,
                        Plot = summary,
                        Rating = rating,
                        Genres = genres,
                        YearDisplay = $"{show.PremieredYear?.ToString() ?? "?"} \u2022 {show.Status ?? "Unknown"}",
                        RatingDisplay = rating != null ? $"\u2605 {rating}" : ""
                    });
                }
            }
            else if (_mediaType == MediaType.Movie && _omdb != null)
            {
                if (!_omdb.HasApiKey)
                {
                    MessageBox.Show("OMDB API key not configured. Set it in Settings > Downloads.", "No API Key",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var movies = await _omdb.Search(query);
                foreach (var movie in movies)
                {
                    var posterUrl = movie.Poster is "N/A" or null ? null : movie.Poster;

                    _results.Add(new MetadataResult
                    {
                        Title = movie.Title,
                        Year = movie.YearParsed,
                        ImdbId = movie.ImdbID,
                        Type = MediaType.Movie,
                        PosterUrl = posterUrl,
                        Plot = movie.Plot is "N/A" or null ? null : movie.Plot,
                        Rating = movie.imdbRating is "N/A" or null ? null : movie.imdbRating,
                        Genres = movie.Genre is "N/A" or null ? null : movie.Genre,
                        YearDisplay = movie.Year,
                        RatingDisplay = movie.imdbRating is not "N/A" and not null ? $"\u2605 {movie.imdbRating}" : ""
                    });
                }
            }

            foreach (var r in _results)
                ResultsList.Items.Add(r);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Metadata search failed");
            MessageBox.Show($"Search failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        OkButton.IsEnabled = ResultsList.SelectedItem != null;

        if (ResultsList.SelectedItem is not MetadataResult selected)
        {
            DetailPanel.Visibility = Visibility.Collapsed;
            return;
        }

        // For movies: fetch full details if we don't have plot yet
        if (selected.Type == MediaType.Movie && _omdb != null
            && string.IsNullOrEmpty(selected.Plot) && !string.IsNullOrEmpty(selected.ImdbId))
        {
            try
            {
                var full = await _omdb.GetById(selected.ImdbId);
                if (full != null)
                {
                    selected.Plot = full.Plot is "N/A" or null ? null : full.Plot;
                    selected.Rating = full.imdbRating is "N/A" or null ? selected.Rating : full.imdbRating;
                    selected.Genres = full.Genre is "N/A" or null ? selected.Genres : full.Genre;
                    selected.RatingDisplay = selected.Rating != null ? $"\u2605 {selected.Rating}" : "";
                    if (selected.PosterUrl == null && full.Poster is not "N/A" and not null)
                        selected.PosterUrl = full.Poster;
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Failed to fetch movie details for {ImdbId}", selected.ImdbId);
            }
        }

        UpdateDetailPanel(selected);
    }

    private void UpdateDetailPanel(MetadataResult result)
    {
        DetailPanel.Visibility = Visibility.Visible;
        DetailTitle.Text = $"{result.Title} ({result.Year?.ToString() ?? "?"})";
        DetailRating.Text = result.Rating != null ? $"\u2605 {result.Rating}/10" : "";
        DetailGenres.Text = result.Genres ?? "";
        DetailPlot.Text = result.Plot ?? "";

        if (!string.IsNullOrEmpty(result.PosterUrl))
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(result.PosterUrl, UriKind.Absolute);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 150;
                bitmap.EndInit();
                PosterImage.Source = bitmap;
                PosterImage.Visibility = Visibility.Visible;
            }
            catch
            {
                PosterImage.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            PosterImage.Visibility = Visibility.Collapsed;
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (ResultsList.SelectedItem is not MetadataResult selected) return;

        var qualityText = (QualityCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Any";
        var quality = qualityText switch
        {
            "SD" => QualityProfile.SD,
            "720p" => QualityProfile.Q720p,
            "1080p" => QualityProfile.Q1080p,
            "2160p" => QualityProfile.Q2160p,
            _ => QualityProfile.Any
        };

        SelectedItem = new WishlistItem
        {
            Type = selected.Type,
            Title = selected.Title,
            Year = selected.Year,
            ImdbId = selected.ImdbId,
            TvMazeId = selected.TvMazeId,
            Quality = quality,
            PosterUrl = selected.PosterUrl,
            Plot = selected.Plot,
            Rating = selected.Rating,
            Genres = selected.Genres
        };

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _tvMaze?.Dispose();
        _omdb?.Dispose();
    }

    private static string? StripHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        var text = Regex.Replace(html, "<[^>]+>", "").Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }
}

internal class MetadataResult
{
    public string Title { get; set; } = "";
    public int? Year { get; set; }
    public string? ImdbId { get; set; }
    public int? TvMazeId { get; set; }
    public MediaType Type { get; set; }
    public string? PosterUrl { get; set; }
    public string? Plot { get; set; }
    public string? Rating { get; set; }
    public string? Genres { get; set; }

    // Display helpers for the list
    public string YearDisplay { get; set; } = "";
    public string RatingDisplay { get; set; } = "";

    public override string ToString() => Title;
}
