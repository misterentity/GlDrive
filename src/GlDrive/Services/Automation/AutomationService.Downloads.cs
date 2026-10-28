using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using GlDrive.Downloads;

namespace GlDrive.Services.Automation;

public sealed partial class AutomationService
{
    private object Downloads(string op, JsonElement a)
    {
        if (op == "downloads.list")
        {
            var filter = Optional(a, "serverId", 128);
            if (filter != null && filter != ServerManager.TorrentDownloadQueueId) Server(filter);
            var status = Optional(a, "status", 32);
            var selected = status == null ? (DownloadStatus?)null : EnumValue(a, "status", DownloadStatus.Queued);
            var all = _servers.GetDownloadQueues().Where(q => filter == null || q.Id == filter)
                .SelectMany(q => q.Manager.Store.Items.Select(item => (Item: item, QueueId: q.Id, QueueName: q.Name)))
                .Where(d => selected == null || d.Item.Status == selected).OrderByDescending(d => d.Item.QueuedAt)
                .Select(d => DownloadSnapshot(d.Item, d.QueueId, d.QueueName)).ToArray();
            return Page(all, a);
        }
        var serverId = Required(a, "serverId", 128);
        if (op == "downloads.enqueue")
        {
            // This endpoint remains FTP-specific. Torrent creation belongs to player.download_torrent.
            var server = Server(serverId);
            var ftpManager = Mounted(serverId).Downloads ?? throw new InvalidOperationException("Download service unavailable.");
            var path = RemotePath(a, allowRoot: false);
            var name = Line(Optional(a, "releaseName", 512) ?? path.Split('/').Last(), "releaseName");
            var category = Line(Optional(a, "category", 128) ?? "", "category");
            var destination = DownloadPathResolver.Flat(_config.Downloads, category, name, Optional(a, "localPath"));
            if (!Path.IsPathFullyQualified(destination)) throw new ArgumentException("'localPath' must be an absolute local directory path.");
            var wishlistId = Optional(a, "wishlistItemId", 128);
            if (wishlistId != null && _wishlist.GetById(wishlistId) == null)
                throw new KeyNotFoundException($"Unknown wishlist item: {wishlistId}");
            var item = new DownloadItem
            {
                RemotePath = path, ReleaseName = name, Category = category, LocalPath = Path.GetFullPath(destination),
                ServerId = serverId, ServerName = server.Name, WishlistItemId = wishlistId
            };
            if (!ftpManager.Enqueue(item)) throw new InvalidOperationException("Download was not queued: it is already active or its destination directory already exists.");
            return DownloadSnapshot(item, serverId, server.Name);
        }
        if (serverId != ServerManager.TorrentDownloadQueueId) Server(serverId);
        var manager = _servers.GetDownloadManager(serverId) ?? throw new InvalidOperationException("Download service unavailable.");
        if (op == "downloads.clear_finished")
        {
            switch (Optional(a, "status", 32) ?? "all")
            {
                case "all": manager.RemoveFinished(); break;
                case "Completed": manager.RemoveCompleted(); break;
                case "Failed": manager.RemoveFailed(); break;
                case "Cancelled": manager.RemoveCancelled(); break;
                default: throw new ArgumentException("'status' must be Completed, Failed, Cancelled or all.");
            }
            return new { cleared = true };
        }
        var id = Required(a, "id", 128);
        var existing = manager.Store.GetById(id) ?? throw new KeyNotFoundException($"Unknown download: {id}");
        switch (op)
        {
            case "downloads.cancel":
                if (existing.Status is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Cancelled)
                    throw new InvalidOperationException("This download is already finished.");
                manager.Cancel(id);
                break;
            case "downloads.retry":
                if (existing.Status is not (DownloadStatus.Failed or DownloadStatus.Cancelled))
                    throw new InvalidOperationException("Only failed or cancelled downloads can be retried.");
                if (Flag(a, "skipBlockedFiles"))
                {
                    if (serverId != ServerManager.TorrentDownloadQueueId || !_servers.SkipBlockedTorrentFilesAndRetry(id))
                        throw new InvalidOperationException("Skipping blocked files requires a refused torrent awaiting content approval.");
                    break;
                }
                if (existing.RequiresContentApproval)
                    throw new InvalidOperationException("This torrent requires explicit skipBlockedFiles=true approval to retry without blocked files.");
                manager.Retry(id);
                break;
            case "downloads.pause":
                if (existing.Status is not (DownloadStatus.Queued or DownloadStatus.Downloading))
                    throw new InvalidOperationException("Only queued or downloading items can be paused.");
                manager.Pause(id);
                break;
            case "downloads.resume":
                if (existing.Status != DownloadStatus.Paused)
                    throw new InvalidOperationException("Only paused downloads can be resumed.");
                manager.Resume(id);
                break;
            case "downloads.remove":
                if (existing.Status is not (DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Cancelled))
                    throw new InvalidOperationException("Cancel an active download before removing its history.");
                manager.Store.Remove(id);
                return new { removed = id };
            case "downloads.reorder":
                if (existing.Status != DownloadStatus.Queued) throw new InvalidOperationException("Only queued downloads can be reordered.");
                switch (Required(a, "direction", 8))
                {
                    case "up": manager.MoveUp(id); break;
                    case "down": manager.MoveDown(id); break;
                    default: throw new ArgumentException("'direction' must be up or down.");
                }
                break;
            default: throw new ArgumentException($"Unknown operation: {op}");
        }
        return DownloadSnapshot(existing, serverId,
            serverId == ServerManager.TorrentDownloadQueueId ? "BitTorrent" : Server(serverId).Name);
    }

