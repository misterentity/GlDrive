using System.Threading.Channels;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Spread;
using Serilog;

namespace GlDrive.Library;

/// <summary>Consumes existing observations only. No timer, provider client, scan or FTP operation.</summary>
public sealed class NukeWatch : IDisposable
{
    private sealed record Signal(OwnedReleaseObservation? Owned = null, NukeWatchEvidence? Evidence = null,
        bool Reconcile = false, TaskCompletionSource? Barrier = null);
    private readonly AppConfig _config;
    private readonly Func<IReadOnlyList<DownloadItem>> _downloads;
    private readonly Func<IReadOnlyList<SitebotReleaseFacts>> _facts;
    private readonly Func<string, WishlistItem?> _wishlist;
    private readonly Func<OwnedReleaseObservation, SitebotEvent, DownloadItem?> _enqueue;
    private readonly Channel<Signal> _queue;
    private readonly Lock _admission = new();
    private readonly Task _worker;
    private int _disposed;
    private int _reconcileQueued;
    private long _dropped;
    public NukeWatchStore Store { get; }
    public long Dropped => Interlocked.Read(ref _dropped);
    public event Action<OwnedNukeEntry>? Flagged;

    public NukeWatch(AppConfig config, NukeWatchStore store, Func<IReadOnlyList<DownloadItem>> downloads,
        Func<IReadOnlyList<SitebotReleaseFacts>> facts, Func<string, WishlistItem?> wishlist,
        Func<OwnedReleaseObservation, SitebotEvent, DownloadItem?> enqueue)
    {
        _config = config; Store = store; _downloads = downloads; _facts = facts; _wishlist = wishlist; _enqueue = enqueue;
        _queue = Channel.CreateBounded<Signal>(new BoundedChannelOptions(Math.Clamp(config.NukeWatch.QueueCapacity, 1, 4096))
        { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest }, dropped =>
        {
            if (dropped.Reconcile) Interlocked.Exchange(ref _reconcileQueued, 0);
            dropped.Barrier?.TrySetResult();
            Interlocked.Increment(ref _dropped);
        });
        using var flow = ExecutionContext.IsFlowSuppressed() ? default : ExecutionContext.SuppressFlow();
        _worker = Task.Run(RunAsync);
    }

    public void QueueDownloadReconcile()
    {
        if (Volatile.Read(ref _disposed) != 0 || !_config.NukeWatch.Enabled || Interlocked.Exchange(ref _reconcileQueued, 1) != 0) return;
        if (!_queue.Writer.TryWrite(new(Reconcile: true))) Interlocked.Exchange(ref _reconcileQueued, 0);
    }

    public void ObserveOwned(OwnedReleaseObservation owned) => Publish(new(Owned: owned));

    public void ObservePreDb(IEnumerable<PreDbRelease> releases)
    {
        if (!_config.NukeWatch.Enabled || Volatile.Read(ref _disposed) != 0) return;
        foreach (var release in releases)
            if (release is { IsNuked: true } && NukeWatchStore.ValidRelease(release.Release))
                Publish(new(Evidence: new(release.Release, "predb", ClampReason(release.Reason), DateTime.UtcNow)));
        // A non-nuked status or provider failure is unknown, never an UNNUKE.
    }

    public void ObserveSitebot(SitebotEvent observation)
    {
        if (observation.Kind is SitebotEventKind.Nuke or SitebotEventKind.Unnuke)
            Publish(new(Evidence: new(observation.Release, "site:" + observation.ServerId,
                ClampReason(observation.Reason), observation.ObservedAtUtc, observation.Kind == SitebotEventKind.Nuke)));
        else if (observation.Kind == SitebotEventKind.Complete) Publish(new());
    }

    private static string ClampReason(string? reason) => (reason ?? "")[..Math.Min(reason?.Length ?? 0, 1024)];
    private void Publish(Signal signal)
    {
        if (Volatile.Read(ref _disposed) == 0 && _config.NukeWatch.Enabled) _queue.Writer.TryWrite(signal);
    }

