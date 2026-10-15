using System.IO;
using GlDrive.Downloads;
using GlDrive.Player;
using MonoTorrent;
using Serilog;

namespace GlDrive.Services;

public partial class ServerManager
{
    public const string TorrentDownloadQueueId = "__torrents";
    private readonly object _torrentDownloadLock = new();
    private DownloadManager? _torrentDownloads;
    private TorrentDownloadSource? _torrentDownloadSource;
    private bool _torrentDownloadsStopping;

    public DownloadManager? TorrentDownloads
    {
        get { lock (_torrentDownloadLock) return _torrentDownloads; }
    }

    // Explicit application startup keeps constructors and UI previews from starting
    // a saved queue. The backend itself remains lazy until an item needs metadata.
    public void StartTorrentDownloads()
    {
        lock (_torrentDownloadLock)
        {
            ObjectDisposedException.ThrowIf(_torrentDownloadsStopping, this);
            if (_torrentDownloads != null) return;
            if (_config.Servers.Any(s => s.Id == TorrentDownloadQueueId))
                throw new InvalidOperationException("The reserved torrent queue identifier is assigned to an FTP server.");
            var store = new DownloadStore(TorrentDownloadQueueId);
            store.Load();
            var source = new TorrentDownloadSource(_config);
            var manager = new DownloadManager(store, source, _config.Downloads,
                MountService.SharedDownloadDiskReservation);
            _torrentDownloadSource = source;
            _torrentDownloads = manager;
            manager.Start();
        }
    }

    public IReadOnlyList<(string Id, string Name, DownloadManager Manager)> GetDownloadQueues()
    {
        var queues = GetMountedServers().Where(s => s.Downloads != null)
            .Select(s => (Id: s.ServerId, Name: s.ServerName, Manager: s.Downloads!)).ToList();
        if (TorrentDownloads is { } torrents) queues.Add((TorrentDownloadQueueId, "BitTorrent", torrents));
        return queues;
    }

    public IReadOnlyList<DownloadManager> GetDownloadManagers() => GetDownloadQueues().Select(q => q.Manager).ToArray();

    public DownloadManager? GetDownloadManager(string queueId) =>
        queueId == TorrentDownloadQueueId ? TorrentDownloads : GetServer(queueId)?.Downloads;

    public DownloadItem EnqueueTorrentDownload(string magnetUri, string title, string destination,
        bool skipBlockedFiles = false)
    {
        if (!Path.IsPathFullyQualified(destination))
            throw new ArgumentException("Choose an absolute torrent destination directory.", nameof(destination));
        MagnetLink magnet;
        try { magnet = MagnetLink.Parse(magnetUri); }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        { throw new ArgumentException("The torrent magnet link is invalid.", nameof(magnetUri)); }
        var hash = magnet.InfoHashes.V1OrV2.ToHex();
        StartTorrentDownloads();
        var manager = TorrentDownloads!;
        var name = string.IsNullOrWhiteSpace(title) ? hash : title;
        var safeName = PathSanitizer.Sanitize(name);
        // An item owns a separate directory, including when the user picks an existing
        // library folder. Extraction must never scan unrelated files in that folder.
        var itemDirectory = Path.Combine(Path.GetFullPath(destination),
            safeName[..Math.Min(safeName.Length, 120)] + " [" + hash[..Math.Min(hash.Length, 12)] + "]");
        var item = new DownloadItem
        {
            SourceKind = DownloadSourceKind.Torrent, TorrentMagnet = magnetUri,
            RemotePath = hash, ReleaseName = name,
            LocalPath = itemDirectory, Category = "Torrents",
            ServerId = TorrentDownloadQueueId, ServerName = "BitTorrent",
            SkipBlockedTorrentFiles = skipBlockedFiles
        };
        if (manager.Enqueue(item)) return item;
        return manager.FindPendingOrActive(hash)
            ?? throw new InvalidOperationException("The existing torrent was removed while this download was being queued. Try again.");
    }

    public bool SkipBlockedTorrentFilesAndRetry(string id)
    {
        var manager = TorrentDownloads;
        var item = manager?.Store.GetById(id);
        if (item is not { SourceKind: DownloadSourceKind.Torrent, Status: DownloadStatus.Failed, RequiresContentApproval: true })
            return false;
        item.SkipBlockedTorrentFiles = true;
        item.RequiresContentApproval = false;
        manager!.Store.Update(item);
        manager.Retry(id);
        return true;
    }

    public void RefreshTorrentDownloadRate()
    {
        TorrentDownloadSource? source;
        lock (_torrentDownloadLock) source = _torrentDownloadSource;
        if (source != null) _ = ApplyRate(source);
        async Task ApplyRate(TorrentDownloadSource backend)
        {
            try { await backend.ApplyDownloadRateLimitAsync(TorrentStreamService.DownloadRateLimit(_config.Downloads)); }
            catch (Exception ex) { Log.Warning(ex, "Could not update the torrent download rate limit"); }
        }
    }

    private void StopTorrentDownloads()
    {
        DownloadManager? manager;
        TorrentDownloadSource? source;
        lock (_torrentDownloadLock)
        {
            if (_torrentDownloadsStopping) return;
            _torrentDownloadsStopping = true;
            manager = _torrentDownloads;
            source = _torrentDownloadSource;
        }
        if (manager == null || source == null) return;
        // Do not close stores or engine state underneath a writer. If shutdown is
        // slow, the owned drain continues and DownloadManager retains its store.
        var drain = Task.Run(async () =>
        {
            try
            {
                await manager.StopAsync();
                await source.DisposeAsync();
                await manager.StopAsync();
            }
            finally { manager.Dispose(); }
        });
        _ = drain.ContinueWith(failed => Log.Warning(failed.Exception, "Torrent download background shutdown failed"),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        try
        {
            if (!drain.Wait(TimeSpan.FromSeconds(15)))
                Log.Warning("Torrent download shutdown is still draining in the background");
        }
        catch (Exception ex) { Log.Warning(ex, "Torrent download shutdown failed"); }
    }
}
