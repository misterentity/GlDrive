using System.IO;
using System.Net;
using System.Net.Sockets;
using FluentFTP;
using GlDrive.Downloads;
using GlDrive.Ftp;
using GlDrive.Services;
using Serilog;
using SharpCompress.Archives.Rar;

namespace GlDrive.Player;

public class MediaStreamServer : IDisposable
{
    private readonly ServerManager _serverManager;
    private HttpListener? _listener;
    private CancellationTokenSource _cts = new();
    private int _port;

    public int Port => _port;
    public string BaseUrl => $"http://127.0.0.1:{_port}/";

    public MediaStreamServer(ServerManager serverManager)
    {
        _serverManager = serverManager;
    }

    public void Start()
    {
        _port = FindFreePort();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
        _listener.Start();
        _ = AcceptLoop();
        Log.Information("Media stream server started on port {Port}", _port);
    }

    private static int FindFreePort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var ctx = await _listener!.GetContextAsync();
                _ = Task.Run(() => HandleRequest(ctx));
            }
            catch (ObjectDisposedException) { break; }
            catch (HttpListenerException) { break; }
            catch (Exception ex)
            {
                Log.Debug(ex, "Media server accept error");
            }
        }
    }

    private async Task HandleRequest(HttpListenerContext ctx)
    {
        try
        {
            var rawUrl = ctx.Request.RawUrl ?? "";
            Log.Debug("Media stream request: {Method} {Url}", ctx.Request.HttpMethod, rawUrl);

            if (rawUrl.StartsWith("/rar-stream"))
                await HandleRarStream(ctx);
            else if (rawUrl.StartsWith("/stream"))
                await HandleDirectStream(ctx);
            else
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
            }
        }
        catch (HttpListenerException) { }
        catch (IOException) { }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warning(ex, "Media stream request error for {Url}", ctx.Request.RawUrl);
            try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
        }
    }

    // ── Direct video file streaming ──
    private async Task HandleDirectStream(HttpListenerContext ctx)
    {
        var serverId = ctx.Request.QueryString["server"];
        var remotePath = ctx.Request.QueryString["path"];

        if (string.IsNullOrEmpty(serverId) || string.IsNullOrEmpty(remotePath))
        {
            ctx.Response.StatusCode = 400;
            ctx.Response.Close();
            return;
        }

        var server = _serverManager.GetServer(serverId);
        if (server?.Pool == null || !server.Pool.IsConnected)
        {
            ctx.Response.StatusCode = 503;
            ctx.Response.Close();
            return;
        }

        // Get file size
        long fileSize;
        await using (var sizeConn = await server.Pool.Borrow(_cts.Token))
        {
            fileSize = await sizeConn.Client.GetFileSize(remotePath, -1, _cts.Token);
        }

        Log.Debug("Streaming {Path} (size={Size}) from server {Server}", remotePath, fileSize, serverId);

        // Parse Range header
        long offset = 0;
        var rangeHeader = ctx.Request.Headers["Range"];
        if (rangeHeader != null && rangeHeader.StartsWith("bytes="))
        {
            var parts = rangeHeader[6..].Split('-');
            if (long.TryParse(parts[0], out var start))
                offset = start;
        }

        // Set response headers
        ctx.Response.ContentType = GetVideoContentType(remotePath);
        ctx.Response.Headers.Add("Accept-Ranges", "bytes");

        if (offset > 0 && fileSize > 0)
        {
            ctx.Response.StatusCode = 206;
            ctx.Response.Headers.Add("Content-Range", $"bytes {offset}-{fileSize - 1}/{fileSize}");
            ctx.Response.ContentLength64 = fileSize - offset;
        }
        else
        {
            ctx.Response.StatusCode = 200;
            if (fileSize > 0) ctx.Response.ContentLength64 = fileSize;
        }

        // Stream from FTP
        await using var conn = await server.Pool.Borrow(_cts.Token);
        if (server.Pool.UseCpsv)
            await StreamCpsv(conn.Client, remotePath, offset, ctx.Response.OutputStream, _cts.Token);
        else
            await StreamStandard(conn.Client, remotePath, offset, ctx.Response.OutputStream, _cts.Token);

        ctx.Response.Close();
    }

    // ── RAR streaming: download volumes on-demand, extract video, pipe to HTTP ──
    private async Task HandleRarStream(HttpListenerContext ctx)
    {
        var serverId = ctx.Request.QueryString["server"];
        var releasePath = ctx.Request.QueryString["path"];

        if (string.IsNullOrEmpty(serverId) || string.IsNullOrEmpty(releasePath))
        {
            ctx.Response.StatusCode = 400;
            ctx.Response.Close();
            return;
        }

        var server = _serverManager.GetServer(serverId);
        if (server?.Pool == null || !server.Pool.IsConnected || server.Search == null)
        {
            ctx.Response.StatusCode = 503;
            ctx.Response.Close();
            return;
        }

        Log.Information("RAR stream request for {Path} on {Server}", releasePath, serverId);

        // List files and find RAR volumes
        var files = await server.Search.GetReleaseFiles(releasePath, _cts.Token);
        var volumes = files
            .Where(f => ArchiveExtractor.IsArchiveFile(f.Name) &&
                        !f.Name.EndsWith(".sfv", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => GetVolumeOrder(f.Name))
            .ToList();

        if (volumes.Count == 0)
        {
            Log.Warning("No RAR volumes found in {Path}", releasePath);
            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
            return;
        }

        Log.Information("Found {Count} RAR volumes in {Path}", volumes.Count, releasePath);

        // Create temp dir for volume cache
        var tempDir = Path.Combine(Path.GetTempPath(), "GlDrive", "rar-cache", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);

        try
        {
            // Download volumes to temp files (sequentially — .rar first, then .r00, .r01...)
            var tempFiles = new List<string>();
            foreach (var vol in volumes)
            {
                var localPath = Path.Combine(tempDir, vol.Name);
                Log.Information("Downloading RAR volume: {Name} ({Size} bytes)", vol.Name, vol.Size);

                var data = await server.Ftp!.DownloadFile(vol.FullName, _cts.Token);
                await File.WriteAllBytesAsync(localPath, data, _cts.Token);
                tempFiles.Add(localPath);

                // After downloading the first .rar volume, check if we can find the video entry
                // This lets us start streaming ASAP for single-volume RARs
                if (tempFiles.Count == 1 && volumes.Count == 1)
                    break;
            }

            // Open the archive from the first .rar file (SharpCompress auto-discovers volumes)
            var firstRar = tempFiles.First(f => f.EndsWith(".rar", StringComparison.OrdinalIgnoreCase));
            using var archive = RarArchive.OpenArchive(firstRar);

            // Find the largest video entry
            var videoEntry = archive.Entries
                .Where(e => !e.IsDirectory && IsVideoFile(e.Key ?? ""))
                .OrderByDescending(e => e.Size)
                .FirstOrDefault();

            if (videoEntry == null)
            {
                Log.Warning("No video file found in RAR archive at {Path}", releasePath);
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }

            var videoName = videoEntry.Key ?? "video";
            Log.Information("Streaming RAR entry: {Name} ({Size} bytes)", videoName, videoEntry.Size);

            // Set response headers
            ctx.Response.ContentType = GetVideoContentType(videoName);
            ctx.Response.StatusCode = 200;
            if (videoEntry.Size > 0)
                ctx.Response.ContentLength64 = videoEntry.Size;

            // Stream the decompressed video entry to HTTP
            await using var entryStream = videoEntry.OpenEntryStream();
            var buffer = new byte[256 * 1024];
            int read;
            while ((read = await entryStream.ReadAsync(buffer, _cts.Token)) > 0)
            {
                await ctx.Response.OutputStream.WriteAsync(buffer.AsMemory(0, read), _cts.Token);
                await ctx.Response.OutputStream.FlushAsync(_cts.Token);
            }

            ctx.Response.Close();
            Log.Information("RAR stream completed for {Name}", videoName);
        }
        finally
        {
            // Clean up temp files
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    /// <summary>
    /// Returns sort order for RAR volume names: .rar=0, .r00=1, .r01=2, .s00=100, etc.
    /// </summary>
    private static int GetVolumeOrder(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext == ".rar") return -1;
        if (ext.Length == 4 && (ext[1] == 'r' || ext[1] == 's') &&
            int.TryParse(ext[2..], out var num))
        {
            return ext[1] == 'r' ? num : 100 + num;
        }
        return 999;
    }

    private static string GetVideoContentType(string path)
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

    private static bool IsVideoFile(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext is ".mkv" or ".avi" or ".mp4" or ".m4v" or ".wmv" or ".mov"
            or ".mpg" or ".mpeg" or ".ts" or ".vob" or ".flv" or ".webm";
    }

    private static async Task StreamStandard(AsyncFtpClient client, string remotePath, long offset,
        Stream output, CancellationToken ct)
    {
        await using var ftpStream = await client.OpenRead(remotePath, FtpDataType.Binary, offset, token: ct);

        var buffer = new byte[256 * 1024];
        int read;
        while ((read = await ftpStream.ReadAsync(buffer, ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            await output.FlushAsync(ct);
        }

        ftpStream.Close();
        await client.GetReply(ct);
    }

    private static async Task StreamCpsv(AsyncFtpClient client, string remotePath, long offset,
        Stream output, CancellationToken ct)
    {
        await client.Execute("TYPE I", ct);
        if (offset > 0)
            await client.Execute($"REST {offset}", ct);

        var tcp = await CpsvDataHelper.OpenDataTcp(client, ct);
        try
        {
            var retrReply = await client.Execute($"RETR {remotePath}", ct);
            if (retrReply.Code != "150" && retrReply.Code != "125")
                throw new IOException($"RETR failed: {retrReply.Code} {retrReply.Message}");

            var ssl = await CpsvDataHelper.NegotiateDataTls(tcp.GetStream(), ct);
            try
            {
                var buffer = new byte[256 * 1024];
                int read;
                while ((read = await ssl.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    await output.FlushAsync(ct);
                }

                ssl.Close();
                tcp.Close();
                await client.GetReply(ct);
            }
            finally { ssl.Dispose(); }
        }
        finally { tcp.Dispose(); }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }
}
