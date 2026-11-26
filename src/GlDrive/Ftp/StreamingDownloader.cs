using System.IO;
using FluentFTP;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Downloads.Segmented;
using Serilog;

namespace GlDrive.Ftp;

/// <summary>A remote file to download; <see cref="Size"/> -1 = ask the server.</summary>
public sealed record FtpDownloadFile(string RemotePath, string LocalPath, long Size);

/// <summary>
/// FTP downloads through the multi-stream engine (<see cref="SegmentedTransfer"/>). Lane 1 is
/// the main pool's connection. Verified release mirrors and an idle race login can contribute lanes. Files land as <c>.gdpart</c> staging files and are renamed into
/// place when complete, so a half-written volume never carries its final name.
/// </summary>
public class StreamingDownloader
{
    private readonly FtpConnectionPool _pool;
    private readonly string _serverId;
    private readonly DownloadConfig _config;
    private readonly IRaceLoginLender? _raceLender;
    private readonly TransferPacer _serverPacer;
    private long _serverCap;

    public StreamingDownloader(FtpConnectionPool pool, string serverId, DownloadConfig config,
        int serverSpeedLimitKbps, IRaceLoginLender? raceLender)
    {
        _pool = pool;
        _serverId = serverId;
        _config = config;
        _raceLender = raceLender;
        UpdateSpeedLimit(serverSpeedLimitKbps);
        _serverPacer = new TransferPacer(() => ServerBytesPerSecond, _ => 0);
        DownloadNetworkServices.Initialize(config);
    }

    internal Func<string, IReadOnlyList<DownloadMirror>>? FindMirrors { get; init; }

    internal long ServerBytesPerSecond => Interlocked.Read(ref _serverCap);
    internal void UpdateSpeedLimit(int kbps) => Interlocked.Exchange(ref _serverCap, kbps > 0 ? kbps * 1024L : 0);

    /// <summary>True when the download pool is in a BNC cooldown — callers should
    /// defer rather than borrow (which would just throw).</summary>
    public bool IsPoolInCooldown => _pool.IsThrottled;

    public Task DownloadFiles(IReadOnlyList<FtpDownloadFile> files,
        IProgress<DownloadProgress>? progress, CancellationToken ct, Func<bool>? yieldWhen = null,
        Action<long>? onBytes = null, string? releaseRoot = null, IReadOnlyList<FtpDownloadFile>? manifests = null) =>
        DownloadFilesCore(files, progress, ct, yieldWhen, onBytes, releaseRoot, manifests, null);

