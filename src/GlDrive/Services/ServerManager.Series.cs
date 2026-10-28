using System.Collections.Concurrent;
using GlDrive.Downloads;
using GlDrive.Spread;
using Serilog;

namespace GlDrive.Services;

public partial class ServerManager
{
    private TvMazeClient _seriesMetadata = null!;
    private SeriesInventoryRefresher _seriesRefresher = null!;
    private readonly CancellationTokenSource _seriesCts = new();
    private readonly SitebotFactStore _racePullFacts = new();
    private readonly ConcurrentDictionary<string, (RacePullCandidate Candidate, DateTime Observed)> _racePullPaths = new();

    private void InitializeSeries()
    {
        _seriesMetadata = new TvMazeClient();
        _seriesRefresher = new SeriesInventoryRefresher(_wantedWishlist, _seriesMetadata.GetEpisodes);
        if (_spreadManager != null) _spreadManager.JobCompleted += OnRacePullCompleted;
    }

    private async Task RefreshSeriesAsync()
    {
        if (_disposed || !_servers.Values.Any(m => m.CurrentState == MountState.Connected)) return;
        try { await _seriesRefresher.RefreshDueAsync(_seriesCts.Token); }
        catch (OperationCanceledException) when (_seriesCts.IsCancellationRequested) { }
        catch (Exception ex) { Log.Warning(ex, "Series inventory refresh failed"); }
    }

    private bool IsRacePullComplete(string serverId, string release) =>
        _config.Downloads.RaceCompletedPullsEnabled && _config.Downloads.RacePullServerIds.Contains(serverId) &&
        _racePullFacts.TryGet(serverId, release)?.Complete != null;

    private void OnRacePullCompleted(SpreadJob job)
    {
        if (_disposed || !_config.Downloads.RaceCompletedPullsEnabled) return;
        try
        {
            var followed = _wantedWishlist.Items.Where(i => SeriesMonitorPolicy.IsMonitored(i) && i.AutoPullCompletedRaces);
            foreach (var candidate in RacePullPolicy.Find(job.GetDetail(), followed))
            {
                if (!_config.Downloads.RacePullServerIds.Contains(candidate.ServerId)) continue;
                var now = DateTime.UtcNow;
                _racePullFacts.Observe(new SitebotEvent
                {
                    ServerId = candidate.ServerId, Release = candidate.ReleaseName, Section = candidate.Category,
                    Kind = SitebotEventKind.Complete, Bytes = candidate.SizeBytes, ObservedAtUtc = now
                });
                _racePullPaths[candidate.ServerId + "\n" + candidate.ReleaseName.ToUpperInvariant()] = (candidate, now);
                OfferRacePull(candidate, now);
            }
            foreach (var entry in _racePullPaths.Where(p => p.Value.Observed < DateTime.UtcNow.AddHours(-6)).ToArray())
                _racePullPaths.TryRemove(entry.Key, out _);
            foreach (var entry in _racePullPaths.OrderByDescending(p => p.Value.Observed).Skip(1024).ToArray())
                _racePullPaths.TryRemove(entry.Key, out _);
        }
        catch (Exception ex) { Log.Warning(ex, "Race completion could not update series pulls"); }
    }

    private void OfferRacePull(RacePullCandidate candidate, DateTime observed)
    {
        if (!IsRacePullComplete(candidate.ServerId, candidate.ReleaseName) ||
            !_servers.TryGetValue(candidate.ServerId, out var mount) || mount.Matcher == null) return;
        foreach (var id in candidate.WishlistItemIds)
            if (_wantedWishlist.GetById(id) is { AutoPullCompletedRaces: true } item && SeriesMonitorPolicy.IsMonitored(item))
                _wantedCoordinator.Offer(mount.Matcher, candidate.Category, candidate.ReleaseName, candidate.RemotePath,
                    WantedReadiness.TrustedComplete, sizeBytes: candidate.SizeBytes, completeAtUtc: observed, wantedItemId: id,
                    acquisitionAllowed: () => IsRacePullComplete(candidate.ServerId, candidate.ReleaseName) &&
                        _wantedWishlist.GetById(id) is { AutoPullCompletedRaces: true } current && SeriesMonitorPolicy.IsMonitored(current));
    }

    private void ReplayRacePulls(string serverId)
    {
        foreach (var evidence in _racePullPaths.Values.Where(p => p.Candidate.ServerId == serverId))
            OfferRacePull(evidence.Candidate, evidence.Observed);
    }

    private IReadOnlyList<SearchResult> CachedSeriesCandidates() => _servers.Values
        .Where(m => m.CurrentState == MountState.Connected && m.Search != null)
        .SelectMany(m => m.Search!.GetCachedIndexSnapshot().Entries.Select(entry => new SearchResult
        {
            ServerId = m.ServerId, ServerName = m.ServerName, ReleaseName = entry.ReleaseName,
            RemotePath = entry.RemotePath, Category = entry.Category, Modified = entry.Modified
        })).ToArray();

    public object SeriesStatus(string itemId)
    {
        var item = _wantedWishlist.GetById(itemId) ?? throw new KeyNotFoundException("Unknown wishlist item.");
        return new { item.Id, item.Title, item.MonitorMode, item.SeriesInventoryUpdatedAtUtc,
            episodes = SeriesMonitorPolicy.GetEpisodes(item, CachedSeriesCandidates(), DateTimeOffset.UtcNow) };
    }

    public SeriesSeasonPlan PlanSeriesSeason(string itemId, int season)
    {
        var item = _wantedWishlist.GetById(itemId) ?? throw new KeyNotFoundException("Unknown wishlist item.");
        return SeriesSeasonAssembler.Plan(item, season, CachedSeriesCandidates(), _config.Downloads, DateTimeOffset.UtcNow,
            server => _config.Downloads.PreferFastestWantedSource && _spreadManager != null
                ? DownloadSourceRanker.MeasuredSpeed(_spreadManager.TransferSpeeds, server) : 0);
    }

    public object PullSeriesSeason(string itemId, int season)
    {
        var plan = PlanSeriesSeason(itemId, season);
        if (!plan.Ready) return new { admitted = 0, plan };
        var admitted = 0;
        foreach (var candidate in plan.Releases)
            if (_servers.TryGetValue(candidate.ServerId, out var mount) && mount.Matcher != null &&
                mount.Matcher.TryAcquireSeries(itemId, candidate.Category, candidate.ReleaseName, candidate.RemotePath)) admitted++;
        return new { admitted, plan };
    }

    private void StopSeries()
    {
        if (_spreadManager != null) _spreadManager.JobCompleted -= OnRacePullCompleted;
        _seriesCts.Cancel();
        _seriesMetadata.Dispose();
    }
}
