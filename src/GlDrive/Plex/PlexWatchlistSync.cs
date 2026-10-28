using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Library;
using Serilog;

namespace GlDrive.Plex;

public sealed record WatchlistSeriesMetadata(int TvMazeId, bool Ended, IReadOnlyList<SeriesEpisode>? Inventory);
public sealed record PlexWatchlistSyncResult(string Status, int Added = 0, int Existing = 0, int Completed = 0, int Skipped = 0);

/// <summary>One-way, explicitly enabled inbox. Plex is read only; local decisions survive offline providers.</summary>
public sealed class PlexWatchlistSync : IDisposable
{
    public const string Source = "PlexWatchlist";
    private readonly AppConfig _config;
    private readonly WishlistStore _wishlist;
    private readonly Func<IEnumerable<DownloadItem>> _downloads;
    private readonly Func<LibraryIndexSnapshot> _library;
    private readonly Func<IPlexWatchlistReader?> _reader;
    private readonly Func<WishlistItem, CancellationToken, Task<WatchlistSeriesMetadata?>> _metadata;
    private readonly Func<DateTimeOffset> _now;
    private readonly CancellationTokenSource _stop = new();
    private readonly Timer? _timer;
    private int _running, _reconciling, _disposed;
    private DateTimeOffset? _lastAttempt;
    public PlexWatchlistSyncResult LastResult { get; private set; } = new("not-run");

    public PlexWatchlistSync(AppConfig config, WishlistStore wishlist, Func<IEnumerable<DownloadItem>> downloads,
        Func<LibraryIndexSnapshot> library)
        : this(config, wishlist, downloads, library, () => CreateReader(config), startTimer: true) { }

    internal PlexWatchlistSync(AppConfig config, WishlistStore wishlist, Func<IEnumerable<DownloadItem>> downloads,
        Func<LibraryIndexSnapshot> library, Func<IPlexWatchlistReader?> reader,
        Func<WishlistItem, CancellationToken, Task<WatchlistSeriesMetadata?>>? metadata = null,
        Func<DateTimeOffset>? now = null, bool startTimer = false)
    {
        _config = config; _wishlist = wishlist; _downloads = downloads; _library = library;
        _reader = reader; _metadata = metadata ?? FetchMetadata; _now = now ?? (() => DateTimeOffset.UtcNow);
        if (startTimer)
        {
            using var flow = ExecutionContext.IsFlowSuppressed() ? default : ExecutionContext.SuppressFlow();
            _timer = new(_ => _ = SyncAsync(), null, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(1));
        }
    }

    private bool Enabled() => Volatile.Read(ref _disposed) == 0 && _config.Plex.WatchlistSyncEnabled;

