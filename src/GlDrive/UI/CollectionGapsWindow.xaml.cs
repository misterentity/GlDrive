using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using GlDrive.Downloads;

namespace GlDrive.UI;

public partial class CollectionGapsWindow : Window
{
    private readonly CollectionGapService _service;
    private CancellationTokenSource? _operation;
    private int? _displayedMovieId;
    private bool _closed;

    public CollectionGapsWindow(CollectionGapService service, int? movieTmdbId = null)
    {
        InitializeComponent();
        _service = service;
        ShowLibraryStatus();
        if (movieTmdbId is > 0) MovieIdBox.Text = movieTmdbId.Value.ToString(CultureInfo.InvariantCulture);
        Closed += (_, _) => { _closed = true; _operation?.Cancel(); FilmsGrid.ItemsSource = null; };
    }

    private async void RefreshLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (_operation != null) return;
        using var cancellation = new CancellationTokenSource();
        _operation = cancellation; SetBusy(true);
        StatusLabel.Text = "Reading configured library folders and Plex…";
        try
        {
            await Task.Run(() => _service.RefreshLibraryAsync(cancellation.Token), cancellation.Token);
            if (_closed) return;
            if (_displayedMovieId != null) ShowSnapshot(_service.Snapshot);
            else StatusLabel.Text = "Library index refreshed. Choose Refresh collection to compare a movie.";
            ShowLibraryStatus();
        }
        catch (OperationCanceledException) { if (!_closed) StatusLabel.Text = "Library refresh cancelled. No films were added."; }
        catch { if (!_closed) StatusLabel.Text = "Library refresh is unavailable. Unknown films cannot be added."; }
        finally { _operation = null; if (!_closed) SetBusy(false); }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_operation != null) return;
        if (!int.TryParse(MovieIdBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var movieId) || movieId <= 0)
        { StatusLabel.Text = "Enter a positive movie TMDB ID."; return; }
        using var cancellation = new CancellationTokenSource();
        _operation = cancellation;
        SetBusy(true); FilmsGrid.ItemsSource = null; _displayedMovieId = null;
        CollectionLabel.Text = ""; StatusLabel.Text = "Checking collection…";
        try
        {
            var result = await Task.Run(() => _service.RefreshAsync(movieId, cancellation.Token), cancellation.Token);
            if (_closed) return;
            ShowSnapshot(result);
        }
        catch (OperationCanceledException) { if (!_closed) StatusLabel.Text = "Refresh cancelled. No films were added."; }
        catch { if (!_closed) StatusLabel.Text = "Collection information is unavailable. No films were added."; }
        finally { _operation = null; if (!_closed) SetBusy(false); }
    }

    private async void AddFilm_Click(object sender, RoutedEventArgs e)
    {
        if (_operation != null || _displayedMovieId is not { } movieId || sender is not Button { DataContext: CollectionFilmRow row } || !row.CanAdd) return;
        using var cancellation = new CancellationTokenSource();
        _operation = cancellation; SetBusy(true);
        try
        {
            var result = await Task.Run(() =>
            {
                cancellation.Token.ThrowIfCancellationRequested();
                return _service.AddMissing(movieId, row.TmdbId);
            }, cancellation.Token);
            if (_closed) return;
            var snapshot = _service.Snapshot;
            if (snapshot.MovieTmdbId == movieId) ShowSnapshot(snapshot);
            StatusLabel.Text = result.Status switch
            {
                "added" => "Added to the wishlist using the current default quality and normal source permissions.",
                "already-wanted" => "This film already has a wishlist item; its settings were preserved.",
                "owned" => "A qualifying copy is already owned. Nothing added.",
                "downloading" => "This film already has a queued or active download. Nothing added.",
                "ownership-unknown" => "Refresh the library index before adding a film whose ownership is unknown.",
                "year-required" => "This film has no known release year and cannot be added safely.",
                _ => "Collection data changed or is unavailable. Refresh before adding this film."
            };
        }
        catch (OperationCanceledException) { if (!_closed) StatusLabel.Text = "Add cancelled before it began."; }
        catch { if (!_closed) StatusLabel.Text = "The wishlist could not be updated. Check its current contents before retrying."; }
        finally { _operation = null; if (!_closed) SetBusy(false); }
    }

    private void ShowSnapshot(CollectionGapSnapshot snapshot)
    {
        _displayedMovieId = snapshot.Status is "available" or "partial" ? snapshot.MovieTmdbId : null;
        CollectionLabel.Text = snapshot.CollectionName ?? "";
        FilmsGrid.ItemsSource = snapshot.Films.Select(film => new CollectionFilmRow(film)).ToArray();
        ShowLibraryStatus();
        StatusLabel.Text = snapshot.Status switch
        {
            "available" => "Owned copies, current downloads and wishlist entries are compared locally. Only missing films with a known release year can be added. Refresh the library index to resolve unknown ownership.",
            "partial" => "Only part of this collection could be read. Only missing films with a known release year can be added; this is not a complete collection inventory.",
            "no-collection" => "TMDB reports no collection for this film.",
            "busy" => "Another collection refresh is already running. Try again when it finishes.",
            "not-refreshed" => "Choose Refresh collection to look up this movie.",
            _ => "Collection information is unknown or unavailable. No additions are offered from this response."
        };
    }
    private void ShowLibraryStatus()
    {
        var status = _service.LibraryStatus;
        if (status.RefreshedAtUtc is not { } refreshed)
        {
            LibraryLabel.Text = "Library index: not refreshed. Unknown ownership cannot be added.";
            return;
        }
        var age = Math.Max(0, (DateTime.UtcNow - refreshed.ToUniversalTime()).TotalMinutes);
        var ageText = age < 1 ? "just now" : $"{(int)age} min ago";
        LibraryLabel.Text = $"Library index: {(status.Complete ? "complete" : "incomplete")}, {ageText} ({refreshed.ToLocalTime():g}). " +
            $"{status.ScannedRoots} folders scanned; {status.ScanIssues} scan issues.";
    }
    private void SetBusy(bool busy)
    {
        RefreshButton.IsEnabled = !busy; CancelButton.IsEnabled = busy;
        RefreshLibraryButton.IsEnabled = !busy;
        MovieIdBox.IsEnabled = !busy; FilmsGrid.IsEnabled = !busy;
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => _operation?.Cancel();
}

public sealed record CollectionFilmRow(CollectionGapFilm Film)
{
    public int TmdbId => Film.TmdbId;
    public string Title => Film.Title;
    public int? Year => Film.Year;
    public string DisplayStatus => Film.Status switch
    {
        "owned" => "Owned", "downloading" => "Downloading", "wanted" => "Wanted",
        "missing" => "Missing", _ => "Unknown"
    };
    public bool CanAdd => Film.Status == "missing" && Film.Year is >= 1 and <= 9999;
}
