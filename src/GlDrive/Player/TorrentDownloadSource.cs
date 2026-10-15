using System.IO;
using GlDrive.Config;
using GlDrive.Downloads;
using MonoTorrent;
using MonoTorrent.Client;
using DownloadProgress = GlDrive.Downloads.DownloadProgress;

namespace GlDrive.Player;

internal sealed class TorrentContentBlockedException(
    string message, IReadOnlyList<TorrentFileDecision> decisions, bool canSkipFiles)
    : DownloadBlockedException(message, canSkipFiles)
{
    public IReadOnlyList<TorrentFileDecision> Decisions { get; } = decisions;
}

/// <summary>A whole-item backend; MonoTorrent owns pieces and integrity, DownloadManager owns the queue.</summary>
internal sealed class TorrentDownloadSource : IWholeDownloadSource, IDisposable, IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly Lazy<ITorrentDownloadBackend> _backend;
    private readonly CancellationTokenSource _stop = new();
    private readonly HashSet<Task> _preparing = [];
    private readonly HashSet<Prepared> _sessions = [];
    private readonly TimeSpan _pollInterval;
    private Task? _disposeTask;
    private bool _disposed;

    public TorrentDownloadSource(AppConfig config)
        : this(() => new TorrentDownloadBackend(new TorrentStreamService(
            Path.Combine(ConfigManager.AppDataPath, "torrent-downloads"),
            config.Torrent.VpnAdapterName, new TorrentContentPolicy(config.Torrent),
            TorrentStreamService.DownloadRateLimit(config.Downloads)))) { }

    internal TorrentDownloadSource(Func<ITorrentDownloadBackend> backend, TimeSpan? pollInterval = null)
    {
        _backend = new Lazy<ITorrentDownloadBackend>(backend);
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(1);
    }

    public Task<IPreparedDownload> PrepareAsync(DownloadItem item, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var task = PrepareCoreAsync(item, progress, ct);
            _preparing.Add(task);
            _ = task.ContinueWith(t => { lock (_sync) _preparing.Remove(t); },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }
    }

    private async Task<IPreparedDownload> PrepareCoreAsync(DownloadItem item, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        var token = linked.Token;
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(item.TorrentMagnet))
            throw new DownloadBlockedException("The saved torrent request has no magnet link.");
        if (!Path.IsPathFullyQualified(item.LocalPath))
            throw new DownloadBlockedException("The torrent destination must be an absolute directory.");
        // Parse failures never include the original magnet (which can contain private tracker credentials).
        MagnetLink magnet;
        try { magnet = MagnetLink.Parse(item.TorrentMagnet); }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        { throw new DownloadBlockedException("The saved torrent magnet link is invalid."); }
        var hash = magnet.InfoHashes.V1OrV2.ToHex();
        if (!string.Equals(item.RemotePath, hash, StringComparison.OrdinalIgnoreCase))
            throw new DownloadBlockedException("The saved torrent identity does not match its magnet link.");

        progress?.Report(new DownloadProgress(0, 0, 0, StatusDetail: "Fetching torrent metadata"));
        var result = await _backend.Value.ScreenAsync(magnet, item.LocalPath,
            status => progress?.Report(new DownloadProgress(0, 0, 0, StatusDetail: status)), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!result.Ok) throw new IOException(result.Error ?? "Torrent metadata was unavailable.");
        var torrent = result.Torrent!;
        if (!torrent.InfoHashes.Contains(magnet.InfoHashes.V1OrV2))
            throw new DownloadBlockedException("Torrent metadata does not match the requested identity.");
        var blocked = result.Decisions.Where(d => d.IsBlocked).ToArray();
        if (result.HasEscapes)
            throw new TorrentContentBlockedException("Torrent paths escape the selected destination; this cannot be overridden.", blocked, false);
        if (blocked.Length > 0 && !item.SkipBlockedTorrentFiles)
            throw new TorrentContentBlockedException("Torrent contains blocked file types. Explicit approval is required to download only the remaining files.", blocked, true);
        if (result.Decisions.Count != torrent.Files.Count)
            throw new DownloadBlockedException("Torrent screening did not cover every declared file.");
        var selected = torrent.Files.Where((_, i) => !result.Decisions[i].IsBlocked).ToArray();
        if (selected.Length == 0)
            throw new TorrentContentBlockedException("No permitted files remain in this torrent.", blocked, false);
        var total = selected.Sum(f => f.Length);
        var root = Path.GetFullPath(item.LocalPath);
        var directories = selected.Select(f => Path.GetDirectoryName(Path.GetFullPath(Path.Combine(root, f.Path)))!)
            .Append(root).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var session = new Prepared(this, _backend.Value, torrent, result.Decisions, root, total, directories, _pollInterval);
        lock (_sync)
        {
            token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            _sessions.Add(session);
        }
        progress?.Report(new DownloadProgress(0, total, 0, StatusDetail: "Metadata screened; waiting to download"));
        return session;
    }

    public Task ApplyDownloadRateLimitAsync(long bytesPerSecond)
    {
        lock (_sync)
            return !_disposed && _backend.IsValueCreated
                ? _backend.Value.SetRateAsync(bytesPerSecond) : Task.CompletedTask;
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposeTask != null) return new ValueTask(_disposeTask);
            _disposed = true;
            var preparing = _preparing.ToArray();
            var sessions = _sessions.ToArray();
            // Never enter session cleanup while holding the owner lock: a normally
            // finishing session takes that lock to unregister itself.
            _disposeTask = Task.Run(() => DisposeCoreAsync(preparing, sessions));
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync(Task[] preparing, Prepared[] sessions)
    {
        _stop.Cancel();
        try { await Task.WhenAll(preparing).ConfigureAwait(false); } catch { /* callers own preparation failures */ }
        try { await Task.WhenAll(sessions.Select(s => s.DisposeAsync().AsTask())).ConfigureAwait(false); }
        finally
        {
            if (_backend.IsValueCreated) await _backend.Value.DisposeAsync().ConfigureAwait(false);
            _stop.Dispose();
        }
    }

    private sealed class Prepared(TorrentDownloadSource owner, ITorrentDownloadBackend backend,
        Torrent torrent, IReadOnlyList<TorrentFileDecision> decisions, string root, long total,
        IReadOnlyList<string> directories, TimeSpan pollInterval) : IPreparedDownload
    {
        private readonly object _sync = new();
        private readonly CancellationTokenSource _stop = new();
        private Task? _execution;
        private Task? _disposal;
        public long TotalBytes => total;
        public IReadOnlyList<string> Directories => directories;

        public Task ExecuteAsync(IProgress<DownloadProgress>? progress, CancellationToken ct)
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposal != null, this);
                if (_execution != null) throw new InvalidOperationException("A prepared torrent can be executed only once.");
                return _execution = ExecuteCoreAsync(progress, ct);
            }
        }

        private async Task ExecuteCoreAsync(IProgress<DownloadProgress>? progress, CancellationToken ct)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token, owner._stop.Token);
            var token = linked.Token;
            token.ThrowIfCancellationRequested();
            // Add only the already-screened metadata. The adapter installs file priorities before Start.
            await using (var transfer = await backend.AddAsync(torrent, root, decisions, token).ConfigureAwait(false))
            {
                token.ThrowIfCancellationRequested();
                await transfer.StartAsync().ConfigureAwait(false);
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var state = transfer.Snapshot;
                    if (state.Error != null) throw new IOException("Torrent download failed: " + state.Error);
                    var percent = double.IsFinite(state.Percent) ? Math.Clamp(state.Percent, 0, 100) : 0;
                    var done = (long)Math.Min(total, total * (percent / 100));
                    var rate = double.IsFinite(state.BytesPerSecond) ? Math.Max(0, state.BytesPerSecond) : 0;
                    progress?.Report(new DownloadProgress(done, total, rate,
                        StatusDetail: $"{state.State} · {state.Seeds} seeds / {state.Peers} peers"));
                    if (state.VerifiedComplete) break;
                    await Task.Delay(pollInterval, token).ConfigureAwait(false);
                }
            }
            // Disposal above stops writers, saves fast resume and sweeps skipped-file artifacts.
            token.ThrowIfCancellationRequested();
            progress?.Report(new DownloadProgress(total, total, 0, StatusDetail: "Verified and saved"));
        }

        public ValueTask DisposeAsync()
        {
            lock (_sync)
            {
                if (_disposal != null) return new ValueTask(_disposal);
                // Likewise do not acquire the owner lock under this session's lock.
                _disposal = Task.Run(DisposeCoreAsync);
                return new ValueTask(_disposal);
            }
        }

        private async Task DisposeCoreAsync()
        {
            _stop.Cancel();
            if (_execution != null)
                try { await _execution.ConfigureAwait(false); } catch { /* ExecuteAsync owns the failure */ }
            _stop.Dispose();
            lock (owner._sync) owner._sessions.Remove(this);
        }
    }
}

