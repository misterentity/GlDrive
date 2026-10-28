namespace GlDrive.Spread;

public partial class SpreadManager
{
    private PersistedRace PredictRaceNoLock(PersistedRace request)
    {
        var sourceIds = new[] { request.KnownSourceServerId }.OfType<string>().Concat(RaceAnnounceMerge.Hints(request).Select(h => h.ServerId))
            .Concat(request.ServerIds).Distinct(StringComparer.Ordinal).ToArray();
        var announced = sourceIds.Select(id => SitebotFacts.TryGet(id, request.ReleaseName)?.AnnouncedBytes)
            .FirstOrDefault(bytes => bytes is > 0);
        if (announced == null && request.PredictedSeconds is > 0 && double.IsFinite(request.PredictedSeconds.Value)) return request;
        var source = request.KnownSourceServerId;
        double[] speeds = source == null ? [] : request.ServerIds.Where(id => id != source &&
                _config.Servers.Any(s => s.Id == id && !s.SpreadSite.DownloadOnly))
            .Select(id => _speedTracker.GetAverageSpeed(source, id)).Where(speed => speed > 0 && double.IsFinite(speed)).ToArray();
        var estimate = RaceWorkEstimator.Estimate(request.Section, request.ReleaseName, announced ?? request.ExpectedBytes,
            speeds.Length > 0 ? 1 / speeds.Sum(speed => 1 / speed) : 0, _history.Items);
        if (request.ExpectedBytes == estimate.ExpectedBytes && request.PredictedSeconds == estimate.PredictedSeconds
            && request.PredictionBasis == estimate.Basis) return request;
        return request with { ExpectedBytes = estimate.ExpectedBytes, PredictedSeconds = estimate.PredictedSeconds, PredictionBasis = estimate.Basis };
    }

    private PersistedRace MergeRaceNoLock(PersistedRace existing, PersistedRace incoming, bool canChangeSection)
    {
        var merged = RaceAnnounceMerge.Merge(existing, incoming,
            id => _config.Servers.FirstOrDefault(s => s.Id == id), canChangeSection);
        if (merged.Section != existing.Section || merged.KnownSourceServerId != existing.KnownSourceServerId)
            merged = merged with
            {
                PredictedSeconds = null, PredictionBasis = null,
                ExpectedBytes = existing.PredictionBasis?.StartsWith("announced-size/", StringComparison.Ordinal) == true ? existing.ExpectedBytes : null
            };
        return PredictRaceNoLock(merged);
    }

    private RaceStartResult? MergeDuplicateNoLock(PersistedRace incoming, DateTime now)
    {
        var active = _activeJobs.FirstOrDefault(j => IsSameRace(j.Section, j.ReleaseName, incoming.Section, incoming.ReleaseName));
        var starting = _startingRequests.FindIndex(r => IsSameRace(r.Section, r.ReleaseName, incoming.Section, incoming.ReleaseName));
        if (active != null || starting >= 0)
        {
            var changed = false;
            if (_config.Spread.MergeRaceAnnounces)
            {
                if (active != null && _activeRequests.TryGetValue(active, out var request) && !request.CleanupOnly)
                {
                    var merged = MergeRaceNoLock(request, incoming, false);
                    changed = !ReferenceEquals(merged, request);
                    _activeRequests[active] = merged;
                    if (changed) active.QueueSourceHints(merged.SourceHints);
                }
                else if (starting >= 0 && !_startingRequests[starting].CleanupOnly)
                {
                    var startingRequest = _startingRequests[starting];
                    var merged = MergeRaceNoLock(startingRequest, incoming, true);
                    changed = !ReferenceEquals(merged, startingRequest);
                    _startingRequests[starting] = merged;
                }
            }
            return new(RaceStartOutcome.AlreadyActive, incoming.Section, incoming.ReleaseName, Job: active,
                Reason: active == null ? "Race admission is already in progress" : "Release is already racing", HintsMerged: changed);
        }
        var duplicate = _raceQueue.FindIndex(r => IsSameRace(r.Section, r.ReleaseName, incoming.Section, incoming.ReleaseName));
        if (duplicate < 0) return null;
        var queued = _raceQueue[duplicate];
        var updated = _config.Spread.MergeRaceAnnounces && !queued.CleanupOnly ? MergeRaceNoLock(queued, incoming, true) : queued;
        _raceQueue[duplicate] = updated;
        return new(RaceStartOutcome.Duplicate, incoming.Section, incoming.ReleaseName,
            QueuePosition: QueuePositionNoLock(updated, now), Reason: updated.WaitReason ?? "Release is already queued",
            HintsMerged: !ReferenceEquals(updated, queued));
    }

    private int StartingIndexNoLock(PersistedRace request) => _startingRequests.FindIndex(r => r.RequestId == request.RequestId);
}
