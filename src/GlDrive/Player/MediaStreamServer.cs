using System.IO;
using System.Net;
using System.Net.Sockets;
using FluentFTP;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Ftp;
using GlDrive.Services;
using Serilog;
using SharpCompress.Archives.Rar;

namespace GlDrive.Player;

public class MediaStreamServer : IDisposable
{
    private readonly ServerManager _serverManager;
    private readonly AppConfig _config;
    private HttpListener? _listener;
    private CancellationTokenSource _cts = new();
    private readonly string _authToken = Guid.NewGuid().ToString("N");
    private int _port;

    public int Port => _port;
    public string BaseUrl => $"http://127.0.0.1:{_port}/";
    public string AuthToken => _authToken;

    /// <summary>
    /// The library directory where player caches downloaded/extracted video files.
    /// Uses %AppData%\GlDrive\Player to avoid depending on the download path (which may be on a mounted FTP drive).
    /// </summary>
    public string LibraryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GlDrive", "Player");

    public MediaStreamServer(ServerManager serverManager, AppConfig config)
    {
        _serverManager = serverManager;
        _config = config;
    }

    public void Start()
    {
        _port = FindFreePort();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
        _listener.Start();
        Directory.CreateDirectory(LibraryPath);
        _ = AcceptLoop();
        Log.Information("Media stream server started on port {Port}, library at {Path}", _port, LibraryPath);
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

            // Verify auth token on all requests
            if (ctx.Request.QueryString["token"] != _authToken)
            {
                ctx.Response.StatusCode = 403;
                ctx.Response.Close();
                return;
            }
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

    /// <summary>
    /// Checks the library for an already-cached video file for the given release.
    /// Returns the local path if found, null otherwise.
    /// </summary>
    public string? FindCachedVideo(string releaseName)
    {
        var releaseDir = Path.GetFullPath(Path.Combine(LibraryPath, SanitizeName(releaseName)));
        if (!IsWithinLibrary(releaseDir)) return null;
        if (!Directory.Exists(releaseDir)) return null;

        var files = Directory.GetFiles(releaseDir, "*", SearchOption.AllDirectories);

        // Prefer extracted video files over RAR archives
        var video = files
            .Where(f => IsVideoFile(f) && !f.EndsWith(".partial", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => new FileInfo(f).Length)
            .FirstOrDefault();

        return video;
    }

    // ── Direct video file streaming (saves to library as it streams) ──
    private async Task HandleDirectStream(HttpListenerContext ctx)
    {
        var serverId = ctx.Request.QueryString["server"];
        var remotePath = ctx.Request.QueryString["path"];
        var release = ctx.Request.QueryString["release"] ?? Path.GetFileName(Path.GetDirectoryName(remotePath) ?? "");

        if (string.IsNullOrEmpty(serverId) || string.IsNullOrEmpty(remotePath))
        {
            ctx.Response.StatusCode = 400;
            ctx.Response.Close();
            return;
        }

        // Check library cache first
        var fileName = Path.GetFileName(remotePath);
        var releaseDir = Path.GetFullPath(Path.Combine(LibraryPath, SanitizeName(release)));
        var cachedFile = IsWithinLibrary(releaseDir) ? ResolveInLibrary(releaseDir, fileName) : null;
        if (cachedFile == null)
        {
            ctx.Response.StatusCode = 400;
            ctx.Response.Close();
            return;
        }

        if (File.Exists(cachedFile))
        {
            Log.Information("Serving cached file: {Path}", cachedFile);
            await ServeLocalFile(ctx, cachedFile);
            return;
        }

        var server = _serverManager.GetServer(serverId);
        if (server?.Pool == null || !server.Pool.IsConnected)
        {
            Log.Warning("Stream request for {Path}: server {Id} not connected", remotePath, serverId);
            ctx.Response.StatusCode = 503;
            ctx.Response.Close();
            return;
        }

        // Get file size with timeout to avoid hanging
        long fileSize;
        using var sizeCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        sizeCts.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await using var sizeConn = await server.Pool.Borrow(sizeCts.Token);
            fileSize = await sizeConn.Client.GetFileSize(remotePath, -1, sizeCts.Token);
        }
        catch (OperationCanceledException)
        {
            Log.Warning("Stream request timed out getting file size for {Path}", remotePath);
            ctx.Response.StatusCode = 504;
            ctx.Response.Close();
            return;
        }

        Log.Debug("Streaming {Path} (size={Size}) from server {Server}", remotePath, fileSize, serverId);

        var rangeHeader = ctx.Request.Headers["Range"];
        if (!TryResolveRange(rangeHeader, fileSize, out var range))
        {
            ctx.Response.StatusCode = 416;
            if (fileSize > 0) ctx.Response.Headers.Add("Content-Range", $"bytes */{fileSize}");
            ctx.Response.Close();
            return;
        }

        // Set response headers
        ctx.Response.ContentType = GetVideoContentType(remotePath);
        ctx.Response.Headers.Add("Accept-Ranges", "bytes");

        if (range.IsPartial)
        {
            ctx.Response.StatusCode = 206;
            ctx.Response.Headers.Add("Content-Range", $"bytes {range.Offset}-{range.End}/{fileSize}");
            ctx.Response.ContentLength64 = range.Length!.Value;
        }
        else
        {
            ctx.Response.StatusCode = 200;
            if (range.Length.HasValue) ctx.Response.ContentLength64 = range.Length.Value;
        }

        // Stream from FTP, saving to library if streaming from the start
        Directory.CreateDirectory(releaseDir);
        var tempFile = cachedFile + ".partial";
        FileStream? saveStream = null;

        // Only save to library when streaming from the beginning (no seek)
        if (!range.IsPartial && range.Offset == 0)
        {
            try { saveStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None); }
            catch (Exception ex) { Log.Warning(ex, "Could not open cache file for writing"); }
        }

        bool moveSucceeded = false;
        try
        {
            using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            streamCts.CancelAfter(TimeSpan.FromSeconds(15)); // Timeout for connection borrow
            await using var conn = await server.Pool.Borrow(streamCts.Token);
            // Reset timeout — streaming can take as long as needed
            streamCts.CancelAfter(Timeout.InfiniteTimeSpan);
            if (server.Pool.UseCpsv)
                await StreamCpsv(conn.Client, remotePath, range.Offset, range.Length, ctx.Response.OutputStream, _cts.Token, saveStream);
            else
                await StreamStandard(conn.Client, remotePath, range.Offset, range.Length, ctx.Response.OutputStream, _cts.Token, saveStream);

            // Rename .partial to final when complete
            if (saveStream != null)
            {
                await saveStream.DisposeAsync();
                saveStream = null;
                try
                {
                    if (File.Exists(cachedFile)) File.Delete(cachedFile);
                    File.Move(tempFile, cachedFile);
                    moveSucceeded = true;
                    Log.Information("Cached video to library: {Path}", cachedFile);
                }
                catch (Exception ex) { Log.Warning(ex, "Failed to finalize cache file"); }
            }
        }
        finally
        {
            if (saveStream != null) await saveStream.DisposeAsync();
            // Only clean up partial file on failure — on success it was renamed to cachedFile.
            if (!moveSucceeded)
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
        }

        ctx.Response.Close();
    }

