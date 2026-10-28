using System.Text.Json;
using GlDrive.Config;

namespace GlDrive.Downloads;

public partial class WishlistStore
{
    private static void NormalizeQualityState(WishlistItem item)
    {
        item.ProfileId ??= ScoredQualityProfile.Legacy(item.Quality).Id;
        item.GrabbedReleases ??= [];
        item.BestOwned ??= [];
        item.PendingGrabs ??= [];
    }

    private static WishlistCopy CloneCopy(WishlistCopy copy) => new()
    {
        Key = copy.Key, ReleaseName = copy.ReleaseName, DownloadId = copy.DownloadId,
        ServerId = copy.ServerId, SizeBytes = copy.SizeBytes, LocalPath = copy.LocalPath,
        CompletedAt = copy.CompletedAt
    };

    internal WishlistQualityDecision EvaluateScoredRelease(string itemId, string release,
        DownloadConfig config, long? sizeBytes = null)
    {
        lock (_state.Sync)
        {
            EnsureLoaded();
            return EvaluateScoredReleaseLocked(itemId, release, config, sizeBytes);
        }
    }

    private WishlistQualityDecision EvaluateScoredReleaseLocked(string itemId, string release,
        DownloadConfig config, long? sizeBytes)
    {
        var item = _state.Items.FirstOrDefault(i => i.Id == itemId);
        if (item == null) return new(false, 0, false, "wishlist-item-not-found", null, []);
        var profile = WishlistAcquisition.ResolveProfile(item, config);
        if (profile == null) return new(false, 0, false, "quality-profile-not-found", item.ProfileId, []);
        var candidate = ReleaseScorer.Score(release, profile, sizeBytes);
        var keys = WishlistAcquisition.Keys(item, release);
        WishlistQualityDecision Decision(bool accepted, string reason) =>
            new(accepted, candidate.Score, candidate.MeetsCutoff, reason, profile.Id, keys);
        if (item.Dead || item.Status == WishlistStatus.Paused) return Decision(false, "wishlist-item-inactive");
        if (item.Status == WishlistStatus.Completed && !profile.UpgradeProperRepackAfterCutoff)
            return Decision(false, "wishlist-cutoff-met");
        if (!WishlistAcquisition.MatchesTitle(item, release)) return Decision(false, "title-or-year-mismatch");
        if (keys.Count == 0) return Decision(false, "unknown-episode-identity");
        if (!candidate.Accepted) return Decision(false, candidate.Reason);

        foreach (var other in _state.Items.Where(other => WishlistAcquisition.SameTitle(item, other)))
        {
            var copies = other.BestOwned.Concat(other.PendingGrabs).ToArray();
            // Derive keys under the current identity: adding an IMDb/TVMaze id
            // later must not strand evidence persisted under a title identity.
            foreach (var copy in copies.Where(c => keys.Any(k =>
                         WishlistAcquisition.Overlaps(k, WishlistAcquisition.CurrentKey(item, c)))))
            {
                if (string.Equals(copy.ReleaseName, release, StringComparison.OrdinalIgnoreCase))
                    return Decision(false, copy.CompletedAt.HasValue ? "already-owned" : "already-pending");
                var previous = ReleaseScorer.Score(copy.ReleaseName, profile, copy.SizeBytes);
                if (!ReleaseScorer.IsImprovement(candidate, previous, profile))
                    return Decision(false, copy.CompletedAt.HasValue ? "not-better-than-owned" : "not-better-than-pending");
            }
            // Older versions recorded enqueue names without a completion result.
            // They remain conservative grab evidence, never fabricated ownership.
            foreach (var previousRelease in other.GrabbedReleases.Where(r => !copies.Any(c =>
                         string.Equals(c.ReleaseName, r, StringComparison.OrdinalIgnoreCase))))
            {
                if (!WishlistAcquisition.Keys(item, previousRelease).Any(k => keys.Any(c => WishlistAcquisition.Overlaps(k, c)))) continue;
                if (string.Equals(previousRelease, release, StringComparison.OrdinalIgnoreCase) ||
                    !ReleaseScorer.IsImprovement(candidate, ReleaseScorer.Score(previousRelease, profile), profile))
                    return Decision(false, "not-better-than-previous-grab");
            }
        }
        if (item.Status == WishlistStatus.Completed && item.BestOwned.Count == 0)
            return Decision(false, "completed-without-owned-evidence");
        return Decision(true, candidate.Reason);
    }

