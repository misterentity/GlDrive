namespace GlDrive.Spread;

public sealed record BulkRaceRemovalResult(int Removed, int Skipped);
public sealed record BulkRaceSiteEditResult(int Changed, int Skipped, bool Saved);
public sealed record RaceBatchRequest(string Section, string ReleaseName, IReadOnlyList<string> ServerIds,
    SpreadMode Mode, string? KnownSourceServerId = null, string? KnownSourcePath = null);

public partial class SpreadManager
{
    public const int MaxManualRaceBatch = 25;

    /// <summary>Admit one explicit manual batch, save once, then let the ordinary queue worker start it.</summary>
    public IReadOnlyList<RaceStartResult> EnqueueRacesDetailed(IReadOnlyList<RaceBatchRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count > MaxManualRaceBatch) throw new ArgumentException($"Select at most {MaxManualRaceBatch} releases per race batch", nameof(requests));
        if (requests.Count == 0) return [];
        var results = new List<RaceStartResult>(requests.Count);
        var changed = false;
        lock (_lock)
        {
            var now = AdmissionUtcNow();
            foreach (var item in requests)
            {
                var section = SanitizeFtpPath(item.Section);
                var release = SanitizeFtpPath(item.ReleaseName);
                var request = new PersistedRace(section, release, item.ServerIds.Distinct(StringComparer.Ordinal).ToList(),
                    item.Mode, item.KnownSourceServerId, item.KnownSourcePath, now);
                if (_disposed || IsRaceNuked(request))
                {
                    results.Add(new(RaceStartOutcome.Unavailable, section, release, Reason: _disposed ? "Spread engine is stopped" : NukeHoldMessage));
                    continue;
                }
                if (MergeDuplicateNoLock(request, now, includeQueuePosition: false) is { } duplicate)
                {
                    changed |= duplicate.HintsMerged;
                    results.Add(duplicate);
                    continue;
                }
                if (IsRecentlyDeadRace(section, release, now, out var deadReason, out var remaining))
                {
                    results.Add(new(RaceStartOutcome.Suppressed, section, release, SuppressedFor: remaining, Reason: deadReason));
                    continue;
                }
                request = _config.Spread.MergeRaceAnnounces ? MergeRaceNoLock(request, request, true) : PredictRaceNoLock(request);
                _raceQueue.Add(request);
                changed = true;
                results.Add(new(RaceStartOutcome.Queued, section, release));
            }
            // Queue ordering can be expensive; calculate it once for the completed batch.
            var positions = EligibleQueueIndicesNoLock(now).Select((index, position) => (index, position: position + 1))
                .ToDictionary(pair => _raceQueue[pair.index].RequestId, pair => pair.position, StringComparer.Ordinal);
            for (var index = 0; index < results.Count; index++)
            {
                var result = results[index];
                if (result.Outcome is not (RaceStartOutcome.Queued or RaceStartOutcome.Duplicate)) continue;
                var request = _raceQueue.First(race => IsSameRace(race.Section, race.ReleaseName, result.Section, result.ReleaseName));
                var position = positions.TryGetValue(request.RequestId, out var value) ? (int?)value : null;
                results[index] = result with { QueuePosition = position,
                    Reason = result.Reason ?? (position == null ? "Waiting for connected spread pools" : null) };
            }
        }
        if (changed)
        {
            PersistQueue();
            _workers.TryRun(() => { DequeueNextRace(); return Task.CompletedTask; });
        }
        return results;
    }

    /// <summary>Change exact queued identities as one transaction, retaining live state if its local save fails.</summary>
    public BulkRaceSiteEditResult EditQueuedRaceSites(IEnumerable<string> requestIds, string serverId, bool add)
    {
        var selected = requestIds.ToHashSet(StringComparer.Ordinal);
        var changed = new List<string>();
        lock (_lock)
        {
            if (_disposed) return new(0, selected.Count, true);
            var server = _config.Servers.FirstOrDefault(site => site.Id == serverId);
            var available = server is { Enabled: true } && server.SpreadSite.Sections.Count > 0 &&
                _spreadPools.TryGetValue(serverId, out var pool) && !pool.IsDisposed;
            for (var index = 0; index < _raceQueue.Count; index++)
            {
                var request = _raceQueue[index];
                if (!selected.Contains(request.RequestId) || _controlRequestsInFlight.Contains(request.RequestId) ||
                    _startingRequests.Any(starting => IsSameRace(starting.Section, starting.ReleaseName, request.Section, request.ReleaseName)) ||
                    _activeJobs.Any(job => IsSameRace(job.Section, job.ReleaseName, request.Section, request.ReleaseName))) continue;
                var result = RaceControlPolicy.EditSite(request, serverId, add, available, IsNuked(serverId, request.ReleaseName), out var updated);
                if (!result.Accepted) continue;
                _raceQueue[index] = updated!;
                _controlRequestsInFlight.Add(request.RequestId);
                changed.Add(request.RequestId);
            }
        }
        if (changed.Count == 0) return new(0, selected.Count, true);
        var saved = PersistQueue();
        lock (_lock)
            foreach (var id in changed) _controlRequestsInFlight.Remove(id);
        _workers.TryRun(() => { DequeueNextRace(); return Task.CompletedTask; });
        return new(changed.Count, selected.Count - changed.Count, saved);
    }

    /// <summary>Remove only the selected still-queued identities, with no cancellation or cleanup side effects.</summary>
    public BulkRaceRemovalResult RemoveQueuedRequests(IEnumerable<string> requestIds)
    {
        var selected = requestIds.ToHashSet(StringComparer.Ordinal);
        int removed;
        lock (_lock)
        {
            if (_disposed) return new(0, selected.Count);
            removed = _raceQueue.RemoveAll(request => selected.Contains(request.RequestId) &&
                !request.HasOwnedPartial && !request.CleanupOnly && !_controlRequestsInFlight.Contains(request.RequestId) &&
                !_startingRequests.Any(starting => starting.RequestId == request.RequestId) &&
                !_activeRequests.Values.Any(active => active.RequestId == request.RequestId));
            if (removed > 0) ScheduleQueueWakeupNoLock();
        }
        if (removed > 0) PersistQueue();
        return new(removed, selected.Count - removed);
    }
}