    // ── RAR streaming: download volumes, extract video to library, pipe to HTTP ──
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

        var releaseName = Path.GetFileName(releasePath);
        var releaseDir = Path.GetFullPath(Path.Combine(LibraryPath, SanitizeName(releaseName)));
        if (!IsWithinLibrary(releaseDir))
        {
            ctx.Response.StatusCode = 400;
            ctx.Response.Close();
            return;
        }

        // Check if we already have extracted video cached
        var cachedVideo = FindCachedVideo(releaseName);
        if (cachedVideo != null)
        {
            Log.Information("Serving cached RAR extraction: {Path}", cachedVideo);
            await ServeLocalFile(ctx, cachedVideo);
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

        // Download volumes to library folder (persistent cache)
        Directory.CreateDirectory(releaseDir);

        try
        {
            var tempFiles = new List<string>();
            foreach (var vol in volumes)
            {
                var localPath = ResolveInLibrary(releaseDir, vol.Name)
                    ?? throw new IOException($"Unsafe RAR volume name: {vol.Name}");
                if (!File.Exists(localPath))
                {
                    var volNum = tempFiles.Count + 1;
                    Log.Information("Downloading RAR volume {Num}/{Total}: {Name} ({Size} bytes)",
                        volNum, volumes.Count, vol.Name, vol.Size);

                    // Stream to disk — use CPSV for BNC servers, standard PASV otherwise
                    await using var conn = await server.Pool!.Borrow(_cts.Token);
                    var tempPath = localPath + ".partial";
                    try
                    {
                        await using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
                        if (server.Pool.UseCpsv)
                        {
                            await CpsvDataHelper.DownloadFileToStream(conn.Client, vol.FullName, fileStream, null, _cts.Token);
                        }
                        else
                        {
                            await using var ftpStream = await conn.Client.OpenRead(vol.FullName, FtpDataType.Binary, 0, token: _cts.Token);
                            var buf = new byte[256 * 1024];
                            int rd;
                            while ((rd = await ftpStream.ReadAsync(buf, _cts.Token)) > 0)
                                await fileStream.WriteAsync(buf.AsMemory(0, rd), _cts.Token);
                            ftpStream.Close();
                            await conn.Client.GetReply(_cts.Token);
                        }
                    }
                    catch
                    {
                        try { File.Delete(tempPath); } catch { }
                        throw;
                    }
                    File.Move(tempPath, localPath);
                    Log.Information("Volume {Num}/{Total} downloaded", volNum, volumes.Count);
                }
                else
                {
                    Log.Information("RAR volume already cached: {Name}", vol.Name);
                }
                tempFiles.Add(localPath);
            }

            // Open the archive and find the video entry
            var firstRar = tempFiles.First(f => f.EndsWith(".rar", StringComparison.OrdinalIgnoreCase));
            using var archive = RarArchive.OpenArchive(firstRar);

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

            var videoName = videoEntry.Key ?? "video.mkv";
            // Entry names come from a RAR fetched off a remote site — untrusted (zip-slip).
            var extractedPath = ResolveInLibrary(releaseDir, videoName)
                ?? throw new IOException($"Unsafe archive entry name: {videoName}");
            Log.Information("Extracting & streaming RAR entry: {Name} ({Size} bytes)", videoName, videoEntry.Size);

            // Set response headers
            ctx.Response.ContentType = GetVideoContentType(videoName);
            ctx.Response.StatusCode = 200;
            if (videoEntry.Size > 0)
                ctx.Response.ContentLength64 = videoEntry.Size;

            // Stream decompressed entry to HTTP AND save to library
            await using var entryStream = videoEntry.OpenEntryStream();
            FileStream? saveStream = null;
            var tempExtractPath = extractedPath + ".partial";
            try { saveStream = new FileStream(tempExtractPath, FileMode.Create, FileAccess.Write, FileShare.None); }
            catch (Exception ex) { Log.Warning(ex, "Could not create extraction cache file"); }

            try
            {
                var buffer = new byte[256 * 1024];
                int read;
                while ((read = await entryStream.ReadAsync(buffer, _cts.Token)) > 0)
                {
                    await ctx.Response.OutputStream.WriteAsync(buffer.AsMemory(0, read), _cts.Token);
                    await ctx.Response.OutputStream.FlushAsync(_cts.Token);
                    if (saveStream != null)
                        await saveStream.WriteAsync(buffer.AsMemory(0, read), _cts.Token);
                }

                // Finalize the cached extraction
                if (saveStream != null)
                {
                    await saveStream.DisposeAsync();
                    saveStream = null;
                    try
                    {
                        if (File.Exists(extractedPath)) File.Delete(extractedPath);
                        File.Move(tempExtractPath, extractedPath);
                        Log.Information("Cached extracted video to library: {Path}", extractedPath);

                        // Delete RAR volumes now that we have the extracted video
                        foreach (var tf in tempFiles)
                        {
                            try { File.Delete(tf); } catch { }
                        }
                    }
                    catch (Exception ex) { Log.Warning(ex, "Failed to finalize extraction cache"); }
                }
            }
            finally
            {
                if (saveStream != null) await saveStream.DisposeAsync();
                try { if (File.Exists(tempExtractPath)) File.Delete(tempExtractPath); } catch { }
            }

            ctx.Response.Close();
            Log.Information("RAR stream completed for {Name}", videoName);
        }
        catch (Exception ex) when (ex is not HttpListenerException and not OperationCanceledException)
        {
            Log.Warning(ex, "RAR stream failed for {Path}", releasePath);
            throw;
        }
    }

