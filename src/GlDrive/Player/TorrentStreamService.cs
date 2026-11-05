using System.IO;
using System.Net;
using System.Net.Sockets;
using MonoTorrent;
using MonoTorrent.Client;
using MonoTorrent.Streaming;
using Serilog;

namespace GlDrive.Player;

public partial class TorrentStreamService : IDisposable
{
    private readonly string _downloadPath;
    private readonly ClientEngine _engine;
    private readonly TorrentContentPolicy _policy;

    /// <summary>Blocked file paths per torrent, so their artifacts can be swept on stop.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<string>> _blockedArtifacts = new();
    private TorrentManager? _activeManager;
    private string? _activeStreamHash;
    private IHttpStream? _activeHttpStream;
    private bool _disposed;

    /// <param name="vpnAdapterName">
    /// Name (or description fragment) of a VPN tunnel adapter to bind torrent sockets to, e.g.
    /// "ProtonVPN". When set and available, it takes precedence over the selected download
    /// networks for outgoing peers and binds the incoming listener and DHT socket.
    /// With no VPN preference, outgoing peers use the selected download networks (or the
    /// default route); the listener and DHT retain their ordinary all-interface binding.
    ///
    /// Outgoing peer addresses are resolved for each new connection; listener and DHT
    /// bindings are chosen at service construction. Tracker and DNS traffic are not bound
    /// by this setting. A missing adapter preserves the existing warning-and-default-route
    /// policy, so this is not a VPN kill switch. See <see cref="VpnBinding"/>.
    /// </param>
    /// <param name="maxDownloadBytesPerSecond">Engine-wide download cap, 0 = unlimited. See <see cref="DownloadRateLimit"/>.</param>
    public TorrentStreamService(
        string downloadPath,
        string? vpnAdapterName = null,
        TorrentContentPolicy? contentPolicy = null,
        long maxDownloadBytesPerSecond = 0,
        bool queueDownloads = false)
    {
        _policy = contentPolicy ?? new TorrentContentPolicy(blockExecutables: true);

        _downloadPath = downloadPath;
        Directory.CreateDirectory(_downloadPath);

        var cacheDir = Path.Combine(_downloadPath, ".torrent-cache");
        Directory.CreateDirectory(cacheDir);

        var httpPort = FindFreePort();
        var dhtPort = FindFreePort();
        var listenPort = FindFreePort();

        // Resolved fresh on every construction: the tunnel's address changes on reconnect or
        // server switch, so a remembered one is a silent misbind waiting to happen.
        var bindAddress = VpnBinding.ResolveBindAddress(vpnAdapterName);

        Log.Information("TorrentStreamService: HTTP={HttpPort}, DHT={DhtPort}, Listen={ListenPort}, Bind={Bind}",
            httpPort, dhtPort, listenPort, bindAddress);

        var settings = new EngineSettingsBuilder
        {
            CacheDirectory = cacheDir,
            MaximumConnections = 100,
            MaximumHalfOpenConnections = 16,
            MaximumUploadRate = 100 * 1024, // 100 KB/s upload
            MaximumDownloadRate = ToEngineRate(maxDownloadBytesPerSecond),
            // Local peer discovery broadcasts on the LAN and is meaningless over a tunnel;
            // leaving it on while bound to a VPN would announce us on the local network.
            AllowLocalPeerDiscovery = Equals(bindAddress, IPAddress.Any),
            AllowPortForwarding = false,
            AutoSaveLoadDhtCache = true,
            AutoSaveLoadFastResume = true,
            // Queue ownership ends at completion/cancellation. Accurate mode avoids
            // MonoTorrent's unowned periodic resume writer racing that shutdown.
            FastResumeMode = queueDownloads ? FastResumeMode.Accurate : FastResumeMode.BestEffort,
            AutoSaveLoadMagnetLinkMetadata = true,
            DhtEndPoint = new IPEndPoint(bindAddress, dhtPort),
            ListenEndPoints = new Dictionary<string, IPEndPoint>
            {
                { "ipv4", new IPEndPoint(bindAddress, listenPort) }
            },
            // Stays on loopback: this is the local HTTP endpoint VLC plays from, not peer traffic.
            HttpStreamingPrefix = $"http://127.0.0.1:{httpPort}/",
        }.ToSettings();

        var peerNetworking = TorrentPeerNetworking.ForDownloads(vpnAdapterName);
        _engine = new ClientEngine(settings, peerNetworking.ApplyTo(TorrentTrackerFactories.Create()));
    }

