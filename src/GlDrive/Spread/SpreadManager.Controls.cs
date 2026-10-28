namespace GlDrive.Spread;

public sealed record RaceControlResult(bool Accepted, string Code, string Message);

public partial class SpreadManager
{
    internal const string NukeHoldMessage = "Nuke safety hold: release is nuked";
    private readonly HashSet<string> _controlRequestsInFlight = new(StringComparer.Ordinal);
    /// <summary>Exact trusted nuke state. Failure to read it must not authorize new writes.</summary>
    public Func<string, string, bool>? IsReleaseNuked { get; set; }

    internal bool IsNuked(string serverId, string release)
    {
        try { return IsReleaseNuked?.Invoke(serverId, release) ?? false; }
        catch { return true; }
    }

    private bool IsRaceNuked(PersistedRace request) => request.ServerIds
        .Concat(request.Partial?.Directories.Select(d => d.ServerId) ?? [])
        .Any(id => IsNuked(id, request.ReleaseName));

    public void RefreshNukeHolds()
    {
        SpreadJob[] held;
        lock (_lock)
        {
            if (_disposed) return;
            held = _activeRequests.Where(pair => IsRaceNuked(pair.Value)).Select(pair => pair.Key).ToArray();
            ScheduleQueueWakeupNoLock();
        }
        foreach (var job in held) job.Stop();
        _workers.TryRun(() => { DequeueNextRace(); return Task.CompletedTask; });
    }

    public RaceControlResult ResetRace(string jobId)
    {
        lock (_lock)
        {
            if (_disposed) return new(false, "unavailable", "Spread engine is stopped");
            var job = _activeJobs.FirstOrDefault(job => job.Id == jobId);
            if (job == null) return new(false, "not-active", "Race is no longer active");
            if (!_activeRequests.TryGetValue(job, out var request))
                return new(false, "not-requested", "Race has already been stopped or released");
            if (IsRaceNuked(request))
                return new(false, "nuke-held", NukeHoldMessage);
            return job.RequestReset()
                ? new(true, "reset-pending", "Failure counters will reset and a rescan will run at the next scan boundary")
                : new(false, "not-running", "Only running or paused races can be reset");
        }
    }

    public RaceStartResult ReRace(string historyId, IReadOnlyList<string>? serverIds = null)
    {
        var history = _history.Items.FirstOrDefault(item => item.Id == historyId);
        if (history == null) return new(RaceStartOutcome.Unavailable, "", "", Reason: "Race history entry was not found");
        if (string.Equals(history.FailureCategory, "nuked", StringComparison.OrdinalIgnoreCase))
            return new(RaceStartOutcome.Unavailable, history.Section, history.ReleaseName, Reason: "Known nuked releases cannot be re-raced");
        if (serverIds == null)
        {
            lock (_lock) serverIds = _config.Servers.Where(server => server.Enabled && server.SpreadSite.Sections.Count > 0 &&
                _spreadPools.TryGetValue(server.Id, out var pool) && !pool.IsDisposed).Select(server => server.Id).ToArray();
        }
        return StartRaceDetailed(history.Section, history.ReleaseName, serverIds, history.Mode);
    }

    public RaceControlResult EditQueuedRaceSite(string section, string release, string serverId, bool add, string? expectedRequestId = null)
    {
        PersistedRace? previous = null, updated = null;
        lock (_lock)
        {
            if (_disposed) return new(false, "unavailable", "Spread engine is stopped");
            if (_activeJobs.Any(j => IsSameRace(j.Section, j.ReleaseName, section, release)) ||
                _startingRequests.Any(r => IsSameRace(r.Section, r.ReleaseName, section, release)))
                return new(false, "active-membership-unsupported", "Site membership can only change while the request is queued");
            var index = _raceQueue.FindIndex(r => IsSameRace(r.Section, r.ReleaseName, section, release));
            if (index < 0) return new(false, "not-queued", "Queued request was not found");
            previous = _raceQueue[index];
            if (expectedRequestId != null && previous.RequestId != expectedRequestId)
                return new(false, "request-changed", "The selected queued request has been replaced");
            if (_controlRequestsInFlight.Contains(previous.RequestId)) return new(false, "control-busy", "Another queue edit is being saved");
            var server = _config.Servers.FirstOrDefault(s => s.Id == serverId);
            var available = server is { Enabled: true } && server.SpreadSite.Sections.Count > 0 &&
                _spreadPools.TryGetValue(serverId, out var pool) && !pool.IsDisposed;
            var result = RaceControlPolicy.EditSite(previous, serverId, add, available, IsNuked(serverId, release), out updated);
            if (!result.Accepted) return result;
            _controlRequestsInFlight.Add(previous.RequestId);
            _raceQueue[index] = updated!;
        }
        if (!PersistQueue())
        {
            RestoreFailedControl(previous!, cleanup: false, addedSite: add ? serverId : null);
            return new(false, "save-failed", "Queue membership could not be saved");
        }
        lock (_lock) _controlRequestsInFlight.Remove(previous!.RequestId);
        _workers.TryRun(() => { DequeueNextRace(); return Task.CompletedTask; });
        return new(true, "queue-updated", add ? "Site added to queued request" : "Site removed from queued request");
    }

