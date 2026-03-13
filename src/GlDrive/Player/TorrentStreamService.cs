using System.IO;
using System.Net;
using System.Net.Sockets;
using MonoTorrent;
using MonoTorrent.Client;
using MonoTorrent.Connections;
using Serilog;

namespace GlDrive.Player;

public class TorrentStreamService : IDisposable
{
    private readonly string _downloadPath;
    private readonly ClientEngine _engine;
    private TorrentManager? _activeManager;
    private Stream? _activeStream;
    private HttpListener? _httpListener;
    private int _httpPort;
    private string _httpAuthToken = "";
    private CancellationTokenSource? _serveCts;
    private bool _disposed;

    public TorrentStreamService(string downloadPath)
    {
        _downloadPath = downloadPath;
        Directory.CreateDirectory(_downloadPath);

        var settings = new EngineSettingsBuilder
        {
            CacheDirectory = Path.Combine(_downloadPath, ".torrent-cache"),
            MaximumConnections = 60,
            MaximumHalfOpenConnections = 8,
            MaximumUploadRate = 50 * 1024, // 50 KB/s upload limit
        }.ToSettings();

        _engine = new ClientEngine(settings);
    }

    /// <summary>
    /// Starts streaming a torrent from a magnet link. Uses MonoTorrent's streaming API
    /// to download pieces sequentially. Returns an HTTP URL that VLC can play from.
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

        onProgress?.Invoke("Fetching torrent metadata...", 0);

        var manager = await _engine.AddStreamingAsync(magnet, saveDir);
        _activeManager = manager;

        try
        {
            await manager.StartAsync();
            onProgress?.Invoke("Connecting to peers...", 0);

            // Wait for metadata
            var metadataTimeout = TimeSpan.FromSeconds(90);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!manager.HasMetadata)
            {
                ct.ThrowIfCancellationRequested();
                if (sw.Elapsed > metadataTimeout)
                {
                    onProgress?.Invoke("Timeout waiting for metadata from peers", 0);
                    Log.Warning("Torrent metadata timeout");
                    return null;
                }
                await Task.Delay(500, ct);
                var peers = manager.Peers.Seeds + manager.Peers.Leechs;
                onProgress?.Invoke($"Fetching metadata... ({peers} peers)", 0);
            }

            // Find the largest video file
            var videoFile = FindVideoFile(manager);
            if (videoFile == null)
            {
                onProgress?.Invoke("No video file found in torrent", 0);
                Log.Warning("No video file in torrent");
                return null;
            }

            Log.Information("Torrent video: {Name} ({Size} bytes)", videoFile.Path, videoFile.Length);

            // Set priority: DoNotDownload for non-video files
            foreach (var file in manager.Files)
            {
                if (file != videoFile)
                    await manager.SetFilePriorityAsync(file, Priority.DoNotDownload);
            }

            onProgress?.Invoke($"Buffering: {Path.GetFileName(videoFile.Path)}...", 0);

            // Create a seekable stream via MonoTorrent's streaming API
            // This prebuffers the first and last pieces automatically
            var stream = await manager.StreamProvider!.CreateStreamAsync(videoFile, prebuffer: true, ct);
            _activeStream = stream;

            // Start a local HTTP server to serve the stream to VLC
            var httpUrl = StartHttpServer(stream, videoFile.Length, GetContentType(videoFile.Path));

            // Monitor progress in background
            _ = MonitorProgress(manager, videoFile, onProgress, ct);

            onProgress?.Invoke("Ready to play", 5);
            Log.Information("Torrent streaming at {Url}", httpUrl);

