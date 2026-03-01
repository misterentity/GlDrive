using System.Diagnostics;
using System.IO;
using GlDrive.Downloads;
using FluentFTP;
using Serilog;

namespace GlDrive.Ftp;

public class StreamingDownloader
{
    private const int BufferSize = 256 * 1024; // 256KB
    private readonly FtpConnectionPool _pool;

    public StreamingDownloader(FtpConnectionPool pool) => _pool = pool;

    public async Task DownloadToFile(
        string remotePath, string localPath, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);

        await using var conn = await _pool.Borrow(ct);
        var client = conn.Client;

        Log.Information("Streaming download: {Remote} -> {Local}", remotePath, localPath);

        if (_pool.UseCpsv)
            await DownloadCpsv(client, remotePath, localPath, progress, ct);
        else
            await DownloadStandard(client, remotePath, localPath, progress, ct);

        Log.Information("Download complete: {Local}", localPath);
    }

    private async Task DownloadStandard(
        AsyncFtpClient client, string remotePath, string localPath,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var size = await client.GetFileSize(remotePath, -1, ct);
        await using var remoteStream = await client.OpenRead(remotePath, token: ct);
        await using var fileStream = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, true);

        await CopyWithProgress(remoteStream, fileStream, size, progress, ct);

        remoteStream.Close();
        var reply = await client.GetReply(ct);
        Log.Debug("RETR complete: {Code} {Message}", reply.Code, reply.Message);
    }

    private async Task DownloadCpsv(
        AsyncFtpClient client, string remotePath, string localPath,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        // Get file size first
        var sizeReply = await client.Execute($"SIZE {remotePath}", ct);
        long size = -1;
        if (sizeReply.Success && long.TryParse(sizeReply.Message.Trim(), out var parsed))
            size = parsed;

        await client.Execute("TYPE I", ct);
        var tcp = await CpsvDataHelper.OpenDataTcp(client, ct);
        try
        {
            var retrReply = await client.Execute($"RETR {remotePath}", ct);
            if (retrReply.Code != "150" && retrReply.Code != "125")
                throw new IOException($"RETR failed: {retrReply.Code} {retrReply.Message}");

            var ssl = await CpsvDataHelper.NegotiateDataTls(tcp.GetStream(), ct);
            try
            {
                await using var fileStream = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, true);
                await CopyWithProgress(ssl, fileStream, size, progress, ct);

                ssl.Close();
                tcp.Close();
                var completeReply = await client.GetReply(ct);
                Log.Debug("RETR complete: {Code} {Message}", completeReply.Code, completeReply.Message);
            }
            finally { ssl.Dispose(); }
        }
        finally { tcp.Dispose(); }
    }

    private static async Task CopyWithProgress(
        Stream source, Stream destination, long totalSize,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var buffer = new byte[BufferSize];
        long totalRead = 0;
        var sw = Stopwatch.StartNew();
        int bytesRead;

        while ((bytesRead = await source.ReadAsync(buffer.AsMemory(0, BufferSize), ct)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            totalRead += bytesRead;

            var elapsed = sw.Elapsed.TotalSeconds;
            var speed = elapsed > 0 ? totalRead / elapsed : 0;
            progress?.Report(new DownloadProgress(totalRead, totalSize, speed));
        }
    }
}