    /// <summary>
    /// The torrent download cap in bytes/s implied by the download settings: slow mode wins,
    /// otherwise the global download speed limit. 0 = unlimited.
    /// </summary>
    public static long DownloadRateLimit(GlDrive.Config.DownloadConfig downloads) =>
        downloads.MultiStream.SlowMode
            ? Math.Max(0, downloads.MultiStream.SlowModeKbps) * 1024L
            : Math.Max(0, downloads.SpeedLimitKbps) * 1024L;

    /// <summary>Change the engine-wide download cap live (slow-mode toggle). 0 = unlimited.</summary>
    public async Task ApplyDownloadRateLimitAsync(long bytesPerSecond)
    {
        var rate = ToEngineRate(bytesPerSecond);
        if (_engine.Settings.MaximumDownloadRate == rate) return;
        await _engine.UpdateSettingsAsync(
            new EngineSettingsBuilder(_engine.Settings) { MaximumDownloadRate = rate }.ToSettings());
        Log.Information("TorrentStreamService: download cap set to {Rate} B/s (0 = unlimited)", rate);
    }

    private static int ToEngineRate(long bytesPerSecond) => (int)Math.Clamp(bytesPerSecond, 0, int.MaxValue);

    /// <summary>Outcome of screening a magnet's contents before anything is added to the engine.</summary>
    public sealed record ScreeningResult(
        Torrent? Torrent,
        IReadOnlyList<TorrentFileDecision> Decisions,
        string? Error)
    {
        public IEnumerable<TorrentFileDecision> Blocked => Decisions.Where(d => d.IsBlocked);
        public bool HasEscapes => Decisions.Any(d => d.Verdict == TorrentFileVerdict.EscapesSaveDirectory);
        public bool Ok => Torrent != null && Error == null;
    }

