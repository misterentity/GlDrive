using GlDrive.Downloads;
using GlDrive.Spread;
using Serilog;

namespace GlDrive.Services;

public partial class ServerManager
{
    private WishlistStore _wantedWishlist = null!;
    private WantedAcquisitionCoordinator _wantedCoordinator = null!;
    private System.Threading.Timer? _wantedTimer;
    private int _wantedSweepRequested;
    private int _wantedTickRunning;
    private IReadOnlyList<WantedSiteOffer> _wantedOffers = [];

    public sealed record WantedSiteOffer(string ServerId, WantedIndexCandidate Candidate);

    private void InitializeWanted()
    {
        _wantedWishlist = new WishlistStore();
        _wantedCoordinator = new WantedAcquisitionCoordinator(_wantedWishlist, _config.Downloads)
        {
            CompletionStillValid = (serverId, release) => !_disposed &&
                _nukes.StateAvailable &&
                !_nukes.IsNuked(serverId, release) &&
                _servers.TryGetValue(serverId, out var mount) && mount.CurrentState == MountState.Connected &&
                (_spreadManager?.SitebotFacts.TryGet(serverId, release)?.Complete != null || IsRacePullComplete(serverId, release)),
            SourceSpeed = serverId => _spreadManager is { } spread
                ? DownloadSourceRanker.MeasuredSpeed(spread.TransferSpeeds, serverId) : 0
        };
        InitializeSeries();
        _wantedWishlist.Changed += QueueWantedSweep;
        _wantedTimer = new System.Threading.Timer(_ => ProcessWanted(), null,
            TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    private void QueueWantedSweep()
    {
        if (_disposed) return;
        Interlocked.Exchange(ref _wantedSweepRequested, 1);
        _ = Task.Run(ProcessWanted);
    }

    private void ProcessWanted()
    {
        if (_disposed || Interlocked.Exchange(ref _wantedTickRunning, 1) != 0) return;
        try
        {
            if (!_config.Downloads.WantedIndexSweepsEnabled) _wantedOffers = [];
            var sweepRequested = Interlocked.Exchange(ref _wantedSweepRequested, 0) != 0;
            if (sweepRequested)
                foreach (var serverId in _servers.Keys) ReplayRacePulls(serverId);
            if (sweepRequested && _config.Downloads.WantedIndexSweepsEnabled)
            {
                var offers = new List<WantedSiteOffer>();
                foreach (var mount in _servers.Values.OrderBy(s => s.ServerId, StringComparer.Ordinal))
                {
                    if (_disposed) return;
                    if (mount.CurrentState != MountState.Connected || mount.Search == null || mount.Matcher == null) continue;
                    var candidates = WantedIndexSweep.Find(mount.Search.GetCachedIndexSnapshot(), _wantedWishlist,
                        _config.Downloads, _config.Downloads.WantedIndexSweepLimit);
                    foreach (var candidate in candidates)
                    {
                        offers.Add(new(mount.ServerId, candidate));
                        if (!_config.Downloads.AutoDownloadWishlist || !_config.Downloads.WantedDecisionWindowEnabled) continue;
                        var wanted = _wantedWishlist.GetById(candidate.WishlistItemId);
                        if (wanted == null || wanted.Type == MediaType.TvShow && wanted.MonitorMode == SeriesMonitorMode.Legacy) continue;
                        var complete = _spreadManager?.SitebotFacts.TryGet(mount.ServerId, candidate.ReleaseName)?.Complete;
                        _wantedCoordinator.Offer(mount.Matcher, candidate.Category, candidate.ReleaseName, candidate.RemotePath,
                            complete != null ? WantedReadiness.TrustedComplete : WantedReadiness.Unknown,
                            sizeBytes: complete?.Bytes, completeAtUtc: complete?.ObservedAtUtc, wantedItemId: candidate.WishlistItemId);
                    }
                }
                _wantedOffers = offers.AsReadOnly();
            }
            _wantedCoordinator.ProcessDue();
            _ = RefreshSeriesAsync();
        }
        catch (Exception ex) { Log.Warning(ex, "Wanted cache sweep failed"); }
        finally { Volatile.Write(ref _wantedTickRunning, 0); }
    }

    private void ObserveWantedSitebot(SitebotEvent observation)
    {
        if (_disposed) return;
        var facts = _spreadManager?.SitebotFacts.TryGet(observation.ServerId, observation.Release);
        // The B7 fact store rejects stale/future/out-of-order observations.
        if (facts?.Latest?.ObservedAtUtc != observation.ObservedAtUtc || facts.Latest.Kind != observation.Kind) return;
        if (facts.Complete == null) _wantedCoordinator.Invalidate(observation.ServerId, observation.Release);
        if (facts.Complete == null) _racePullFacts.Observe(observation);
        if (observation.Kind == SitebotEventKind.Complete && facts.Complete != null)
            OfferWantedCompletion(facts.Complete);
    }

    private void OfferWantedCompletion(SitebotEvent observation)
    {
        if (_disposed || !_config.Downloads.AutoDownloadWishlist || !_config.Downloads.WantedDecisionWindowEnabled) return;
        var server = _config.Servers.FirstOrDefault(s => s.Id == observation.ServerId && s.Enabled);
        if (server == null || !_servers.TryGetValue(server.Id, out var mount) || mount.Matcher == null) return;
        var path = WishlistIrcRelease.ResolvePath(server, observation.Section, observation.Release);
        if (path != null) _wantedCoordinator.Offer(mount.Matcher, observation.Section, observation.Release, path,
            WantedReadiness.TrustedComplete, fromIrc: true, sizeBytes: observation.Bytes,
            completeAtUtc: observation.ObservedAtUtc);
    }

    private void ReplayWantedCompletions(string serverId)
    {
        foreach (var facts in _spreadManager?.SitebotFacts.Snapshot() ?? [])
            if (facts.ServerId == serverId && facts.Complete != null)
                OfferWantedCompletion(facts.Complete);
    }

    public object WantedStatus() => new
    {
        windowEnabled = _config.Downloads.WantedDecisionWindowEnabled,
        indexSweepsEnabled = _config.Downloads.WantedIndexSweepsEnabled,
        windows = _wantedCoordinator.Snapshot(),
        offers = _wantedOffers
    };

    private void StopWanted()
    {
        StopSeries();
        _wantedWishlist.Changed -= QueueWantedSweep;
        _wantedTimer?.Dispose();
        foreach (var server in _servers.Keys) _wantedCoordinator.RemoveServer(server);
        _wantedOffers = [];
    }
}