    private async Task DownloadFilesCore(IReadOnlyList<FtpDownloadFile> files, IProgress<DownloadProgress>? progress,
        CancellationToken ct, Func<bool>? yieldWhen, Action<long>? onBytes, string? releaseRoot,
        IReadOnlyList<FtpDownloadFile>? manifests, IReadOnlyDictionary<string, MirrorChecksum>? retryChecksums)
    {
        if (yieldWhen?.Invoke() == true) throw new BackgroundPullYieldedException();
        var multi = _config.MultiStream;
        var networks = DownloadNetworkServices.ResolveNetworks();
        var usable = networks.Where(n => !DownloadNetworkServices.IsOverAllowance(n)).ToList();
        if (usable.Count == 0)
            throw new NetworkAllowanceExhaustedException(string.Join(", ", networks.Select(n => n.DisplayName)));

        var conn = await _pool.BorrowFor(FtpBorrowPurpose.Download, ct);
        var mainLane = new FtpLane(conn, _pool, _pool.UseCpsv, usable[0], usable[0].DisplayName,
            yieldWhen: yieldWhen, overAllowance: DownloadNetworkServices.IsOverAllowance);
        var mirrorLanes = new List<(string Id, MirrorLane Lane)>();
        var transferFiles = new List<TransferFile>(files.Count);
        try
        {
            foreach (var f in files)
            {
                if (yieldWhen?.Invoke() == true) throw new BackgroundPullYieldedException();
                var size = f.Size >= 0 ? f.Size : await mainLane.GetSizeAsync(f.RemotePath, ct);
                if (size > 0 && File.Exists(f.LocalPath) && new FileInfo(f.LocalPath).Length >= size)
                {
                    // Already complete on disk — the engine counts it as done without a fetch.
                    transferFiles.Add(new TransferFile(f.RemotePath, f.LocalPath, size));
                    continue;
                }
                if (_config.WriteBufferLimitMb > 0 && size > _config.WriteBufferLimitMb * 1024L * 1024)
                    throw new IOException($"Write buffer limit exceeded ({_config.WriteBufferLimitMb} MB): {f.RemotePath}");
                // size + MDTM identify the remote version; a staged partial of another version
                // is restarted instead of resumed (StagedFile compares this).
                var stamp = size >= 0 ? await mainLane.GetModifiedStampAsync(f.RemotePath, ct) : null;
                var file = new TransferFile(f.RemotePath, f.LocalPath, size, stamp == null ? null : $"{size}:{stamp}");
                if (retryChecksums != null && retryChecksums.TryGetValue(file.Key, out var expected))
                    file = file with { MirrorChecksum = expected };
                transferFiles.Add(file);
            }

            if (multi.Enabled && multi.UseReleaseMirrors && releaseRoot != null)
                await PrepareMirrorsAsync(mainLane, releaseRoot, manifests ?? [], transferFiles, usable, mirrorLanes, ct);

            ILaneSource? extra = null;
            if (multi.Enabled && multi.BorrowIdleRaceLogin && _raceLender != null && _pool.UseCpsv)
            {
                var second = usable[usable.Count > 1 ? 1 : 0];
                extra = new RaceLoginLaneSource(_raceLender, _serverId, second, DownloadNetworkServices.IsOverAllowance);
            }

            var options = new SegmentedTransferOptions
            {
                BufferSize = Math.Clamp(_config.StreamingBufferSizeKb, 64, 4096) * 1024,
                MinSplitBytes = Math.Clamp(multi.FtpMinSplitMb, 4, 1024) * 1024L * 1024,
                MaxLanes = mirrorLanes.Count + (extra != null ? 2 : 1),
                Pacer = new CompositePacer(DownloadNetworkServices.Pacer, _serverPacer),
                OnBytes = (network, bytes) => { DownloadNetworkServices.RecordBytes(network, bytes); onBytes?.Invoke(bytes); },
            };

            Log.Information("Streaming download: {Count} file(s), {Bytes:N0} bytes via {Network}{Extra}",
                transferFiles.Count, transferFiles.Where(f => f.Size > 0).Sum(f => f.Size), usable[0].DisplayName,
                extra != null ? " (+ race login while idle)" : "");

            var reporter = progress == null ? null : new Progress<TransferProgress>(p =>
                progress.Report(new DownloadProgress(p.DoneBytes, p.TotalBytes, p.BytesPerSecond, p.CurrentFile, p.Lanes, p.Map)));

            using (KeepAwake.Acquire())
            {
                var transfer = new SegmentedTransfer(transferFiles, options);
                // The engine owns the lane from here and disposes it (returning the connection).
                var lanes = mirrorLanes.Select(site => (site.Id, Lane: (ISegmentLane)site.Lane))
                    .Prepend((Id: _serverId, Lane: (ISegmentLane)mainLane))
                    .OrderByDescending(site => site.Id == multi.PreferredDownloadServerId)
                    .Select(site => site.Lane).ToArray();
                mirrorLanes.Clear();
                mainLane = null;
                await transfer.RunAsync(lanes, extra, reporter, ct);
            }
        }
        catch (MirrorContentMismatchException) when (releaseRoot != null)
        {
            ct.ThrowIfCancellationRequested();
            Log.Warning("Mirror checksum mismatch for {Root}; retrying on the original site", releaseRoot);
            var expected = transferFiles.Where(f => f.MirrorChecksum != null)
                .ToDictionary(f => f.Key, f => f.MirrorChecksum!, StringComparer.Ordinal);
            await DownloadFilesCore(files, progress, ct, yieldWhen, onBytes, null, null, expected);
        }
        catch (DownloadLanesYieldedException) when (yieldWhen != null)
        {
            throw new BackgroundPullYieldedException();
        }
        catch (BackgroundPullYieldedException) { throw; }
        catch (Exception ex) when (mainLane != null)
        {
            // Failed before the engine took the lane (SIZE, cancel): the control channel may
            // be mid-reply, so poison rather than return it.
            mainLane.Fault(ex);
            throw;
        }
        finally
        {
            foreach (var mirror in mirrorLanes) await mirror.Lane.DisposeAsync();
            if (mainLane != null) await mainLane.DisposeAsync();
            DownloadNetworkServices.Flush();
        }
    }

