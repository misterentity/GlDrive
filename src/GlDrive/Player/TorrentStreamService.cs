using System.IO;
using System.Net;
using System.Net.Sockets;
using MonoTorrent;
using MonoTorrent.Client;
using MonoTorrent.Streaming;
using Serilog;

namespace GlDrive.Player;

public class TorrentStreamService : IDisposable
{
    private readonly string _downloadPath;
    private readonly ClientEngine _engine;
    private TorrentManager? _activeManager;
    private IHttpStream? _activeHttpStream;
    private bool _disposed;

    /// <param name="vpnAdapterName">
    /// Name (or description fragment) of a VPN tunnel adapter to bind torrent sockets to, e.g.
    /// "ProtonVPN". Null or empty binds to all interfaces, i.e. the ordinary connection.
    ///
    /// PARTIAL BY CONSTRUCTION — see <see cref="VpnBinding"/>. This binds the incoming listener
    /// and the DHT socket, which is everything MonoTorrent 3.0.2 exposes. Outgoing peer
    /// connections cannot be bound: that needs a custom ISocketConnector injected through the
    /// `Factories` API, which does not exist in 3.0.2, and 3.0.2 is the newest STABLE release
    /// (3.0.3 and 3.9.0 are alpha). Treat this as reducing exposure, not eliminating it.
    /// </param>
    public TorrentStreamService(string downloadPath, string? vpnAdapterName = null)
    {
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
            // Local peer discovery broadcasts on the LAN and is meaningless over a tunnel;
            // leaving it on while bound to a VPN would announce us on the local network.
            AllowLocalPeerDiscovery = Equals(bindAddress, IPAddress.Any),
            AllowPortForwarding = false,
            AutoSaveLoadDhtCache = true,
            AutoSaveLoadFastResume = true,
            AutoSaveLoadMagnetLinkMetadata = true,
            DhtEndPoint = new IPEndPoint(bindAddress, dhtPort),
            ListenEndPoints = new Dictionary<string, IPEndPoint>
            {
                { "ipv4", new IPEndPoint(bindAddress, listenPort) }
            },
            // Stays on loopback: this is the local HTTP endpoint VLC plays from, not peer traffic.
            HttpStreamingPrefix = $"http://127.0.0.1:{httpPort}/",
        }.ToSettings();

        _engine = new ClientEngine(settings);
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

        var manager = await _engine.AddStreamingAsync(magnet, saveDir);
        _activeManager = manager;

        try
        {
            await manager.StartAsync();
            onProgress?.Invoke("Connecting to peers...", 0);

            // Wait for metadata with progress updates
            var metadataTimeout = TimeSpan.FromSeconds(120);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!manager.HasMetadata)
            {
                ct.ThrowIfCancellationRequested();
                if (sw.Elapsed > metadataTimeout)
                {
                    var peers = manager.Peers.Seeds + manager.Peers.Leechs;
                    Log.Warning("Torrent metadata timeout after {Elapsed}s — {Peers} peers, {Available} available",
                        sw.Elapsed.TotalSeconds, peers, manager.Peers.Available);
                    onProgress?.Invoke("Timeout waiting for metadata — try a torrent with more seeds", 0);
                    return null;
                }
                await Task.Delay(500, ct);
                var p = manager.Peers;
                onProgress?.Invoke(
                    $"Fetching metadata... ({p.Seeds}S/{p.Leechs}L, {p.Available} available)",
                    0);

                // Log every 10 seconds
                if ((int)sw.Elapsed.TotalSeconds % 10 == 0 && sw.Elapsed.TotalSeconds > 1)
                    Log.Information("Metadata wait: {Elapsed}s, Seeds={Seeds}, Leechs={Leechs}, Available={Available}",
                        (int)sw.Elapsed.TotalSeconds, p.Seeds, p.Leechs, p.Available);
            }

            Log.Information("Torrent metadata received in {Elapsed}s — {Files} files",
                sw.Elapsed.TotalSeconds, manager.Files.Count);

            // Find the largest video file
            var videoFile = FindVideoFile(manager);
            if (videoFile == null)
            {
                onProgress?.Invoke("No video file found in torrent", 0);
                Log.Warning("No video file in torrent — files: {Files}",
                    string.Join(", ", manager.Files.Select(f => f.Path)));
                return null;
            }