    internal WishlistQualityDecision TryReserveScoredGrab(string itemId, DownloadItem download,
        DownloadConfig config, long? sizeBytes = null)
    {
        lock (_state.Sync)
        {
            EnsureLoaded();
            var decision = EvaluateScoredReleaseLocked(itemId, download.ReleaseName, config, sizeBytes);
            if (!decision.Accepted) return decision;
            var item = _state.Items.First(i => i.Id == itemId);
            foreach (var key in decision.Keys)
                item.PendingGrabs.Add(CopyFor(download, key, sizeBytes));
            _state.AdmissionsInFlight.Add(download.Id);
            try
            {
                // Claim persistence must succeed before a second queue can observe
                // it or the caller starts a download. Ordinary Save logs errors.
                _state.Write(JsonSerializer.Serialize(_state.Items, JsonOptions));
            }
            catch
            {
                item.PendingGrabs.RemoveAll(c => c.DownloadId == download.Id);
                _state.AdmissionsInFlight.Remove(download.Id);
                throw;
            }
            return decision;
        }
    }

    internal void ReleaseScoredReservation(string itemId, string downloadId, string release)
    {
        lock (_state.Sync)
        {
            EnsureLoaded();
            var item = _state.Items.FirstOrDefault(i => i.Id == itemId);
            _state.AdmissionsInFlight.Remove(downloadId);
            if (item == null) return;
            item.PendingGrabs.RemoveAll(c => c.DownloadId == downloadId);
            ForgetFailedGrab(item, release);
            Save();
        }
    }

    internal void ConfirmScoredGrab(string itemId, string downloadId, string release)
    {
        lock (_state.Sync)
        {
            EnsureLoaded();
            var item = _state.Items.FirstOrDefault(i => i.Id == itemId);
            _state.AdmissionsInFlight.Remove(downloadId);
            // A fast terminal callback can have already failed the reservation.
            if (item != null && item.PendingGrabs.Concat(item.BestOwned).Any(c => c.DownloadId == downloadId))
                RecordGrab(itemId, release);
        }
    }

    internal void ObserveDownloadStatus(DownloadItem download, DownloadConfig config)
    {
        if (download.WishlistItemId == null) return;
        lock (_state.Sync)
        {
            EnsureLoaded();
            var item = _state.Items.FirstOrDefault(i => i.Id == download.WishlistItemId);
            if (item == null) return;
            _state.AdmissionsInFlight.Remove(download.Id);
            if (!WishlistAcquisition.MatchesTitle(item, download.ReleaseName)) return;
            if (!config.ScoredQualityEnabled && !item.PendingGrabs.Any(c => c.DownloadId == download.Id)) return;
            var keys = WishlistAcquisition.Keys(item, download.ReleaseName);
            if (download.Status is DownloadStatus.Failed or DownloadStatus.Cancelled)
            {
                item.PendingGrabs.RemoveAll(c => c.DownloadId == download.Id);
                ForgetFailedGrab(item, download.ReleaseName);
            }
            else if (download.Status == DownloadStatus.Completed)
            {
                var profile = WishlistAcquisition.ResolveProfile(item, config);
                foreach (var key in keys)
                {
                    var copy = CopyFor(download, key, download.TotalBytes > 0 ? download.TotalBytes : null);
                    copy.CompletedAt = download.CompletedAt ?? DateTime.UtcNow;
                    var existing = item.BestOwned.FirstOrDefault(c => WishlistAcquisition.CurrentKey(item, c) == key);
                    if (existing == null || profile != null &&
                        IsBetterOwned(copy, existing, profile))
                    {
                        item.BestOwned.RemoveAll(c => c.Key == key || ReferenceEquals(c, existing));
                        item.BestOwned.Add(copy);
                    }
                }
                item.PendingGrabs.RemoveAll(c => c.DownloadId == download.Id);
                AddGrab(item, download.ReleaseName);
                if (item.Type == MediaType.Movie && item.Status == WishlistStatus.Watching && profile != null &&
                    item.BestOwned.Any(c => ReleaseScorer.Score(c.ReleaseName, profile, c.SizeBytes).MeetsCutoff))
                    item.Status = WishlistStatus.Completed;
            }
            else
            {
                // Retry is an explicit user action. Track its pending work even if
                // another candidate was acquired after the earlier failure.
                foreach (var key in keys.Where(k => !item.PendingGrabs.Any(c => c.DownloadId == download.Id && c.Key == k)))
                    item.PendingGrabs.Add(CopyFor(download, key, download.TotalBytes > 0 ? download.TotalBytes : null));
                AddGrab(item, download.ReleaseName);
            }
            Save();
        }
    }