    private async Task PrepareMirrorsAsync(FtpLane main, string root, IReadOnlyList<FtpDownloadFile> manifests,
        List<TransferFile> files, List<NetworkPath> networks, List<(string Id, MirrorLane Lane)> lanes, CancellationToken ct)
    {
        var mirrors = FindMirrors?.Invoke(root.Split('/')[^1]);
        if (mirrors == null || mirrors.Count == 0) return;
        var prefix = root + "/";
        var needed = files.Where(f => f.Size > 0 && f.Key.StartsWith(prefix, StringComparison.Ordinal) &&
            !(File.Exists(f.LocalPath) && new FileInfo(f.LocalPath).Length >= f.Size)).ToArray();
        if (needed.Length == 0) return;
        var relativeManifests = manifests.Where(f => f.RemotePath.StartsWith(prefix, StringComparison.Ordinal))
            .Select(f => f.RemotePath[prefix.Length..]).ToArray();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var origin = new Dictionary<string, MirrorChecksum>(StringComparer.Ordinal);
        try
        {
            var inventory = await MirrorInventory.ReadAsync(main, root, relativeManifests, deadline.Token);
            foreach (var file in needed)
            {
                var relative = file.Key[prefix.Length..];
                if (inventory.Files.TryGetValue(relative, out var hash) ||
                    (hash = await main.ChecksumAsync(file.Key, FtpHashAlgorithm.NONE, deadline.Token)) != null)
                    origin.Add(relative, hash);
            }
        }
        catch (Exception ex)
        {
            main.Fault(ex);
            ct.ThrowIfCancellationRequested();
            Log.Debug(ex, "Could not verify source checksums for {Root}", root);
            return;
        }

        if (origin.Count == 0) return;
        foreach (var mirror in mirrors.OrderByDescending(m => m.Id == _config.MultiStream.PreferredDownloadServerId)
                     .ThenBy(m => m.Id, StringComparer.Ordinal))
        {
            if (lanes.Count >= 2 || deadline.IsCancellationRequested) break;
            var network = networks[(1 + lanes.Count) % networks.Count];
            try
            {
                var lane = await mirror.Downloader.TryOpenMirrorAsync(mirror, prefix, relativeManifests,
                    origin, network, deadline.Token);
                if (lane != null) lanes.Add((mirror.Id, lane));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; }
        }
        ct.ThrowIfCancellationRequested();
        for (int i = 0; i < files.Count; i++)
        {
            var file = files[i];
            if (!lanes.Any(site => site.Lane.CanServe(file))) continue;
            var hash = origin[file.Key[prefix.Length..]];
            files[i] = file with { Validator = $"mirror:{hash.Algorithm}:{hash.Value}", MirrorChecksum = hash };
        }
    }

    private async Task<MirrorLane?> TryOpenMirrorAsync(DownloadMirror mirror, string originPrefix,
        string[] manifests, IReadOnlyDictionary<string, MirrorChecksum> origin, NetworkPath network, CancellationToken ct)
    {
        FtpLane? ftp = null;
        try
        {
            var conn = await FtpLane.BorrowWithin(t => _pool.BorrowFor(FtpBorrowPurpose.Download, t),
                TimeSpan.FromSeconds(2), mirror.Name, ct);
            ftp = new FtpLane(conn, null, _pool.UseCpsv, network, mirror.Name,
                overAllowance: DownloadNetworkServices.IsOverAllowance);
            var copy = await MirrorInventory.ReadAsync(ftp, mirror.Root, manifests, ct);
            var paths = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (relative, expected) in origin)
            {
                var path = mirror.Root + "/" + relative;
                if (!copy.Files.TryGetValue(relative, out var actual) || actual.Algorithm != expected.Algorithm)
                    actual = await ftp.ChecksumAsync(path, expected.Algorithm, ct);
                if (actual == expected) paths.Add(originPrefix + relative, path);
            }
            if (paths.Count == 0) return null;
            var lane = new MirrorLane(ftp, paths, new CompositePacer(DownloadNetworkServices.Pacer, _serverPacer));
            ftp = null;
            return lane;
        }
        catch (Exception ex)
        {
            ftp?.Fault(ex);
            ct.ThrowIfCancellationRequested();
            Log.Debug(ex, "Skipping release mirror {Site}", mirror.Name);
            return null;
        }
        finally
        {
            if (ftp != null) await ftp.DisposeAsync();
        }
    }
}