internal readonly record struct TorrentDownloadSnapshot(double Percent, double BytesPerSecond,
    int Seeds, int Peers, string State, bool VerifiedComplete, string? Error = null);

internal interface ITorrentDownloadTransfer : IAsyncDisposable
{
    TorrentDownloadSnapshot Snapshot { get; }
    Task StartAsync();
}

internal interface ITorrentDownloadBackend : IAsyncDisposable
{
    Task<TorrentStreamService.ScreeningResult> ScreenAsync(MagnetLink magnet, string directory, Action<string>? status, CancellationToken ct);
    Task<ITorrentDownloadTransfer> AddAsync(Torrent torrent, string directory, IReadOnlyList<TorrentFileDecision> decisions, CancellationToken ct);
    Task SetRateAsync(long bytesPerSecond);
}

internal sealed class TorrentDownloadBackend(TorrentStreamService service) : ITorrentDownloadBackend
{
    public Task<TorrentStreamService.ScreeningResult> ScreenAsync(MagnetLink magnet, string directory, Action<string>? status, CancellationToken ct) =>
        service.ScreenQueueDownloadAsync(magnet, directory, status, ct);
    public Task<ITorrentDownloadTransfer> AddAsync(Torrent torrent, string directory, IReadOnlyList<TorrentFileDecision> decisions, CancellationToken ct) =>
        service.AddQueueDownloadAsync(torrent, directory, decisions, ct);
    public Task SetRateAsync(long bytesPerSecond) => service.ApplyDownloadRateLimitAsync(bytesPerSecond);
    public ValueTask DisposeAsync() => service.DisposeQueueEngineAsync();
}