    internal void ReconcileDownloads(string serverId, Func<IReadOnlyList<DownloadItem>> snapshot, DownloadConfig config)
    {
        // Take the authoritative queue snapshot while admissions are excluded.
        // A snapshot made before taking this lock could miss a just-confirmed grab.
        lock (_state.Sync) ReconcileDownloads(serverId, snapshot(), config);
    }

    internal void ReconcileDownloads(string serverId, IReadOnlyList<DownloadItem> downloads, DownloadConfig config)
    {
        lock (_state.Sync)
        {
            EnsureLoaded();
            var ids = downloads.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var item in _state.Items)
            {
                var missing = item.PendingGrabs.Where(c => c.ServerId == serverId && !ids.Contains(c.DownloadId)
                    && !_state.AdmissionsInFlight.Contains(c.DownloadId)).ToArray();
                foreach (var copy in missing)
                {
                    item.PendingGrabs.Remove(copy);
                    ForgetFailedGrab(item, copy.ReleaseName);
                }
            }
            foreach (var download in downloads) ObserveDownloadStatus(download, config);
            Save();
        }
    }

    private static WishlistCopy CopyFor(DownloadItem download, string key, long? sizeBytes) => new()
    {
        Key = key, ReleaseName = download.ReleaseName, DownloadId = download.Id,
        ServerId = download.ServerId, SizeBytes = sizeBytes, LocalPath = download.LocalPath
    };

    private static bool IsBetterOwned(WishlistCopy candidate, WishlistCopy existing, ScoredQualityProfile profile)
    {
        var score = ReleaseScorer.Score(candidate.ReleaseName, profile, candidate.SizeBytes);
        var previous = ReleaseScorer.Score(existing.ReleaseName, profile, existing.SizeBytes);
        // Once actually downloaded, keep the best evidence even if it arrived
        // after cutoff (for example an explicit manual download). Revision
        // corrections may have the same numeric score as their predecessor.
        return ReleaseScorer.IsBetterCopy(score, previous, profile);
    }

    private static void AddGrab(WishlistItem item, string release)
    {
        if (!item.GrabbedReleases.Contains(release, StringComparer.OrdinalIgnoreCase)) item.GrabbedReleases.Add(release);
    }

    private void ForgetFailedGrab(WishlistItem item, string release)
    {
        if (!item.PendingGrabs.Concat(item.BestOwned).Any(c => string.Equals(c.ReleaseName, release, StringComparison.OrdinalIgnoreCase)))
            item.GrabbedReleases.RemoveAll(r => string.Equals(r, release, StringComparison.OrdinalIgnoreCase));
        _state.ReleaseEvaluations.ForgetItem(item.Id);
        _state.ReleaseEvaluations.Forget(item.Id, release);
    }
}