    /// <summary>
    /// Serve a local file with Range header support.
    /// </summary>
    private static async Task ServeLocalFile(HttpListenerContext ctx, string filePath)
    {
        var fi = new FileInfo(filePath);
        var fileSize = fi.Length;

        var rangeHeader = ctx.Request.Headers["Range"];
        if (!TryResolveRange(rangeHeader, fileSize, out var range))
        {
            ctx.Response.StatusCode = 416;
            if (fileSize > 0) ctx.Response.Headers.Add("Content-Range", $"bytes */{fileSize}");
            ctx.Response.Close();
            return;
        }

        ctx.Response.ContentType = GetVideoContentType(filePath);
        ctx.Response.Headers.Add("Accept-Ranges", "bytes");

        var length = range.Length!.Value;
        if (range.IsPartial)
        {
            ctx.Response.StatusCode = 206;
            ctx.Response.Headers.Add("Content-Range", $"bytes {range.Offset}-{range.End}/{fileSize}");
            ctx.Response.ContentLength64 = length;
        }
        else
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentLength64 = fileSize;
        }

        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (range.Offset > 0) fs.Seek(range.Offset, SeekOrigin.Begin);

        // Cap reads to the validated [offset, end] window so a `bytes=N-M` request doesn't over-send.
        var remaining = length;
        var buffer = new byte[256 * 1024];
        int read;
        while (remaining > 0 &&
               (read = await fs.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)))) > 0)
        {
            await ctx.Response.OutputStream.WriteAsync(buffer.AsMemory(0, read));
            await ctx.Response.OutputStream.FlushAsync();
            remaining -= read;
        }

        ctx.Response.Close();
    }

    internal readonly record struct ResolvedRange(long Offset, long End, long? Length, bool IsPartial);

    internal static bool TryResolveRange(string? header, long fileSize, out ResolvedRange range)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            range = new ResolvedRange(0, Math.Max(0, fileSize - 1), fileSize >= 0 ? fileSize : null, false);
            return true;
        }

        if (fileSize <= 0 || !header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            range = default;
            return false;
        }

        var spec = header[6..].Trim();
        if (spec.Length == 0 || spec.Contains(','))
        {
            range = default;
            return false;
        }

        var parts = spec.Split('-', 2);
        long start;
        long end;
        if (parts.Length != 2)
        {
            range = default;
            return false;
        }

        if (parts[0].Length == 0)
        {
            if (!long.TryParse(parts[1], out var suffixLength) || suffixLength <= 0)
            {
                range = default;
                return false;
            }
            suffixLength = Math.Min(suffixLength, fileSize);
            start = fileSize - suffixLength;
            end = fileSize - 1;
        }
        else
        {
            if (!long.TryParse(parts[0], out start) || start < 0 || start >= fileSize)
            {
                range = default;
                return false;
            }
            if (parts[1].Length == 0)
            {
                end = fileSize - 1;
            }
            else if (!long.TryParse(parts[1], out end) || end < start)
            {
                range = default;
                return false;
            }
            end = Math.Min(end, fileSize - 1);
        }

        range = new ResolvedRange(start, end, end - start + 1, true);
        return true;
    }

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

    private static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = string.Concat(name.Select(c =>
            (invalid.Contains(c) || c < 0x20 || c == '/' || c == '\\') ? '_' : c));
        // Reject anything that is only dots/spaces ("", ".", "..", "...", ". ") — Windows trims
        // trailing dots and spaces, so those can still resolve to the parent directory.
        if (sanitized.Trim(' ', '.').Length == 0 || sanitized.Contains('/') || sanitized.Contains('\\'))
            return "_invalid_";
        return sanitized;
    }

    private bool IsWithinLibrary(string fullPath) =>
        fullPath.StartsWith(Path.GetFullPath(LibraryPath) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves <paramref name="fileName"/> inside <paramref name="releaseDir"/> and re-asserts that the
    /// result is still under the library, returning null if it escapes. Load-bearing: Path.GetFileName
    /// strips separators but still returns ".." for input like "foo/..", and RAR entry names arrive from
    /// a remote archive (zip-slip). Containment must be checked on the FINAL path, not just the directory.
    /// </summary>
    private string? ResolveInLibrary(string releaseDir, string fileName)
    {
        var full = Path.GetFullPath(Path.Combine(releaseDir, SanitizeName(Path.GetFileName(fileName))));
        return IsWithinLibrary(full) ? full : null;
    }

    private static async Task StreamStandard(AsyncFtpClient client, string remotePath, long offset,
        long? maxBytes, Stream output, CancellationToken ct, FileStream? saveStream = null)
    {
        await using var ftpStream = await client.OpenRead(remotePath, FtpDataType.Binary, offset, token: ct);

        var buffer = new byte[256 * 1024];
        var remaining = maxBytes ?? long.MaxValue;
        int read;
        while (remaining > 0 &&
               (read = await ftpStream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            await output.FlushAsync(ct);
            if (saveStream != null)
                await saveStream.WriteAsync(buffer.AsMemory(0, read), ct);
            remaining -= read;
        }

        ftpStream.Close();
        await client.GetReply(ct);
    }

    private static async Task StreamCpsv(AsyncFtpClient client, string remotePath, long offset,
        long? maxBytes, Stream output, CancellationToken ct, FileStream? saveStream = null)
    {
        await client.Execute("TYPE I", ct);
        if (offset > 0)
            await client.Execute($"REST {offset}", ct);

        var tcp = await CpsvDataHelper.OpenDataTcp(client, ct);
        try
        {
            var retrReply = await client.Execute($"RETR {CpsvDataHelper.SanitizeFtpPath(remotePath)}", ct);
            if (retrReply.Code != "150" && retrReply.Code != "125")
                throw new IOException($"RETR failed: {retrReply.Code} {retrReply.Message}");

            var ssl = await CpsvDataHelper.NegotiateDataTls(tcp.GetStream(), ct);
            try
            {
                var buffer = new byte[256 * 1024];
                var remaining = maxBytes ?? long.MaxValue;
                int read;
                while (remaining > 0 &&
                       (read = await ssl.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    await output.FlushAsync(ct);
                    if (saveStream != null)
                        await saveStream.WriteAsync(buffer.AsMemory(0, read), ct);
                    remaining -= read;
                }

                ssl.Close();
                tcp.Close();
                await client.GetReply(ct);
            }
            finally { ssl.Dispose(); }
        }
        finally { tcp.Dispose(); }
    }

    /// <summary>
    /// Downloads RAR volumes to the library folder.
    /// Invokes onPlayReady with the first .rar path as soon as it's downloaded.
    /// Remaining volumes download in background so VLC finds them as needed.
    /// </summary>
    public async Task<string?> DownloadAndExtractRar(
        MountService server, string releasePath, List<FtpListItem> files,
        Action<string, int>? onProgress = null, Action<string>? onPlayReady = null, CancellationToken ct = default)
    {
        var releaseName = Path.GetFileName(releasePath);
        var releaseDir = Path.GetFullPath(Path.Combine(LibraryPath, SanitizeName(releaseName)));
        if (!IsWithinLibrary(releaseDir)) return null;

        // Check cache — if first .rar or extracted video exists, return it
        var cached = FindCachedVideo(releaseName);
        if (cached != null) return cached;

        var volumes = files
            .Where(f => ArchiveExtractor.IsArchiveFile(f.Name) &&
                        !f.Name.EndsWith(".sfv", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => GetVolumeOrder(f.Name))
            .ToList();

        if (volumes.Count == 0) return null;

        Directory.CreateDirectory(releaseDir);
        var totalBytes = volumes.Sum(v => v.Size);
        long downloadedBytes = 0;
        bool playSignaled = false;
        string? firstRarPath = null;

        // Download volumes sequentially
        for (int i = 0; i < volumes.Count; i++)
        {
            var vol = volumes[i];
            var localPath = ResolveInLibrary(releaseDir, vol.Name)
                ?? throw new IOException($"Unsafe RAR volume name: {vol.Name}");

            if (firstRarPath == null && localPath.EndsWith(".rar", StringComparison.OrdinalIgnoreCase))
                firstRarPath = localPath;

            if (File.Exists(localPath))
            {
                downloadedBytes += vol.Size;
                onProgress?.Invoke($"Volume {i + 1}/{volumes.Count} cached", totalBytes > 0 ? (int)(downloadedBytes * 100 / totalBytes) : 0);

                // Signal VLC as soon as first .rar is ready
                if (!playSignaled && localPath.EndsWith(".rar", StringComparison.OrdinalIgnoreCase))
                {
                    playSignaled = true;
                    onPlayReady?.Invoke(localPath);
                }
                continue;
            }

            var tempPath = localPath + ".partial";

            // Retry the entire borrow+download up to 5 times
            for (int attempt = 0; ; attempt++)
            {
                onProgress?.Invoke($"Downloading {i + 1}/{volumes.Count}: {Path.GetFileName(vol.Name)}", totalBytes > 0 ? (int)(downloadedBytes * 100 / totalBytes) : 0);

                PooledConnection? conn = null;
                for (int wait = 0; wait < 60 && conn == null; wait++)
                {
                    try
                    {
                        using var borrowCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        borrowCts.CancelAfter(TimeSpan.FromSeconds(2));
                        conn = await server.Pool!.Borrow(borrowCts.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        onProgress?.Invoke($"Waiting for FTP connection... {wait + 1}s", totalBytes > 0 ? (int)(downloadedBytes * 100 / totalBytes) : 0);
                    }
                    catch (Exception) when (!ct.IsCancellationRequested)
                    {
                        onProgress?.Invoke($"Connection error, retrying... {wait + 1}s", totalBytes > 0 ? (int)(downloadedBytes * 100 / totalBytes) : 0);
                        await Task.Delay(1000, ct);
                    }
                }
                if (conn == null)
                    throw new IOException("No FTP connection available — pause other downloads and retry");

                var volStartBytes = downloadedBytes;
                try
                {
                    await using var _ = conn;
                    await using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);

                    if (server.Pool!.UseCpsv)
                    {
                        await CpsvDataHelper.DownloadFileToStream(conn.Client, vol.FullName, fileStream,
                            bytesRead =>
                            {
                                downloadedBytes += bytesRead - (downloadedBytes - volStartBytes);
                                var pct = totalBytes > 0 ? (int)(downloadedBytes * 100 / totalBytes) : 0;
                                onProgress?.Invoke($"Downloading {i + 1}/{volumes.Count} — {pct}%", pct);
                            }, ct);
                    }
                    else
                    {
                        await using var ftpStream = await conn.Client.OpenRead(vol.FullName, FtpDataType.Binary, 0, token: ct);
                        var buf = new byte[256 * 1024];
                        int rd;
                        while ((rd = await ftpStream.ReadAsync(buf, ct)) > 0)
                        {
                            await fileStream.WriteAsync(buf.AsMemory(0, rd), ct);
                            downloadedBytes += rd;
                            var pct = totalBytes > 0 ? (int)(downloadedBytes * 100 / totalBytes) : 0;
                            onProgress?.Invoke($"Downloading {i + 1}/{volumes.Count} — {pct}%", pct);
                        }
                        ftpStream.Close();
                        await conn.Client.GetReply(ct);
                    }
                    break;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (attempt < 4 && !ct.IsCancellationRequested)
                {
                    try { File.Delete(tempPath); } catch { }
                    onProgress?.Invoke($"Download failed ({ex.Message}), retry {attempt + 1}/5...", totalBytes > 0 ? (int)(downloadedBytes * 100 / totalBytes) : 0);
                    Log.Warning(ex, "Volume download attempt {Attempt} failed, retrying", attempt + 1);
                    await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)), ct);
                }
                catch
                {
                    try { File.Delete(tempPath); } catch { }
                    throw;
                }
            }
            File.Move(tempPath, localPath);

            // Signal VLC to start playing as soon as first .rar volume is downloaded
            if (!playSignaled && localPath.EndsWith(".rar", StringComparison.OrdinalIgnoreCase))
            {
                playSignaled = true;
                onPlayReady?.Invoke(localPath);
            }
        }

        onProgress?.Invoke("All volumes downloaded", 100);
        return firstRarPath;
    }

    public void Dispose()
    {
        _cts.Cancel();
        // Stop then Dispose the listener to release the HTTP.sys URL reservation; null-guarded so a
        // double Dispose is a no-op (Stop/Close already torn down → _listener is null on the 2nd call).
        try { _listener?.Stop(); } catch { }
        // HttpListener.Close() releases the HTTP.sys URL reservation and underlying handle;
        // Dispose() is an explicit interface impl (not callable on the concrete type) and is
        // equivalent to Close(), so Close alone is the correct teardown. Null-guarded for double-Dispose.
        try { _listener?.Close(); } catch { }
        _listener = null;
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }
}