    /// <summary>
    /// Fetch a magnet's metadata WITHOUT adding it to the engine, then judge every file it
    /// declares. Returns before anything exists on disk.
    ///
    /// The out-of-band fetch is the whole point, and it is not stylistic. Inside
    /// MetadataMode.HandleLtMetadataMessage MonoTorrent runs, in this order:
    ///     Manager.SetMetadata(torrent);      // file list populated
    ///     Manager.StartAsync();              // -> StartingMode -> CreateEmptyFiles -> DownloadMode
    ///     Manager.RaiseMetadataReceived(..); // only NOW does a consumer learn the file list
    /// So by the time HasMetadata flips, or a MetadataReceived handler runs, the manager has
    /// already been started and pieces are requestable. There is no earlier public hook, and
    /// PauseAsync does not help — it races the StartingMode transition. Any design that keeps
    /// AddAsync(magnet, ...) has a window that cannot be closed. ClientEngine.DownloadMetadataAsync
    /// registers a throwaway manager with an EMPTY save path and stops before that StartAsync,
    /// so nothing is ever written.
    ///
    /// This also means the pre-existing DoNotDownload loop on the streaming path was always
    /// running too late.
    /// </summary>
    private async Task<ScreeningResult> ScreenMagnetAsync(
        MagnetLink magnet,
        string saveDir,
        TimeSpan metadataTimeout,
        Action<string>? onStatus,
        CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        ReadOnlyMemory<byte> raw;
        try
        {
            raw = await FetchMetadataWithReporterAsync(
                token => _engine.DownloadMetadataAsync(magnet, token),
                token => ReportMetadataWaitAsync(sw, metadataTimeout, onStatus, token),
                metadataTimeout, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log.Warning("Torrent: no metadata after {Elapsed:F0}s — try a result with more seeders",
                sw.Elapsed.TotalSeconds);
            return new ScreeningResult(null, [], "Timed out waiting for torrent metadata");
        }
        catch (OperationCanceledException)
        {
            return new ScreeningResult(null, [], "Cancelled");
        }
        if (raw.IsEmpty || !Torrent.TryLoad(raw.Span, out var torrent))
            return new ScreeningResult(null, [], "Torrent metadata was unreadable");

        var root = Path.GetFullPath(saveDir);
        var decisions = _policy.EvaluateAll(torrent.Files.Select(f => f.Path), root).ToList();

        foreach (var d in decisions.Where(d => d.IsBlocked))
            Log.Warning("Torrent content blocked ({Verdict}): {Name} — {Detail}",
                d.Verdict, TorrentContentPolicy.DisplayName(d.TorrentPath), d.Detail);

        Log.Information("Torrent screened in {Elapsed:F0}s — {Name}, {Files} files, {Blocked} blocked",
            sw.Elapsed.TotalSeconds, torrent.Name, decisions.Count, decisions.Count(d => d.IsBlocked));

        return new ScreeningResult(torrent, decisions, null);
    }

    internal static async Task<ReadOnlyMemory<byte>> FetchMetadataWithReporterAsync(
        Func<CancellationToken, Task<ReadOnlyMemory<byte>>> fetch,
        Func<CancellationToken, Task> report,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var metadata = CancellationTokenSource.CreateLinkedTokenSource(ct);
        metadata.CancelAfter(timeout);
        var reporter = report(metadata.Token);
        try
        {
            return await fetch(metadata.Token);
        }
        finally
        {
            // The status loop otherwise lives until the full metadata deadline,
            // delaying even an already-successful fetch by five minutes.
            // This linked source stops only our work, never the caller's token.
            metadata.Cancel();
            await reporter;
        }
    }

    /// <summary>
    /// Keep the caller informed while the out-of-band fetch runs.
    ///
    /// The throwaway probe manager is registered isPublic:false, so it never appears in
    /// _engine.Torrents and the per-torrent "3S/1L, 12 available" counts the old inline loop
    /// showed are genuinely gone. That instrumentation was added deliberately in v3.10.66/.67
    /// because 0% was indistinguishable from a hang, so rather than pretend, report elapsed
    /// time against the deadline plus engine-wide connection activity.
    /// </summary>
    private async Task ReportMetadataWaitAsync(
        System.Diagnostics.Stopwatch sw,
        TimeSpan budget,
        Action<string>? onStatus,
        CancellationToken ct)
    {
        if (onStatus == null) return;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct);
                onStatus($"Fetching metadata… {sw.Elapsed.TotalSeconds:F0}s of {budget.TotalSeconds:F0}s " +
                         $"({_engine.ConnectionManager.OpenConnections} peer connections)");
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Mark every blocked file DoNotDownload while the manager is still stopped, and remember
    /// them so their artifacts can be swept when it stops.
    /// </summary>
    private async Task ApplyBlocksAsync(
        TorrentManager manager, string hash, IReadOnlyList<TorrentFileDecision> decisions)
    {
        var blockedPaths = new List<string>();

        // manager.Files is Torrent.Files.Select(...) in declaration order, so index mapping is safe.
        for (var i = 0; i < manager.Files.Count && i < decisions.Count; i++)
        {
            if (!decisions[i].IsBlocked) continue;

            await manager.SetFilePriorityAsync(manager.Files[i], Priority.DoNotDownload);
            blockedPaths.Add(manager.Files[i].FullPath);
            blockedPaths.Add(manager.Files[i].DownloadIncompleteFullPath);
        }

        if (blockedPaths.Count > 0) _blockedArtifacts[hash] = blockedPaths;
    }

    /// <summary>
    /// Delete anything a blocked file left behind, after the manager has stopped and closed
    /// its handles.
    ///
    /// This is needed because DoNotDownload does not mean "nothing is written". Two reasons:
    /// StartingMode.CreateEmptyFiles creates every zero-length entry with no priority check;
    /// and more importantly DiskManager.WriteAsync has no priority awareness at all, so a
    /// skipped file receives whatever bytes it shares with a kept neighbour's first or last
    /// piece — landing COMPLETE when it is smaller than one piece and sits between two kept
    /// files. With 1-16 MiB pieces a small executable can arrive whole. Sweeping afterwards is
    /// the mitigation; the bytes still existed on disk while the transfer ran, and a crash
    /// before this runs leaves them.
    /// </summary>
    private void SweepBlockedArtifacts(string hash)
    {
        if (!_blockedArtifacts.TryRemove(hash, out var paths)) return;

        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    Log.Information("Torrent: removed blocked artifact {Path}", path);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Torrent: could not remove blocked artifact {Path}", path);
            }
        }
    }

    /// <summary>
    /// Starts streaming a torrent from a magnet link. Uses MonoTorrent's built-in HTTP streaming.
    /// Returns an HTTP URL that VLC can play directly.
    /// </summary>
    public async Task<string?> StartStreamingAsync(
        string magnetLink,
        Action<string, double>? onProgress = null,
        CancellationToken ct = default)
    {
        await StopAsync();

        onProgress?.Invoke("Parsing magnet link...", 0);

        MagnetLink magnet;
        try
        {
            magnet = MagnetLink.Parse(magnetLink);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Invalid magnet link");
            onProgress?.Invoke("Invalid magnet link", 0);
            return null;
        }

        var saveDir = Path.Combine(_downloadPath, "Torrents");
        Directory.CreateDirectory(saveDir);

        onProgress?.Invoke("Connecting to DHT and peers...", 0);
        Log.Information("Starting torrent stream");

        // Screen BEFORE the engine ever sees this torrent. Playing is a download too — the
        // whole torrent's pieces pass through the same disk writer — so the gate applies here
        // exactly as it does to queued torrent downloads (TorrentDownloadSource).
        var screening = await ScreenMagnetAsync(
            magnet, saveDir, TimeSpan.FromSeconds(120), s => onProgress?.Invoke(s, 0), ct);

        if (!screening.Ok)
        {
            onProgress?.Invoke(screening.Error ?? "Could not read torrent", 0);
            return null;
        }

        // A path escaping the save directory is refused outright on both paths — it is a
        // malicious torrent, not an inconvenient one, and there is no legitimate version of it.
        if (screening.HasEscapes)
        {
            Log.Warning("Torrent REFUSED for streaming — declares files outside the save folder");
            onProgress?.Invoke("Refused: torrent declares files outside the save folder", 0);
            return null;
        }

        var torrent = screening.Torrent!;
        var hash = torrent.InfoHashes.V1OrV2.ToHex();

        var manager = await _engine.AddStreamingAsync(torrent, saveDir);
        _activeManager = manager;

        try
        {
            // Find the largest video file. Note this is a stream TARGET selector, not a
            // safety control — it happens to ignore executables, which is not the same thing
            // as refusing them, and everything else is handled by the policy above.
            var videoFile = FindVideoFile(manager);
            if (videoFile == null)
            {
                onProgress?.Invoke("No video file found in torrent", 0);
                Log.Warning("No video file in torrent — files: {Files}",
                    string.Join(", ", manager.Files.Select(f => f.Path)));
                return null;
            }

            Log.Information("Torrent video: {Name} ({Size:F1} MB)", videoFile.Path, videoFile.Length / (1024.0 * 1024));

            // Priorities are set while the manager is still stopped — the reason the metadata
            // fetch was moved out of band. Streaming already skips everything but the chosen
            // video; recording the policy-blocked entries as well is what arms the artifact
            // sweep in StopAsync.
            await ApplyBlocksAsync(manager, hash, screening.Decisions);

            foreach (var file in manager.Files)
            {
                if (file != videoFile)
                    await manager.SetFilePriorityAsync(file, Priority.DoNotDownload);
            }

            _activeStreamHash = hash;

            await manager.StartAsync();

            onProgress?.Invoke($"Buffering: {Path.GetFileName(videoFile.Path)}...", 0);

            // Use MonoTorrent's built-in HTTP streaming — handles Range, seeking, buffering
            var httpStream = await manager.StreamProvider!.CreateHttpStreamAsync(videoFile, prebuffer: true, ct);
            _activeHttpStream = httpStream;

            var streamUrl = httpStream.FullUri.ToString();
            Log.Information("Torrent HTTP stream ready at {Url}", streamUrl);

            // Monitor progress in background
            _ = MonitorProgress(manager, videoFile, onProgress, ct);

            onProgress?.Invoke("Ready to play", 5);
            return streamUrl;
        }
        catch
        {
            await StopAsync();
            throw;
        }
    }

    private async Task MonitorProgress(TorrentManager manager, ITorrentManagerFile videoFile,
        Action<string, double>? onProgress, CancellationToken ct)
    {
        try
        {
            while (manager.State != TorrentState.Stopped && manager.State != TorrentState.Error)
            {
                if (ct.IsCancellationRequested) break;

                var downloaded = videoFile.BytesDownloaded();
                var pct = videoFile.Length > 0 ? (double)downloaded * 100 / videoFile.Length : 0;
                var speed = manager.Monitor.DownloadRate / 1024.0;
                var peers = manager.Peers.Seeds + manager.Peers.Leechs;

                if (pct < 99.9)
                    onProgress?.Invoke($"Downloading: {pct:F1}% ({speed:F0} KB/s, {peers} peers)", pct);
                else
                {
                    onProgress?.Invoke("Download complete", 100);
                    break;
                }

                await Task.Delay(2000, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Debug(ex, "Torrent progress monitor ended"); }
    }

    private static ITorrentManagerFile? FindVideoFile(TorrentManager manager)
    {
        var videoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mkv", ".avi", ".mp4", ".m4v", ".wmv", ".mov",
            ".mpg", ".mpeg", ".ts", ".vob", ".flv", ".webm"
        };

        return manager.Files
            .Where(f => videoExtensions.Contains(Path.GetExtension(f.Path)))
            .OrderByDescending(f => f.Length)
            .FirstOrDefault();
    }

    /// <summary>
    /// Stops the stream. The player calls this on every playback change; queued torrent
    /// downloads run in TorrentDownloadSource and are not affected.
    /// </summary>
    public async Task StopAsync()
    {
        if (_activeHttpStream != null)
        {
            try { _activeHttpStream.Dispose(); } catch { }
            _activeHttpStream = null;
        }

        if (_activeManager != null)
        {
            try
            {
                await _activeManager.StopAsync();
                await _engine.RemoveAsync(_activeManager);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Error stopping torrent");
            }
            _activeManager = null;
        }

        if (_activeStreamHash != null)
        {
            SweepBlockedArtifacts(_activeStreamHash);
            _activeStreamHash = null;
        }
    }

    /// <summary>
    /// How long a queued download waits for magnet metadata before giving up. A magnet carries
    /// only an info hash: until metadata arrives from a peer there is no file list and progress
    /// is legitimately 0%. On a thinly-seeded torrent that can last minutes, so this is longer
    /// than streaming's 120 s — a background download can afford to be patient.
    /// </summary>
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromMinutes(5);

    private static int FindFreePort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _activeHttpStream?.Dispose(); } catch { }

        try
        {
            if (_activeManager != null)
            {
                _activeManager.StopAsync().GetAwaiter().GetResult();
                _engine.RemoveAsync(_activeManager).GetAwaiter().GetResult();
            }
            _engine.StopAllAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Error disposing torrent engine");
        }

        GC.SuppressFinalize(this);
    }
}
