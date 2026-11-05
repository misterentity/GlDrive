using GlDrive.Config;
using GlDrive.Library;

namespace GlDrive.Downloads;

public sealed record CollectionGapSnapshot(int MovieTmdbId, string Status, int? CollectionId,
    string? CollectionName, IReadOnlyList<CollectionGapFilm> Films, DateTimeOffset? CheckedAtUtc);
public sealed record CollectionGapAddResult(string Status, string? WishlistId);
public sealed record CollectionLibraryState(DateTime? RefreshedAtUtc, bool Complete, int ScannedRoots, int ScanIssues)
{
    public IReadOnlyList<string> PlexServers { get; init; } = [];
    public bool PlexComplete { get; init; }
}

/// <summary>
/// One explicit TMDB collection lookup shared by the UI and automation. Local
/// comparisons and wishlist adds never request metadata or refresh the disk index.
/// </summary>
public sealed class CollectionGapService
{
    private readonly AppConfig _config;
    private readonly WishlistStore _wishlist;
    private readonly Func<LibraryIndexSnapshot> _index;
    private readonly Func<IEnumerable<DownloadItem>> _downloads;
    private readonly Func<int, CancellationToken, Task<TmdbCollectionResult>> _provider;
    private readonly Func<CancellationToken, Task<LibraryIndexSnapshot>>? _refreshIndex;
    private readonly SemaphoreSlim _libraryRefresh = new(1, 1);
    private readonly Lock _gate = new();
    private int _refreshing;
    private TmdbCollectionData? _collection;
    private CollectionGapSnapshot _snapshot = Empty(0, "not-refreshed", null);

    public CollectionGapService(AppConfig config, WishlistStore wishlist, Func<LibraryIndexSnapshot> index,
        Func<IEnumerable<DownloadItem>> downloads,
        Func<int, CancellationToken, Task<TmdbCollectionResult>>? provider = null,
        Func<CancellationToken, Task<LibraryIndexSnapshot>>? refreshIndex = null)
    {
        _config = config;
        _wishlist = wishlist;
        _index = index;
        _downloads = downloads;
        _provider = provider ?? FetchCollectionAsync;
        _refreshIndex = refreshIndex;
    }

    public CollectionGapSnapshot Snapshot { get { lock (_gate) return _snapshot; } }
    public CollectionLibraryState LibraryStatus
    {
        get
        {
            var index = ReadIndex();
            return new(index.RefreshedAtUtc == DateTime.MinValue ? null : index.RefreshedAtUtc,
                index.Complete, index.ScannedRoots, index.ScanIssues)
            { PlexServers = index.PlexServers, PlexComplete = index.PlexComplete };
        }
    }

    public async Task<CollectionLibraryState> RefreshLibraryAsync(CancellationToken ct = default)
    {
        if (_refreshIndex == null) throw new InvalidOperationException("Library refresh is unavailable.");
        await _libraryRefresh.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _refreshIndex(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            TmdbCollectionData? collection;
            lock (_gate) collection = _collection;
            if (collection != null)
            {
                var films = Compare(collection);
                lock (_gate)
                    if (ReferenceEquals(_collection, collection)) _snapshot = _snapshot with { Films = films };
            }
            return LibraryStatus;
        }
        finally { _libraryRefresh.Release(); }
    }

