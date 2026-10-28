namespace GlDrive.Spread;

public partial class SpreadManager
{
    private void SavePartialState(SpreadJob job, PersistedRace? original)
    {
        if (original == null) return;
        var partial = job.CapturePartialState();
        lock (_lock)
        {
            var updated = (_activeRequests.GetValueOrDefault(job) ?? original) with
            {
                Partial = partial, StopReason = job.StopReason == RaceStopReason.None ? original.StopReason : job.StopReason.ToString()
            };
            if (_disposed && _shutdownQueueSnapshot != null)
            {
                var index = _shutdownQueueSnapshot.FindIndex(r => IsSameRace(r.Section, r.ReleaseName, job.Section, job.ReleaseName));
                if (index >= 0) _shutdownQueueSnapshot[index] = updated with { WasActive = true };
            }
            else if (_activeRequests.ContainsKey(job)) _activeRequests[job] = updated;
            else if (updated.HasOwnedPartial && job.State == SpreadJobState.Stopped)
            {
                _raceQueue.RemoveAll(r => IsSameRace(r.Section, r.ReleaseName, job.Section, job.ReleaseName));
                _raceQueue.Add(RaceLifetimePolicy.CleanupRequest(updated, DateTime.UtcNow,
                    "Stopped by user; owned partial retained until cleanup deadline",
                    RaceLifetimePolicy.Deadline(partial, _config.Spread.PartialRaceDeadlineHours)));
            }
        }
        if (!PersistQueue())
        {
            PartialCleanupNotice?.Invoke(job.ReleaseName,
                "Race ownership could not be saved; further uploads stopped to preserve cleanup safety");
            job.Stop(RaceStopReason.Stall);
            throw new System.IO.IOException("Cannot safely upload: partial ownership could not be persisted");
        }
    }

    private bool CancelQueuedNoLock(string section, string release)
    {
        var matches = _raceQueue.Where(r => IsSameRace(r.Section, r.ReleaseName, section, release) &&
            !_controlRequestsInFlight.Contains(r.RequestId)).ToList();
        _raceQueue.RemoveAll(r => matches.Any(match => match.RequestId == r.RequestId));
        foreach (var request in matches.Where(r => r.HasOwnedPartial))
            _raceQueue.Add(RaceLifetimePolicy.CleanupRequest(request, DateTime.UtcNow,
                "Stopped by user; owned partial retained until cleanup deadline",
                RaceLifetimePolicy.Deadline(request.Partial, _config.Spread.PartialRaceDeadlineHours))
                with { StopReason = RaceStopReason.UserStop.ToString() });
        return matches.Count > 0;
    }

    internal PersistedRace? NextRequest(PersistedRace request, SpreadJob job, DateTime now,
        bool deferredForPools, bool stillRequested)
    {
        var deadline = RaceLifetimePolicy.Deadline(request.Partial, _config.Spread.PartialRaceDeadlineHours);
        if (request.CleanupOnly)
            return request.HasOwnedPartial ? RaceLifetimePolicy.RetryCleanup(request, now) : null;
        if (request.HasOwnedPartial && (deadline is { } at && now >= at
                || job.StopReason is RaceStopReason.SourceGone or RaceStopReason.Deadline))
            return RaceLifetimePolicy.CleanupRequest(request, now, "Partial cannot be finished; cleaning owned directory");
        if (request.HasOwnedPartial && (!stillRequested || job.StopReason == RaceStopReason.UserStop))
            return RaceLifetimePolicy.CleanupRequest(request, now,
                "Stopped by user; owned partial retained until cleanup deadline", deadline);
        if (!stillRequested) return null;
        if (deferredForPools) return request;
        if (request.HasOwnedPartial && job.State == SpreadJobState.Failed
            && (job.LastError?.Contains("Source probe inconclusive", StringComparison.OrdinalIgnoreCase) == true
                || job.LastError?.Contains("Source scan never succeeded", StringComparison.OrdinalIgnoreCase) == true))
            return request.Defer(now, "Source unavailable; retaining partial until it can be checked", true, false);
        if (job.State == SpreadJobState.Waiting)
            return request.Defer(now, job.WaitReason ?? "Waiting for remaining release files",
                job.WaitingForIncompleteRelease, job.Sites.Values.Any(s => s.BytesTransferred > 0));
        if (job.State == SpreadJobState.Stopped && request.Partial?.HasLandedPayload == true
            && RaceLifetimePolicy.CanResume(job.StopReason, request.ResumeCount, deadline, now))
            return request with
            {
                WasActive = false, ResumeCount = request.ResumeCount + 1,
                NextProbeAtUtc = now.AddSeconds(30), KeepUntilAvailable = false,
                WaitReason = $"Resuming partial after {job.StopReason} ({request.ResumeCount + 1}/{RaceLifetimePolicy.MaxPartialResumes})"
            };
        if (request.HasOwnedPartial)
            return RaceLifetimePolicy.CleanupRequest(request, now, "Resume budget spent or race failed; cleaning owned partial");
        return null;
    }
}