    public async Task<PlexWatchlistSyncResult> SyncAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        if (!Enabled()) return LastResult = new("disabled");
        if (Interlocked.Exchange(ref _running, 1) != 0) return new("busy");
        var added = 0; var existing = 0; var skipped = 0;
        var status = "complete";
        try
        {
            var now = _now();
            if (!force && _lastAttempt is { } last && now - last < TimeSpan.FromMinutes(Math.Clamp(_config.Plex.WatchlistSyncMinutes, 5, 1440)))
                return LastResult;
            _lastAttempt = now;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
            deadline.CancelAfter(TimeSpan.FromMinutes(2));
            try
            {
                using var client = _reader();
                if (client == null) status = "not-signed-in";
                else
                {
                    await foreach (var page in client.ReadWatchlistAsync(deadline.Token).ConfigureAwait(false))
                    {
                        deadline.Token.ThrowIfCancellationRequested();
                        if (!Enabled()) return LastResult = new("disabled", added, existing, 0, skipped);
                        var mapped = page.Items.Select(entry => Map(entry, _config.Downloads)).ToArray();
                        var imported = _wishlist.ImportWatchlistPage(mapped, Enabled);
                        added += imported.Added; existing += imported.Existing; skipped += page.Skipped;
                    }
                }
                // Metadata is daily and independent of import. Missing external IDs or an
                // offline lookup leave a valid inbox entry Watching, never drop the batch.
                foreach (var item in _wishlist.Items)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    if (!Enabled()) return LastResult = new("disabled", added, existing, 0, skipped);
                    if (!_wishlist.TryBeginWatchlistMetadata(item.Id, now, Enabled)) continue;
                    try
                    {
                        var metadata = await _metadata(item, deadline.Token).ConfigureAwait(false);
                        deadline.Token.ThrowIfCancellationRequested();
                        if (metadata?.TvMazeId is > 0)
                            _wishlist.ApplyWatchlistMetadata(item.Id, metadata.TvMazeId, metadata.Ended, metadata.Inventory, now, Enabled);
                    }
                    catch (OperationCanceledException) when (deadline.IsCancellationRequested) { throw; }
                    catch { Log.Warning("Watchlist series metadata unavailable; item remains open"); }
                }
            }
            catch (OperationCanceledException) { status = "cancelled"; }
            catch { status = "unavailable"; Log.Warning("Plex watchlist read unavailable; existing wishlist entries are retained"); }
            var completed = !cancellationToken.IsCancellationRequested && Enabled() ? ReconcileOwnership() : 0;
            return LastResult = new(status, added, existing, completed, skipped);
        }
        catch
        {
            Log.Warning("Plex watchlist sync unavailable; normal activity continues");
            return LastResult = new("unavailable", added, existing, 0, skipped);
        }
        finally { Volatile.Write(ref _running, 0); }
    }

    public void QueueOwnershipReconcile()
    {
        if (!Enabled() || Interlocked.Exchange(ref _reconciling, 1) != 0) return;
        using var flow = ExecutionContext.IsFlowSuppressed() ? default : ExecutionContext.SuppressFlow();
        _ = Task.Run(() =>
        {
            try { ReconcileOwnership(); }
            catch { Log.Warning("Watchlist ownership reconciliation unavailable; items remain open"); }
            finally { Volatile.Write(ref _reconciling, 0); }
        });
    }

    private int ReconcileOwnership()
    {
        if (!Enabled()) return 0;
        var downloads = WishlistOwnership.PrepareDownloads(_downloads());
        var index = _library();
        return _wishlist.CompleteOwnedWatchlist(item => WishlistOwnership.IsOwnedPrepared(item, _config.Downloads, index, downloads, _now()), Enabled);
    }

    internal static WishlistItem Map(PlexWatchlistEntry entry, DownloadConfig config)
    {
        var quality = WishlistQuality.Parse(config.QualityDefault);
        return new()
        {
            Source = Source, PlexWatchlistId = entry.Id, Type = entry.Type, Title = entry.Title, Year = entry.Year,
            ImdbId = entry.ImdbId, TmdbId = entry.TmdbId, TvdbId = entry.TvdbId,
            Quality = quality, ProfileId = ScoredQualityProfile.Legacy(quality).Id, MonitorMode = SeriesMonitorMode.Legacy
        };
    }

    private static IPlexWatchlistReader? CreateReader(AppConfig config)
    {
        var token = CredentialStore.GetApiKey("plex");
        if (string.IsNullOrEmpty(token)) return null;
        return new PlexClient(string.IsNullOrWhiteSpace(config.Plex.ClientIdentifier) ? "gldrive-watchlist-readonly" : config.Plex.ClientIdentifier,
            "GlDrive", Services.UpdateChecker.CurrentVersion.ToString(), token);
    }

    private static async Task<WatchlistSeriesMetadata?> FetchMetadata(WishlistItem item, CancellationToken ct)
    {
        using var client = new TvMazeClient();
        var show = item.TvMazeId is > 0 ? await client.GetShow(item.TvMazeId.Value, ct).ConfigureAwait(false) :
            await client.LookupExternal(item.TvdbId, item.ImdbId, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (show?.Id is not > 0) return null;
        var ended = string.Equals(show.Status, "Ended", StringComparison.OrdinalIgnoreCase);
        IReadOnlyList<SeriesEpisode>? inventory = null;
        if (ended)
        {
            var episodes = await client.GetEpisodes(show.Id, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (episodes != null) inventory = SeriesInventoryRefresher.ConvertEpisodes(episodes);
        }
        return new(show.Id, ended, inventory);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _timer?.Dispose();
        _stop.Cancel();
    }
}