public partial class TorrentStreamService
{
    // A caller-owned, explicitly configured engine supports isolated verification and
    // keeps queue downloads independent of the Player's streaming engine lifetime.
    internal TorrentStreamService(ClientEngine engine, TorrentContentPolicy policy)
    {
        _engine = engine;
        _policy = policy;
        _downloadPath = engine.Settings.CacheDirectory;
    }

    internal async Task<ScreeningResult> ScreenQueueDownloadAsync(MagnetLink magnet, string directory, Action<string>? status, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // DownloadMetadataAsync removes its probe with CacheDataOnly in MonoTorrent 3.0.2.
        // Reuse and re-screen cached metadata so pause/restart cannot erase fast resume.
        // MonoTorrent 3.0.2's internal GetMetadataPath uses this public cache directory
        // and canonical hash filename; avoid reflection or registering an unsafe probe.
        var metadataPath = Path.Combine(_engine.Settings.MetadataCacheDirectory, magnet.InfoHashes.V1OrV2.ToHex() + ".torrent");
        if (Torrent.TryLoad(metadataPath, out var cached) && cached.InfoHashes.Contains(magnet.InfoHashes.V1OrV2))
        {
            status?.Invoke("Screening cached torrent metadata");
            return new(cached, _policy.EvaluateAll(cached.Files.Select(f => f.Path), Path.GetFullPath(directory)).ToArray(), null);
        }
        return await PreserveResumeDuringMetadataAsync(
            () => ScreenMagnetAsync(magnet, directory, MetadataTimeout, status, ct),
            _engine.Settings.GetFastResumePath(magnet.InfoHashes)).ConfigureAwait(false);
    }

