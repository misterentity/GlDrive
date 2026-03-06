using System.IO;
using System.Net;
using System.Net.Sockets;
using FluentFTP;
using GlDrive.Ftp;
using GlDrive.Services;
using Serilog;

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
            var ext = Path.GetExtension(remotePath).ToLowerInvariant();
            ctx.Response.ContentType = ext switch
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
        catch (HttpListenerException) { } // Client disconnected
        catch (IOException) { } // Client disconnected
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Debug(ex, "Media stream request error");
            try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
        }
    }

    private static async Task StreamStandard(AsyncFtpClient client, string remotePath, long offset,
        Stream output, CancellationToken ct)
    {
        await using var ftpStream = offset > 0
            ? await client.OpenRead(remotePath, FtpDataType.Binary, offset, token: ct)
            : await client.OpenRead(remotePath, token: ct);

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