            return httpUrl;
        }
        catch
        {
            await StopAsync();
            throw;
        }
    }

    private string StartHttpServer(Stream torrentStream, long fileLength, string contentType)
    {
        _serveCts?.Cancel();
        _serveCts?.Dispose();
        try { _httpListener?.Stop(); } catch { }

        _httpPort = FindFreePort();
        _httpAuthToken = Guid.NewGuid().ToString("N");
        _httpListener = new HttpListener();
        _httpListener.Prefixes.Add($"http://127.0.0.1:{_httpPort}/");
        _httpListener.Start();
        _serveCts = new CancellationTokenSource();

        var cts = _serveCts;
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    var ctx = await _httpListener.GetContextAsync();
                    _ = Task.Run(() => HandleHttpRequest(ctx, torrentStream, fileLength, contentType));
                }
                catch (ObjectDisposedException) { break; }
                catch (HttpListenerException) { break; }
                catch (Exception ex) { Log.Debug(ex, "Torrent HTTP server error"); }
            }
        });

        return $"http://127.0.0.1:{_httpPort}/stream?token={_httpAuthToken}";
    }

    private async Task HandleHttpRequest(HttpListenerContext ctx, Stream torrentStream,
        long fileLength, string contentType)
    {
        try
        {
            if (ctx.Request.QueryString["token"] != _httpAuthToken)
            {
                ctx.Response.StatusCode = 403;
                ctx.Response.Close();
                return;
            }

            long offset = 0;
            var rangeHeader = ctx.Request.Headers["Range"];
            if (rangeHeader != null && rangeHeader.StartsWith("bytes="))
            {
                var parts = rangeHeader[6..].Split('-');
                if (long.TryParse(parts[0], out var start))
                    offset = start;
            }

            ctx.Response.ContentType = contentType;
            ctx.Response.Headers.Add("Accept-Ranges", "bytes");

            if (offset > 0 && fileLength > 0)
            {
                ctx.Response.StatusCode = 206;
                ctx.Response.Headers.Add("Content-Range", $"bytes {offset}-{fileLength - 1}/{fileLength}");
                ctx.Response.ContentLength64 = fileLength - offset;
            }
            else
            {
                ctx.Response.StatusCode = 200;
                if (fileLength > 0) ctx.Response.ContentLength64 = fileLength;
            }

            // The MonoTorrent stream is seekable
            lock (torrentStream)
            {
                torrentStream.Seek(offset, SeekOrigin.Begin);
            }

            var buffer = new byte[256 * 1024];
            long totalSent = 0;
            var toSend = fileLength > 0 ? fileLength - offset : long.MaxValue;

            while (totalSent < toSend)
            {
                int read;
                var toRead = (int)Math.Min(buffer.Length, toSend - totalSent);
                lock (torrentStream)
                {
                    read = torrentStream.Read(buffer, 0, toRead);
                }
                if (read <= 0) break;

                await ctx.Response.OutputStream.WriteAsync(buffer.AsMemory(0, read));
                await ctx.Response.OutputStream.FlushAsync();
                totalSent += read;
            }

            ctx.Response.Close();
        }
        catch (HttpListenerException) { }
        catch (IOException) { }
        catch (Exception ex)
        {
            Log.Debug(ex, "Torrent HTTP request error");
            try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
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

    public async Task StopAsync()
    {
        _serveCts?.Cancel();
        _serveCts?.Dispose();
        _serveCts = null;

        try { _httpListener?.Stop(); } catch { }
        _httpListener = null;

        if (_activeStream != null)
        {
            try { _activeStream.Dispose(); } catch { }
            _activeStream = null;
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

    private static string GetContentType(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".mkv" => "video/x-matroska",
            ".avi" => "video/x-msvideo",
            ".mp4" or ".m4v" => "video/mp4",
            ".wmv" => "video/x-ms-wmv",
            ".mov" => "video/quicktime",
            ".mpg" or ".mpeg" => "video/mpeg",
            ".ts" => "video/mp2t",
            ".flv" => "video/x-flv",
            ".webm" => "video/webm",
            _ => "application/octet-stream"
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _serveCts?.Cancel();
        _serveCts?.Dispose();
        try { _httpListener?.Stop(); } catch { }

        try { _activeStream?.Dispose(); } catch { }

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