    public RaceControlResult RequestOwnedCleanupNow(string section, string release, bool confirmed)
    {
        if (!confirmed) return new(false, "confirmation-required", "Explicit confirmation is required to delete owned incomplete files");
        PersistedRace? previous, updated;
        lock (_lock)
        {
            if (_disposed) return new(false, "unavailable", "Spread engine is stopped");
            if (_activeJobs.Any(j => IsSameRace(j.Section, j.ReleaseName, section, release)) ||
                _startingRequests.Any(r => IsSameRace(r.Section, r.ReleaseName, section, release)))
                return new(false, "stop-and-wait", "Stop the race and wait for its transfers to finish before requesting cleanup");
            var index = _raceQueue.FindIndex(r => IsSameRace(r.Section, r.ReleaseName, section, release));
            if (index < 0) return new(false, "no-owned-partial", "No retained owned partial is available for cleanup");
            previous = _raceQueue[index];
            if (_controlRequestsInFlight.Contains(previous.RequestId)) return new(false, "control-busy", "Another queue edit is being saved");
            if (IsRaceNuked(previous)) return new(false, "nuke-held", NukeHoldMessage);
            var result = RaceControlPolicy.CleanupNow(previous, _config.Servers, AdmissionUtcNow(), out updated);
            if (!result.Accepted) return result;
            _controlRequestsInFlight.Add(previous.RequestId);
            _raceQueue[index] = updated!;
        }
        if (!PersistQueue())
        {
            RestoreFailedControl(previous, cleanup: true);
            return new(false, "save-failed", "Cleanup request could not be saved; no cleanup was started");
        }
        lock (_lock)
        {
            _controlRequestsInFlight.Remove(previous.RequestId);
            ScheduleQueueWakeupNoLock();
        }
        return new(true, "cleanup-queued", "Owned partial cleanup queued; completion, sharing and account checks remain mandatory");
    }

    internal void RestoreFailedControl(PersistedRace previous, bool cleanup, string? addedSite = null)
    {
        lock (_lock)
        {
            // Queue prediction and passive announce merges replace records while
            // persistence runs. Object reference identity is not request identity.
            var index = _raceQueue.FindIndex(r => r.RequestId == previous.RequestId);
            if (index >= 0)
            {
                var current = _raceQueue[index];
                _raceQueue[index] = cleanup ? current with
                {
                    CleanupOnly = previous.CleanupOnly, KeepUntilAvailable = previous.KeepUntilAvailable,
                    NextProbeAtUtc = previous.NextProbeAtUtc, WaitReason = previous.WaitReason,
                    StopReason = previous.StopReason, WasActive = previous.WasActive
                } : current with
                {
                    ServerIds = previous.ServerIds.Concat(current.ServerIds.Where(id => id != addedSite ||
                        current.SourceHints.Any(hint => hint.ServerId == id))).Distinct(StringComparer.Ordinal).ToList(),
                    SourceHints = current.SourceHints.Concat(previous.SourceHints).DistinctBy(hint => hint.ServerId).ToList()
                };
            }
            _controlRequestsInFlight.Remove(previous.RequestId);
        }
    }
}

internal static class RaceControlPolicy
{
    internal static RaceControlResult EditSite(PersistedRace request, string serverId, bool add,
        bool available, bool nuked, out PersistedRace? updated)
    {
        updated = null;
        if (request.CleanupOnly) return new(false, "cleanup-only", "Cleanup membership is fixed by recorded directory ownership");
        if (string.IsNullOrWhiteSpace(serverId)) return new(false, "invalid-site", "A configured site is required");
        var included = request.ServerIds.Contains(serverId, StringComparer.Ordinal);
        if (add == included) return new(false, "unchanged", add ? "Site already participates" : "Site does not participate");
        if (add && (!available || nuked)) return new(false, nuked ? "nuke-held" : "site-unavailable", "Site must be enabled, connected and free of a nuke hold");
        if (!add && request.Partial?.Directories.Any(d => d.ServerId == serverId) == true)
            return new(false, "owns-partial", "A site with retained directory ownership cannot be removed");
        if (!add && request.KnownSourceServerId == serverId)
            return new(false, "known-source", "The known source cannot be removed from a queued request");
        var ids = request.ServerIds.Distinct(StringComparer.Ordinal).ToList();
        if (add) ids.Add(serverId); else ids.Remove(serverId);
        if (ids.Count < 2) return new(false, "too-few-sites", "A race requires at least two participating sites");
        updated = request with
        {
            ServerIds = ids,
            SourceHints = request.SourceHints.Where(h => ids.Contains(h.ServerId, StringComparer.Ordinal)).ToList()
        };
        return new(true, "queue-updated", "Queued participant list updated");
    }

    internal static RaceControlResult CleanupNow(PersistedRace request, IReadOnlyList<GlDrive.Config.ServerConfig> servers,
        DateTime now, out PersistedRace? updated)
    {
        updated = null;
        if (!request.HasOwnedPartial || request.Partial!.Directories.All(d => d.CompletionConfirmed))
            return new(false, "no-owned-partial", "No owned incomplete directory is recorded");
        if (request.CleanupAttempts >= RaceLifetimePolicy.MaxCleanupAttempts)
            return new(false, "cleanup-budget-spent", "Cleanup retry budget is exhausted; ownership remains available for manual review");
        foreach (var directory in request.Partial.Directories)
        {
            var server = servers.FirstOrDefault(s => s.Id == directory.ServerId);
            if (server == null || !RaceLifetimePolicy.SafeRoot(directory.RemotePath, request.ReleaseName) ||
                !RaceLifetimePolicy.SameAccount(directory, server.Connection))
                return new(false, "ownership-unproven", "Cleanup requires the original account and an exact GlDrive-created release directory");
        }
        updated = RaceLifetimePolicy.CleanupRequest(request, now, "User-confirmed cleanup of owned partial requested") with
        { StopReason = RaceStopReason.UserStop.ToString() };
        return new(true, "cleanup-queued", "Owned partial cleanup queued");
    }
}