    public async Task<CollectionGapSnapshot> RefreshAsync(int movieTmdbId, CancellationToken ct = default)
    {
        if (movieTmdbId <= 0) throw new ArgumentOutOfRangeException(nameof(movieTmdbId));
        ct.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0)
            return Empty(movieTmdbId, "busy", null);
        try
        {
            TmdbCollectionResult result;
            try { result = await _provider(movieTmdbId, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { result = new("unknown", null); }
            ct.ThrowIfCancellationRequested();
            var checkedAt = DateTimeOffset.UtcNow;
            var collection = result.Status is "available" or "partial" ? result.Collection : null;
            var next = collection == null
                ? Empty(movieTmdbId, result.Status == "no-collection" ? "no-collection" : "unknown", checkedAt)
                : new CollectionGapSnapshot(movieTmdbId, result.Status, collection.Id, collection.Name,
                    Compare(collection), checkedAt);
            lock (_gate)
            {
                _collection = collection;
                return _snapshot = next;
            }
        }
        finally { Volatile.Write(ref _refreshing, 0); }
    }

    public CollectionGapAddResult AddMissing(int movieTmdbId, int filmTmdbId)
    {
        TmdbCollectionData collection;
        lock (_gate)
        {
            if (_collection == null || _snapshot.MovieTmdbId != movieTmdbId || filmTmdbId <= 0)
                return new("unknown-member", null);
            collection = _collection;
        }
        var film = collection.Films.FirstOrDefault(item => item.TmdbId == filmTmdbId);
        if (film == null) return new("unknown-member", null);
        if (film.Year is not (>= 1 and <= 9999)) return new("year-required", null);

        // Re-evaluate positive evidence at the click, including a download or
        // wishlist addition which arrived since the explicit provider refresh.
        var rows = Compare(collection);
        var row = rows.First(item => item.TmdbId == filmTmdbId);
        CollectionGapAddResult result;
        if (row.Status is "owned" or "downloading") result = new(row.Status, null);
        else if (row.Status == "unknown") result = new("ownership-unknown", null);
        else result = _wishlist.AddCollectionMovie(film, WishlistQuality.Parse(_config.Downloads.QualityDefault));

        var updated = Compare(collection);
        lock (_gate)
        {
            // A different window may have refreshed another collection while an
            // observer handled the normal wishlist add. Never overwrite its view.
            if (ReferenceEquals(_collection, collection) && _snapshot.MovieTmdbId == movieTmdbId)
                _snapshot = _snapshot with { Films = updated };
        }
        return result;
    }

    private IReadOnlyList<CollectionGapFilm> Compare(TmdbCollectionData collection)
    {
        var index = ReadIndex();
        DownloadItem[] downloads;
        try { downloads = _downloads().ToArray(); }
        catch { downloads = []; index = index with { Complete = false }; }
        return CollectionGapFinder.Compare(collection, index, downloads, _wishlist.Items, _config.Downloads);
    }

    private LibraryIndexSnapshot ReadIndex()
    {
        try { return _index(); }
        catch { return LibraryIndexSnapshot.Empty; }
    }

    /// <summary>Turn a typed title / IMDb id / TMDB id or link into movie candidates, best first.
    /// A TMDB id needs no request; a title is one or two TMDB searches.</summary>
    public async Task<IReadOnlyList<TmdbSearchResult>> ResolveMovieAsync(CollectionQuery query, CancellationToken ct = default)
    {
        if (query.TmdbId is { } id) return [new TmdbSearchResult { Id = id, MediaType = "movie" }];
        var client = MetadataClients.Tmdb;
        if (!client.HasApiKey) throw new InvalidOperationException("No TMDB API key is configured.");
        if (query.ImdbId != null)
            return await client.FindMovieByImdbId(query.ImdbId, ct).ConfigureAwait(false) is { } found
                ? [new TmdbSearchResult { Id = found, MediaType = "movie" }] : [];
        var results = new List<TmdbSearchResult>();
        foreach (var text in query.SearchTexts())
            results.AddRange(await client.SearchMulti(text, ct).ConfigureAwait(false));
        return query.Rank(results);
    }

    private async Task<TmdbCollectionResult> FetchCollectionAsync(int movieTmdbId, CancellationToken ct)
    {
        var client = MetadataClients.Tmdb;
        return await client.GetMovieCollection(movieTmdbId, ct).ConfigureAwait(false);
    }

    private static CollectionGapSnapshot Empty(int movieTmdbId, string status, DateTimeOffset? checkedAt)
        => new(movieTmdbId, status, null, null, [], checkedAt);
}
