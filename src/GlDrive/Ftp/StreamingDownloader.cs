using System.IO;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Downloads.Segmented;
using Serilog;

namespace GlDrive.Ftp;

/// <summary>A remote file to download; <see cref="Size"/> -1 = ask the server.</summary>
public sealed record FtpDownloadFile(string RemotePath, string LocalPath, long Size);

/// <summary>
/// FTP downloads through the multi-stream engine (<see cref="SegmentedTransfer"/>). Lane 1 is
/// the main pool's connection; lane 2, when enabled, is the site's race login borrowed while
/// the site is not racing. Files land as <c>.gdpart</c> staging files and are renamed into
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

    internal long ServerBytesPerSecond => Interlocked.Read(ref _serverCap);
    internal void UpdateSpeedLimit(int kbps) => Interlocked.Exchange(ref _serverCap, kbps > 0 ? kbps * 1024L : 0);

    /// <summary>True when the download pool is in a BNC cooldown — callers should
    /// defer rather than borrow (which would just throw).</summary>
    public bool IsPoolInCooldown => _pool.IsThrottled;

    public async Task DownloadFiles(IReadOnlyList<FtpDownloadFile> files,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var multi = _config.MultiStream;
        var networks = DownloadNetworkServices.ResolveNetworks();
        var usable = networks.Where(n => !DownloadNetworkServices.IsOverAllowance(n)).ToList();
        if (usable.Count == 0)
            throw new NetworkAllowanceExhaustedException(string.Join(", ", networks.Select(n => n.DisplayName)));

        var conn = await _pool.Borrow(ct);
        var mainLane = new FtpLane(conn, _pool, _pool.UseCpsv, usable[0], usable[0].DisplayName,
            overAllowance: DownloadNetworkServices.IsOverAllowance);
        try
        {
            var transferFiles = new List<TransferFile>(files.Count);
            foreach (var f in files)
            {
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
                transferFiles.Add(new TransferFile(f.RemotePath, f.LocalPath, size,
                    stamp == null ? null : $"{size}:{stamp}"));
            }

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
                MaxLanes = extra != null ? 2 : 1,
                Pacer = new CompositePacer(DownloadNetworkServices.Pacer, _serverPacer),
                OnBytes = DownloadNetworkServices.RecordBytes,
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
                var lanes = new ISegmentLane[] { mainLane };
                mainLane = null;
                await transfer.RunAsync(lanes, extra, reporter, ct);
            }
        }
        catch (Exception ex) when (mainLane != null)
        {
            // Failed before the engine took the lane (SIZE, cancel): the control channel may
            // be mid-reply, so poison rather than return it.
            mainLane.Fault(ex);
            throw;
        }
        finally
        {
            if (mainLane != null) await mainLane.DisposeAsync();
            DownloadNetworkServices.Flush();
        }
    }
}
