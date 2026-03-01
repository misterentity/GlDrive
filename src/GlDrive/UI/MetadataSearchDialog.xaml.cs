using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

        try
        {
            if (_mediaType == MediaType.TvShow && _tvMaze != null)
            {
                var shows = await _tvMaze.Search(query);
                foreach (var show in shows)
                {
                    _results.Add(new MetadataResult
                    {
                        DisplayText = $"{show.Name} ({show.PremieredYear?.ToString() ?? "?"}) — {show.Status ?? "Unknown"}",
                        Title = show.Name,
                        Year = show.PremieredYear,
                        TvMazeId = show.Id,
                        Type = MediaType.TvShow
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
                    _results.Add(new MetadataResult
                    {
                        DisplayText = $"{movie.Title} ({movie.Year}) — {movie.ImdbID}",
                        Title = movie.Title,
                        Year = movie.YearParsed,
                        ImdbId = movie.ImdbID,
                        Type = MediaType.Movie
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

    private void ResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        OkButton.IsEnabled = ResultsList.SelectedItem != null;
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
            Quality = quality
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
}

internal class MetadataResult
{
    public string DisplayText { get; set; } = "";
    public string Title { get; set; } = "";
    public int? Year { get; set; }
    public string? ImdbId { get; set; }
    public int? TvMazeId { get; set; }
    public MediaType Type { get; set; }

    public override string ToString() => DisplayText;
}
