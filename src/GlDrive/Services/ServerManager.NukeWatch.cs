using System.IO;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Library;
using GlDrive.Spread;
using GlDrive.Util;

namespace GlDrive.Services;

public partial class ServerManager
{
    public NukeWatch NukeWatch { get; private set; } = null!;

    private void InitializeNukeWatch()
    {
        var store = new NukeWatchStore(Path.Combine(ConfigManager.AppDataPath, "owned-nuke-watch.json"), () => _config.NukeWatch);
        NukeWatch = new(_config, store, () => GetDownloadManagers().SelectMany(m => m.Store.Items).ToArray(),
            () => _spreadManager?.SitebotFacts.Snapshot() ?? [], id => _wantedWishlist.GetById(id), QueueNukeCorrection);
    }

    private void CompletedDownloadObserved()
    {
        Watchlist.QueueOwnershipReconcile();
        NukeWatch.QueueDownloadReconcile();
    }

    private void ObserveOwnedReleaseSitebot(SitebotEvent observation)
    {
        if (_disposed) return;
        var facts = _spreadManager?.SitebotFacts.TryGet(observation.ServerId, observation.Release);
        if (facts?.Latest?.ObservedAtUtc != observation.ObservedAtUtc || facts.Latest.Kind != observation.Kind) return;
        NukeWatch.ObserveSitebot(observation);
    }

    private DownloadItem? QueueNukeCorrection(OwnedReleaseObservation owned, SitebotEvent complete)
    {
        DownloadItem? queued = null;
        SecureFile.WithPathLock(ConfigManager.ConfigPath, () =>
        {
            if (_disposed || !_config.NukeWatch.Enabled || !_config.NukeWatch.AutoQueueFixes ||
                !_servers.TryGetValue(complete.ServerId, out var mount) || mount.CurrentState != MountState.Connected || mount.Downloads is not { } manager) return;
            var server = _config.Servers.FirstOrDefault(s => s.Id == complete.ServerId && s.Enabled);
            if (server == null) return;
            var wanted = owned.WishlistItemId is { } id ? _wantedWishlist.GetById(id) : null;
            if (wanted is { Dead: true } || wanted?.Status == WishlistStatus.Paused) return;
            var policyItem = wanted ?? GlDrive.Library.NukeWatch.PolicyItem(owned.ReleaseName);
            if (policyItem == null || !WishlistSourceFloor.Allows(policyItem, complete.Release, _config.Downloads)) return;
            var profile = WishlistAcquisition.ResolveProfile(policyItem, _config.Downloads);
            if (profile == null || !NukeWatchPolicy.IsCorrection(owned.ReleaseName, complete.Release, profile, complete.Bytes)) return;
            var path = WishlistIrcRelease.ResolvePath(server, complete.Section, complete.Release);
            if (path == null) return;
            var destination = DownloadPathResolver.ForScene(_config.Downloads, complete.Section, complete.Release);
            if (!Path.IsPathFullyQualified(destination) || SfvRepair.SamePath(destination, owned.LocalPath)) return;
            var item = new DownloadItem
            {
                ReleaseName = complete.Release, RemotePath = path, LocalPath = destination, Category = complete.Section,
                ServerId = server.Id, ServerName = server.Name, TotalBytes = complete.Bytes ?? 0,
                WishlistItemId = wanted?.Id, BackgroundWantedPull = true, NukeCorrectionForRelease = owned.ReleaseName
            };
            bool Eligible()
            {
                if (_disposed || !_config.NukeWatch.Enabled || !_config.NukeWatch.AutoQueueFixes ||
                    !_servers.TryGetValue(server.Id, out var current) || !ReferenceEquals(current, mount) ||
                    current.CurrentState != MountState.Connected || !server.Enabled ||
                    _nukes.IsNuked(server.Id, complete.Release) || NukeWatch.Store.IsCandidateNuked(server.Id, complete.Release) ||
                    _spreadManager?.SitebotFacts.TryGet(server.Id, complete.Release)?.Complete?.ObservedAtUtc != complete.ObservedAtUtc) return false;
                return !GetDownloadManagers().SelectMany(m => m.Store.Items).Any(d =>
                    NukeWatchStore.Equal(d.ReleaseName, complete.Release) &&
                    (NukeWatchStore.Equal(d.NukeCorrectionForRelease, owned.ReleaseName) ||
                     d.Status is DownloadStatus.Queued or DownloadStatus.Downloading or DownloadStatus.Extracting or DownloadStatus.Paused or DownloadStatus.Completed));
            }
            if (manager.TryEnqueueCorrection(item, Eligible)) queued = item;
        });
        return queued;
    }
}