    // Explicit public projection: the persisted model includes private tracker-bearing
    // magnet URIs. Never serialize that model through automation responses.
    internal static object DownloadSnapshot(DownloadItem item, string? queueId = null, string? queueName = null) => new
    {
        item.Id, item.RemotePath, item.ReleaseName, item.LocalPath, item.TotalBytes,
        item.DownloadedBytes, item.Status, item.QueuedAt, item.StartedAt, item.CompletedAt,
        item.ErrorMessage, item.WishlistItemId, item.Category,
        ServerId = queueId ?? item.ServerId, ServerName = queueName ?? item.ServerName,
        item.RetryCount, item.SourceCompletionPending, item.SourceKind, item.StatusDetail,
        item.SkipBlockedTorrentFiles, item.RequiresContentApproval
    };

    private async Task<object?> SearchAsync(string op, JsonElement a, CancellationToken ct)
    {
        if (op == "search.refresh")
        {
            var id = Required(a, "serverId", 128);
            await (Mounted(id).Search ?? throw new InvalidOperationException("Search service unavailable.")).RefreshIndex(ct: ct);
            return new { refreshed = id };
        }
        if (op != "search.query") throw new ArgumentException($"Unknown operation: {op}");
        var query = Line(Required(a, "query", 512), "query");
        var filter = Optional(a, "serverId", 128);
        var limit = Integer(a, "limit", 200, 1, 2000);
        var services = filter == null ? _servers.GetMountedServers() : [Mounted(filter)];
        var targets = services.Select(service => new SearchTarget(service.ServerId, service.ServerName,
            service.Search is { } search ? (query, token) => search.SearchDetailed(query, ct: token) : null)).ToArray();
        return await QuerySearchTargetsAsync(targets, query, limit, ct);
    }

    internal sealed record SearchTarget(string ServerId, string ServerName,
        Func<string, CancellationToken, Task<SearchOutcome>>? Search);
    internal sealed record SearchError(string ServerId, string Message);
    internal sealed record SearchServerInfo(string ServerId, string ServerName, string? Method,
        DateTime? IndexBuiltAtUtc, int ResultCount, bool IsPartial, bool TimedOut, bool Truncated,
        bool Unavailable, int FailedListings, IReadOnlyList<SearchListingFailure> Failures);
    internal sealed record SearchResponse(IReadOnlyList<SearchResult> Results, int Total,
        IReadOnlyList<SearchError> Errors, bool IsPartial, bool Truncated, IReadOnlyList<SearchServerInfo> Servers);