            Log.Information("Torrent video: {Name} ({Size:F1} MB)", videoFile.Path, videoFile.Length / (1024.0 * 1024));

            // Set priority: DoNotDownload for non-video files
            foreach (var file in manager.Files)
            {
                if (file != videoFile)
                    await manager.SetFilePriorityAsync(file, Priority.DoNotDownload);
            }

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

    // ── Download and keep ──────────────────────────────────────────────────────────
    //
    // Deliberately separate from the streaming path above. Streaming owns exactly one
    // _activeManager and StartStreamingAsync calls StopAsync() first, so folding downloads
    // into it would mean starting a download killed your stream and vice versa. Downloads
    // live in their own dictionary: several can run at once, and StopAsync() (which the
    // player calls whenever playback changes) leaves them alone.

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TorrentManager> _downloads = new();

    /// <summary>Progress snapshot for one background download.</summary>
    public record DownloadProgress(
        string Hash,
        string Name,
        double Percent,
        long DownloadRateBytes,
        int Seeds,
        int Leeches,
        string State);

    /// <summary>
    /// Download a torrent to <paramref name="saveDir"/> and stop when it completes, leaving the
    /// files in place. Runs alongside streaming and other downloads.
    ///
    /// Returns the info hash, or null if the magnet was unusable. Progress is reported until
    /// the download finishes, is cancelled, or the service is disposed.
    /// </summary>
    public async Task<string?> StartDownloadAsync(
        string magnetLink,
        string saveDir,
        Action<DownloadProgress>? onProgress = null,
        CancellationToken ct = default)
    {
        MagnetLink magnet;
        try
        {
            magnet = MagnetLink.Parse(magnetLink);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Torrent download: invalid magnet link");
            return null;
        }

        var hash = magnet.InfoHashes.V1OrV2.ToHex();
        if (_downloads.ContainsKey(hash))
        {
            Log.Information("Torrent download already running for {Hash}", hash);
            return hash;
        }

        Directory.CreateDirectory(saveDir);

        var manager = await _engine.AddAsync(magnet, saveDir);
        if (!_downloads.TryAdd(hash, manager))
        {
            await _engine.RemoveAsync(manager);
            return hash;
        }

        Log.Information("Torrent download starting → {SaveDir} ({Hash})", saveDir, hash);
        await manager.StartAsync();

        _ = MonitorDownloadAsync(hash, manager, saveDir, onProgress, ct);
        return hash;
    }

    private async Task MonitorDownloadAsync(
        string hash,
        TorrentManager manager,
        string saveDir,
        Action<DownloadProgress>? onProgress,
        CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && !_disposed)
            {
                await Task.Delay(1000, ct);

                var name = manager.Torrent?.Name ?? manager.InfoHashes.V1OrV2.ToHex();
                var p = manager.Peers;

                onProgress?.Invoke(new DownloadProgress(
                    hash, name, manager.Progress, manager.Monitor.DownloadRate,
                    p.Seeds, p.Leechs, manager.State.ToString()));

                // "Download and keep": the moment the data is on disk we stop. Seeding is not
                // started, so nothing holds upload bandwidth or a connection afterwards.
                if (manager.Complete || manager.State == TorrentState.Seeding)
                {
                    Log.Information("Torrent download complete → {SaveDir} ({Name})", saveDir, name);
                    onProgress?.Invoke(new DownloadProgress(
                        hash, name, 100, 0, p.Seeds, p.Leechs, "Complete"));
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warning(ex, "Torrent download monitor failed for {Hash}", hash);
        }
        finally
        {
            await RemoveDownloadAsync(hash);
        }
    }

    /// <summary>Cancel a running download. Files already written are left on disk.</summary>
    public Task CancelDownloadAsync(string hash) => RemoveDownloadAsync(hash);

    private async Task RemoveDownloadAsync(string hash)
    {
        if (!_downloads.TryRemove(hash, out var manager)) return;

        try
        {
            await manager.StopAsync();
            await _engine.RemoveAsync(manager);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Error stopping torrent download {Hash}", hash);
        }
    }

    /// <summary>
    /// Stops the STREAM only. Downloads are untouched by design — the player calls this on
    /// every playback change, and it must not take background downloads down with it.
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
    }

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