    internal static async Task<T> PreserveResumeDuringMetadataAsync<T>(Func<Task<T>> fetch, string resumePath)
    {
        // A missing/corrupt metadata cache must not let the library's probe delete an
        // otherwise useful resume checkpoint. Restore even if probing is cancelled.
        var resume = File.Exists(resumePath) ? await File.ReadAllBytesAsync(resumePath).ConfigureAwait(false) : null;
        try { return await fetch().ConfigureAwait(false); }
        finally
        {
            if (resume != null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(resumePath)!);
                var temporary = resumePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await File.WriteAllBytesAsync(temporary, resume).ConfigureAwait(false);
                    File.Move(temporary, resumePath, overwrite: true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }
    }

    internal async Task<ITorrentDownloadTransfer> AddQueueDownloadAsync(Torrent torrent, string directory,
        IReadOnlyList<TorrentFileDecision> decisions, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var manager = await _engine.AddAsync(torrent, directory,
            new TorrentSettingsBuilder { CreateContainingDirectory = false }.ToSettings()).ConfigureAwait(false);
        var transfer = new QueueTransfer(_engine, manager);
        try
        {
            for (var i = 0; i < manager.Files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (!decisions[i].IsBlocked) continue;
                await manager.SetFilePriorityAsync(manager.Files[i], Priority.DoNotDownload).ConfigureAwait(false);
                transfer.BlockedPaths.Add(manager.Files[i].DownloadCompleteFullPath);
                transfer.BlockedPaths.Add(manager.Files[i].DownloadIncompleteFullPath);
            }
            ct.ThrowIfCancellationRequested();
            return transfer;
        }
        catch { await transfer.DisposeAsync().ConfigureAwait(false); throw; }
    }

    internal async ValueTask DisposeQueueEngineAsync()
    {
        _disposed = true;
        await _engine.StopAllAsync().ConfigureAwait(false);
        _engine.Dispose();
    }

    private sealed class QueueTransfer(ClientEngine engine, TorrentManager manager) : ITorrentDownloadTransfer
    {
        private readonly object _sync = new();
        private Task? _disposal;
        internal List<string> BlockedPaths { get; } = [];
        public async Task StartAsync()
        {
            await manager.StartAsync().ConfigureAwait(false);
            await WaitForStartupAsync().ConfigureAwait(false);
        }
        public TorrentDownloadSnapshot Snapshot
        {
            get
            {
                var selected = manager.Files.Where(f => f.Priority != Priority.DoNotDownload).ToArray();
                // AllTrue represents hash-verified pieces, unlike a byte count or rounded UI percent.
                var state = manager.State;
                var complete = state is TorrentState.Downloading or TorrentState.Seeding &&
                    selected.Length > 0 && selected.All(f => f.Length == 0 || f.BitField.AllTrue);
                return new(manager.PartialProgress, manager.Monitor.DownloadRate, manager.Peers.Seeds,
                    manager.Peers.Leechs, state.ToString(), complete,
                    state == TorrentState.Error ? "MonoTorrent entered its error state." :
                    state == TorrentState.Stopped && !complete ? "Torrent stopped before verification completed." : null);
            }
        }

        public ValueTask DisposeAsync()
        {
            lock (_sync) return new ValueTask(_disposal ??= StopAndRemoveAsync());
        }

        private async Task StopAndRemoveAsync()
        {
            // No cancellation token: the owner must wait until writers close before publishing a terminal state.
            await WaitForStartupAsync().ConfigureAwait(false);
            await manager.StopAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            try { await engine.RemoveAsync(manager, RemoveMode.KeepAllData).ConfigureAwait(false); }
            finally
            {
                // Stop has closed writers even if unregistering the manager fails.
                foreach (var path in BlockedPaths.Distinct(StringComparer.OrdinalIgnoreCase))
                    if (File.Exists(path)) File.Delete(path);
            }
        }

        private async Task WaitForStartupAsync()
        {
            // MonoTorrent 3.0.2 StartAsync launches StartingMode without awaiting it.
            // Its hash/checkpoint continuation can replace a just-stopped mode. Do not
            // stop/remove underneath that continuation, or publish verified completion
            // while hashing is still in flight. Cancellation retains this owned drain.
            while (manager.State is TorrentState.Starting or TorrentState.Hashing or TorrentState.HashingPaused)
                await Task.Delay(10).ConfigureAwait(false);
        }
    }
}