    public Task DrainAsync()
    {
        if (Volatile.Read(ref _disposed) != 0) return _worker;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.Writer.TryWrite(new(Barrier: done))) done.TrySetResult();
        return done.Task;
    }

    private async Task RunAsync()
    {
        await foreach (var signal in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (signal.Reconcile) Interlocked.Exchange(ref _reconcileQueued, 0);
            if (signal.Barrier != null) { signal.Barrier.TrySetResult(); continue; }
            if (Volatile.Read(ref _disposed) != 0 || !_config.NukeWatch.Enabled) continue;
            try
            {
                var before = Store.Snapshot().Where(e => e.Flagged).Select(e => e.Owned.ReleaseName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (signal.Reconcile) ReconcileDownloads();
                if (signal.Owned != null) Store.ObserveOwned(signal.Owned);
                if (signal.Evidence != null) Store.ObserveNuke(signal.Evidence);
                foreach (var entry in Store.Snapshot().Where(e => e.Flagged && !before.Contains(e.Owned.ReleaseName)))
                {
                    try { Flagged?.Invoke(entry); }
                    catch (Exception ex) { Log.Debug(ex, "Owned-release nuke notification failed"); }
                }
                ProcessCorrections();
            }
            catch (Exception ex) { Log.Warning(ex, "Nuke watch observation failed; normal work continues"); }
        }
    }

    private void ReconcileDownloads()
    {
        foreach (var download in _downloads())
        {
            if (download.Status == DownloadStatus.Completed && download.CompletedAt.HasValue)
                Store.ObserveOwned(new(download.ReleaseName, download.LocalPath, download.CompletedAt.Value, download.WishlistItemId));
            if (!string.IsNullOrWhiteSpace(download.NukeCorrectionForRelease))
                Store.RecordAttempt(new(download.NukeCorrectionForRelease, download.ReleaseName, download.Id, download.QueuedAt,
                    download.Status == DownloadStatus.Completed));
        }
    }

    private void ProcessCorrections()
    {
        if (!_config.NukeWatch.AutoQueueFixes || !_config.NukeWatch.Enabled || Volatile.Read(ref _disposed) != 0) return;
        var downloads = _downloads();
        var completions = _facts().Where(f => f.Complete != null).Select(f => f.Complete!).ToArray();
        foreach (var entry in Store.Snapshot().Where(e => e.Flagged && e.QualityNuke))
        {
            if (entry.Attempts.Any(a => a.Completed) || downloads.Any(d => NukeWatchStore.Equal(d.NukeCorrectionForRelease, entry.Owned.ReleaseName) &&
                    d.Status is DownloadStatus.Queued or DownloadStatus.Downloading or DownloadStatus.Extracting or DownloadStatus.Paused or DownloadStatus.Completed)) continue;
            var wanted = entry.Owned.WishlistItemId is { } id ? _wishlist(id) : null;
            if (wanted is { Dead: true } || wanted?.Status == WishlistStatus.Paused) continue;
            var policyItem = wanted ?? PolicyItem(entry.Owned.ReleaseName);
            if (policyItem == null) continue;
            var profile = WishlistAcquisition.ResolveProfile(policyItem, _config.Downloads);
            if (profile == null) continue;
            foreach (var completion in completions.OrderByDescending(c => c.ObservedAtUtc).ThenBy(c => c.ServerId, StringComparer.Ordinal))
            {
                if (Store.IsCandidateNuked(completion.ServerId, completion.Release) || Store.WasAttempted(entry.Owned.ReleaseName, completion.Release) ||
                    !WishlistSourceFloor.Allows(policyItem, completion.Release, _config.Downloads) ||
                    !NukeWatchPolicy.IsCorrection(entry.Owned.ReleaseName, completion.Release, profile, completion.Bytes)) continue;
                if (downloads.Any(d => NukeWatchStore.Equal(d.ReleaseName, completion.Release) &&
                        (NukeWatchStore.Equal(d.NukeCorrectionForRelease, entry.Owned.ReleaseName) ||
                         d.Status is DownloadStatus.Queued or DownloadStatus.Downloading or DownloadStatus.Extracting or DownloadStatus.Paused or DownloadStatus.Completed))) continue;
                lock (_admission)
                {
                    if (_disposed != 0 || !_config.NukeWatch.Enabled || !_config.NukeWatch.AutoQueueFixes) return;
                    var queued = _enqueue(entry.Owned, completion);
                    if (queued == null) continue;
                    Store.RecordAttempt(new(entry.Owned.ReleaseName, completion.Release, queued.Id, queued.QueuedAt));
                    downloads = _downloads();
                    break;
                }
            }
        }
    }

    internal static WishlistItem? PolicyItem(string release) => NukeWatchPolicy.PolicyItem(release);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.Writer.TryComplete();
    }
}