    internal static async Task<SearchResponse> QuerySearchTargetsAsync(IReadOnlyList<SearchTarget> targets,
        string query, int limit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (targets.Count == 0) throw new InvalidOperationException("No mounted servers are available to search.");

        // Independent servers can answer concurrently. Capture each failure so a
        // busy server never discards another server's valid matches.
        var responses = await Task.WhenAll(targets.Select(async target =>
        {
            try
            {
                if (target.Search == null) throw new InvalidOperationException("Search service unavailable.");
                var outcome = await target.Search(query, ct);
                ct.ThrowIfCancellationRequested();
                return (Target: target, Outcome: (SearchOutcome?)outcome, Error: (string?)null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                return (Target: target, Outcome: (SearchOutcome?)null, Error: (string?)ex.Message);
            }
        }));
        ct.ThrowIfCancellationRequested();
        var results = new List<SearchResult>();
        var errors = new List<SearchError>();
        var servers = new List<SearchServerInfo>();
        foreach (var response in responses)
        {
            var target = response.Target;
            if (response.Outcome is not { } outcome)
            {
                errors.Add(new SearchError(target.ServerId, response.Error!));
                servers.Add(new SearchServerInfo(target.ServerId, target.ServerName, null, null, 0,
                    true, false, false, true, 0, []));
                continue;
            }
            // Preserve the existing results/total/errors schema. Detailed server
            // diagnostics are additive; legacy clients also see incomplete reads
            // in errors instead of mistaking an unavailable listing for no matches.
            foreach (var result in outcome.Results)
                results.Add(new SearchResult
                {
                    ReleaseName = result.ReleaseName, Category = result.Category, RemotePath = result.RemotePath,
                    Size = result.Size, Modified = result.Modified, Files = result.Files.ToList(),
                    ServerId = target.ServerId, ServerName = target.ServerName
                });
            if (outcome.Failures.Count > 0)
                errors.Add(new SearchError(target.ServerId,
                    $"Search incomplete: {outcome.Failures.Count} listing failure(s); {outcome.Failures[0].Path}: {outcome.Failures[0].Message}"));
            else if (outcome.TimedOut)
                errors.Add(new SearchError(target.ServerId, "Search time limit reached; results are incomplete."));
            else if (outcome.IsPartial && !outcome.Truncated)
                errors.Add(new SearchError(target.ServerId, "Search incomplete; results may be missing."));
            servers.Add(new SearchServerInfo(target.ServerId, target.ServerName, outcome.Method, outcome.IndexBuiltAtUtc,
                outcome.Results.Count, outcome.IsPartial, outcome.TimedOut, outcome.Truncated, false,
                outcome.FailedListings, outcome.Failures.ToArray()));
        }
        var truncated = results.Count > limit || servers.Any(server => server.Truncated);
        return new SearchResponse(results.Take(limit).ToArray(), results.Count, errors,
            truncated || servers.Any(server => server.IsPartial || server.Unavailable || server.TimedOut || server.FailedListings > 0),
            truncated, servers);
    }

    private object Wishlist(string op, JsonElement a)
    {
        switch (op)
        {
            case "wishlist.profiles":
                return new
                {
                    enabled = _config.Downloads.ScoredQualityEnabled,
                    builtIn = Enum.GetValues<QualityProfile>().SelectMany(q => new[]
                    {
                        ScoredQualityProfile.Legacy(q), ScoredQualityProfile.Recommended(q)
                    }).ToArray(),
                    configured = _config.Downloads.Profiles
                };
            case "wishlist.wanted":
                return _servers.WantedStatus();
            case "wishlist.series":
                return _servers.SeriesStatus(Required(a, "itemId", 128));
            case "wishlist.season_plan":
                return _servers.PlanSeriesSeason(Required(a, "itemId", 128), Integer(a, "season", 1, 1, 999));
            case "wishlist.season_pull":
                return _servers.PullSeriesSeason(Required(a, "itemId", 128), Integer(a, "season", 1, 1, 999));
            case "wishlist.score":
            {
                var itemId = Required(a, "itemId", 128);
                if (_wishlist.GetById(itemId) == null) throw new KeyNotFoundException($"Unknown wishlist item: {itemId}");
                long? size = null;
                if (a.TryGetProperty("sizeBytes", out var sizeValue))
                {
                    if (sizeValue.ValueKind != JsonValueKind.Number || !sizeValue.TryGetInt64(out var bytes) || bytes <= 0) throw new ArgumentException("sizeBytes must be a positive integer.");
                    size = bytes;
                }
                return _wishlist.EvaluateScoredRelease(itemId, Line(Required(a, "releaseName", 512), "releaseName"), _config.Downloads, size);
            }
            case "wishlist.list": return Page(_wishlist.Items.ToArray(), a);
            case "wishlist.remove":
            {
                var id = Required(a, "id", 128);
                if (_wishlist.GetById(id) == null) throw new KeyNotFoundException($"Unknown wishlist item: {id}");
                _wishlist.Remove(id);
                return new { removed = id };
            }
            case "wishlist.upsert":
            {
                if (!a.TryGetProperty("item", out var input) || input.ValueKind != JsonValueKind.Object)
                    throw new ArgumentException("'item' must be an object.");
                var id = Optional(input, "id", 128);
                var existing = id == null ? null : _wishlist.GetById(id);
                var node = JsonSerializer.SerializeToNode(existing ?? new WishlistItem
                {
                    Quality = WishlistQuality.Parse(_config.Downloads.QualityDefault)
                }, Json)!.AsObject();
                foreach (var property in input.EnumerateObject())
                {
                    if (!node.ContainsKey(property.Name)) throw new ArgumentException($"Unknown wishlist field: {property.Name}");
                    if (property.Name is "bestOwned" or "pendingGrabs" or "seriesInventory" or "seriesInventoryUpdatedAtUtc" or "seriesInventoryLastAttemptAtUtc") throw new ArgumentException($"{property.Name} is maintained by outcomes or metadata refresh and is read-only.");
                    node[property.Name] = JsonNode.Parse(property.Value.GetRawText());
                }
                // Editing the legacy resolution also updates its migrated profile unless
                // the caller explicitly selected a scored/custom profile.
                if (input.TryGetProperty("quality", out _) && !input.TryGetProperty("profileId", out _) &&
                    (existing?.ProfileId == null || existing.ProfileId.StartsWith("legacy-", StringComparison.OrdinalIgnoreCase)))
                    node["profileId"] = null;
                var item = node.Deserialize<WishlistItem>(Json) ?? throw new ArgumentException("Invalid wishlist item.");
                if (string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Title) || item.Title.Length > 512)
                    throw new ArgumentException("Wishlist items require a nonempty id and a title of at most 512 characters.");
                if (!Enum.IsDefined(item.Type) || !Enum.IsDefined(item.Quality) || !Enum.IsDefined(item.Status))
                    throw new ArgumentException("Invalid wishlist type, quality or status.");
                if (!Enum.IsDefined(item.MonitorMode) || item.MonitorMode != SeriesMonitorMode.Legacy && (item.Type != MediaType.TvShow || item.TvMazeId is not > 0))
                    throw new ArgumentException("Series monitoring requires a TV item, a TVMaze id and a valid monitorMode.");
                if (item.AutoPullCompletedRaces && item.MonitorMode == SeriesMonitorMode.Legacy)
                    throw new ArgumentException("Automatic race pulls require a series monitor mode.");
                if (item.MonitorMode == SeriesMonitorMode.Future && existing?.MonitorMode != SeriesMonitorMode.Future &&
                    !input.TryGetProperty("monitorSinceUtc", out _))
                    item.MonitorSinceUtc = DateTimeOffset.UtcNow;
                if (existing != null && (item.TvMazeId != existing.TvMazeId || item.Title != existing.Title))
                {
                    item.SeriesInventory = [];
                    item.SeriesInventoryUpdatedAtUtc = null;
                    item.SeriesInventoryLastAttemptAtUtc = null;
                }
                if (item.GrabbedReleases == null) throw new ArgumentException("grabbedReleases cannot be null.");
                if (WishlistAcquisition.ResolveProfile(item, _config.Downloads) == null)
                    throw new ArgumentException("Unknown profileId; use wishlist.profiles to list available quality profiles.");
                if (existing == null) _wishlist.Add(item); else _wishlist.Update(item);
                return item;
            }
            case "wishlist.match":
            {
                var service = Mounted(Required(a, "serverId", 128));
                var matcher = service.Matcher ?? throw new InvalidOperationException("Wishlist matcher unavailable.");
                matcher.OnNewRelease(Line(Required(a, "category", 128), "category"),
                    Line(Required(a, "releaseName", 512), "releaseName"), RemotePath(a, allowRoot: false));
                return new { submitted = true };
            }
            default: throw new ArgumentException($"Unknown operation: {op}");
        }
    }

    private object Notifications(string op, JsonElement a)
    {
        if (op == "notifications.clear") { _notifications.Clear(); return new { cleared = true }; }
        if (op != "notifications.list") throw new ArgumentException($"Unknown operation: {op}");
        var serverId = Optional(a, "serverId", 128);
        if (serverId != null) Server(serverId);
        return Page(_notifications.Items.Where(n => serverId == null || n.ServerId == serverId).ToArray(), a);
    }

    private static object Page<T>(IReadOnlyList<T> items, JsonElement a)
    {
        var offset = Integer(a, "offset", 0, 0, int.MaxValue);
        var limit = Integer(a, "limit", 200, 1, 2000);
        return new { items = items.Skip(offset).Take(limit).ToArray(), total = items.Count, offset, limit };
    }
}
