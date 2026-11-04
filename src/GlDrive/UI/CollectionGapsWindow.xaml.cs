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

    private sealed record MatchRow(int Id, string Label);
    private bool _showingMatches;

    /// <param name="initialQuery">A TMDB id, IMDb id or "Title Year" (from the selected wishlist
    /// movie); when given, the lookup runs as soon as the window opens.</param>
    public CollectionGapsWindow(CollectionGapService service, string? initialQuery = null)
    {
        InitializeComponent();
        _service = service;
        ShowLibraryStatus();
        if (!string.IsNullOrWhiteSpace(initialQuery)) MovieIdBox.Text = initialQuery;
        Closed += (_, _) => { _closed = true; _operation?.Cancel(); FilmsGrid.ItemsSource = null; };
        Loaded += async (_, _) =>
        {
            // The index lives in memory: after a restart it is empty and every film reads
            // "Unknown". Opening the window is the request to read it.
            if (_service.LibraryStatus.RefreshedAtUtc == null) await RefreshLibraryAsync();
            if (!_closed && !string.IsNullOrWhiteSpace(MovieIdBox.Text)) await FindAsync();
            if (!_closed) MovieIdBox.Focus();
        };
    }

    private async void RefreshLibrary_Click(object sender, RoutedEventArgs e) => await RefreshLibraryAsync();

    private async Task RefreshLibraryAsync()
    {
        if (_operation != null) return;
        using var cancellation = new CancellationTokenSource();
        _operation = cancellation; SetBusy(true);
        StatusLabel.Text = "Reading your Plex servers and library folders...";
        LibraryLabel.Text = "Library index: reading...";
        try
        {
            await Task.Run(() => _service.RefreshLibraryAsync(cancellation.Token), cancellation.Token);
            if (_closed) return;
            if (_displayedMovieId != null) ShowSnapshot(_service.Snapshot);
            else StatusLabel.Text = "Library index read. Type a movie and choose Find collection.";
            ShowLibraryStatus();
        }
        catch (OperationCanceledException) { if (!_closed) { StatusLabel.Text = "Library refresh cancelled. No films were added."; ShowLibraryStatus(); } }
        catch { if (!_closed) { StatusLabel.Text = "Library refresh is unavailable. Unknown films cannot be added."; ShowLibraryStatus(); } }
        finally { _operation = null; if (!_closed) SetBusy(false); }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await FindAsync();

    private async void MatchesBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_showingMatches || MatchesBox.SelectedItem is not MatchRow match) return;
        await LookupAsync(match.Id);
    }

    /// <summary>Resolve what was typed to a movie, then look up its collection.</summary>
    private async Task FindAsync()
    {
        if (_operation != null) return;
        var query = CollectionQuery.Parse(MovieIdBox.Text);
        if (query == null) { StatusLabel.Text = "Type a movie title, a TMDB ID or an IMDb ID."; return; }
        if (query.TmdbId is { } direct) { HideMatches(); await LookupAsync(direct); return; }

        using var cancellation = new CancellationTokenSource();
        _operation = cancellation; SetBusy(true);
        StatusLabel.Text = query.ImdbId != null ? "Finding the IMDb title on TMDB..." : $"Searching TMDB for \"{query.Title}\"...";
        IReadOnlyList<TmdbSearchResult> matches;
        try { matches = await Task.Run(() => _service.ResolveMovieAsync(query, cancellation.Token), cancellation.Token); }
        catch (OperationCanceledException) { if (!_closed) StatusLabel.Text = "Search cancelled."; return; }
        catch (InvalidOperationException ex) { if (!_closed) StatusLabel.Text = ex.Message + " Add one in Settings."; return; }
        catch { if (!_closed) StatusLabel.Text = "TMDB search is unavailable right now."; return; }
        finally { _operation = null; if (!_closed) SetBusy(false); }
        if (_closed) return;
        if (matches.Count == 0)
        {
            HideMatches();
            FilmsGrid.ItemsSource = null; CollectionLabel.Text = ""; _displayedMovieId = null;
            StatusLabel.Text = "TMDB found no movie for that. Try the exact title, add the year, or paste a TMDB/IMDb link.";
            return;
        }
        if (matches.Count > 1)
        {
            _showingMatches = true;
            MatchesBox.ItemsSource = matches.Select(m => new MatchRow(m.Id,
                m.YearParsed is { } y ? $"{m.DisplayTitle} ({y})" : m.DisplayTitle)).ToArray();
            MatchesBox.SelectedIndex = 0;
            MatchesPanel.Visibility = Visibility.Visible;
            _showingMatches = false;
        }
        else HideMatches();
        await LookupAsync(matches[0].Id);
    }

    private void HideMatches()
    {
        _showingMatches = true;
        MatchesBox.ItemsSource = null;
        MatchesPanel.Visibility = Visibility.Collapsed;
        _showingMatches = false;
    }

    private async Task LookupAsync(int movieId)
    {
        if (_operation != null) return;
        using var cancellation = new CancellationTokenSource();
        _operation = cancellation;
        SetBusy(true); FilmsGrid.ItemsSource = null; _displayedMovieId = null;
        CollectionLabel.Text = ""; StatusLabel.Text = "Checking collection...";
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
            "available" => snapshot.Films.Any(f => f.Status == "unknown")
                ? "Some films are Unknown: the library index is missing or incomplete. Refresh it, then find the collection again."
                : "Compared with your Plex servers, library folders, downloads and wishlist. Missing films with a known year can be added.",
            "partial" => "Only part of this collection could be read. Only missing films with a known release year can be added; this is not a complete collection inventory.",
            "no-collection" => "TMDB doesn't list this film as part of a collection.",
            "busy" => "Another collection refresh is already running. Try again when it finishes.",
            "not-refreshed" => "Choose Find collection to look up this movie.",
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
        var complete = status.Complete || status.PlexComplete && status.ScanIssues == 0;
        var plex = status.PlexServers.Count == 0 ? "no Plex servers read"
            : $"Plex: {string.Join(", ", status.PlexServers)}{(status.PlexComplete ? "" : " (some servers unreachable)")}";
        LibraryLabel.Text = $"Library index: {(complete ? "complete" : "incomplete")}, {ageText} ({refreshed.ToLocalTime():g}). " +
            $"{plex}; {status.ScannedRoots} folders scanned; {status.ScanIssues} scan issues.";
    }
    private void SetBusy(bool busy)
    {
        RefreshButton.IsEnabled = !busy; CancelButton.IsEnabled = busy;
        RefreshLibraryButton.IsEnabled = !busy;
        MovieIdBox.IsEnabled = !busy; FilmsGrid.IsEnabled = !busy; MatchesBox.IsEnabled = !busy;
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
