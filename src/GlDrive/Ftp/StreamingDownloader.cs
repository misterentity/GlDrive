using System.Diagnostics;
using System.IO;
using GlDrive.Downloads;
using FluentFTP;
using Serilog;

namespace GlDrive.Ftp;

public class StreamingDownloader
{
    private readonly int _bufferSize;
    private readonly int _writeBufferLimitBytes;
    private readonly long _speedLimitBytesPerSecond;
    private readonly FtpConnectionPool _pool;

    public StreamingDownloader(FtpConnectionPool pool, int bufferSizeKb = 256, int writeBufferLimitMb = 0, int speedLimitKbps = 0)
    {
        _pool = pool;
        _bufferSize = Math.Clamp(bufferSizeKb, 64, 4096) * 1024;
        _writeBufferLimitBytes = writeBufferLimitMb > 0 ? writeBufferLimitMb * 1024 * 1024 : 0;
        _speedLimitBytesPerSecond = speedLimitKbps > 0 ? speedLimitKbps * 1024L : 0;
    }

    public async Task DownloadToFile(
        string remotePath, string localPath, long resumeOffset,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);

        await using var conn = await _pool.Borrow(ct);
        var client = conn.Client;

        if (resumeOffset > 0)
            Log.Information("Resuming download at {Offset}: {Remote} -> {Local}", resumeOffset, remotePath, localPath);
        else
            Log.Information("Streaming download: {Remote} -> {Local}", remotePath, localPath);

        if (_pool.UseCpsv)
            await DownloadCpsv(client, remotePath, localPath, resumeOffset, progress, ct);
        else
            await DownloadStandard(client, remotePath, localPath, resumeOffset, progress, ct);

        Log.Information("Download complete: {Local}", localPath);
    }

    private async Task DownloadStandard(
        AsyncFtpClient client, string remotePath, string localPath,
        long resumeOffset, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var size = await client.GetFileSize(remotePath, -1, ct);
        await using var remoteStream = resumeOffset > 0
            ? await client.OpenRead(remotePath, FtpDataType.Binary, resumeOffset, token: ct)
            : await client.OpenRead(remotePath, token: ct);
        var fileMode = resumeOffset > 0 ? FileMode.Append : FileMode.Create;
        await using var fileStream = new FileStream(localPath, fileMode, FileAccess.Write, FileShare.None, _bufferSize, true);

        await CopyWithProgress(remoteStream, fileStream, size, resumeOffset, progress, ct);

        remoteStream.Close();
        var reply = await client.GetReply(ct);
        Log.Debug("RETR complete: {Code} {Message}", reply.Code, reply.Message);
    }

    private async Task DownloadCpsv(
        AsyncFtpClient client, string remotePath, string localPath,
        long resumeOffset, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var sizeReply = await client.Execute($"SIZE {CpsvDataHelper.SanitizeFtpPath(remotePath)}", ct);
        long size = -1;
        if (sizeReply.Success && long.TryParse(sizeReply.Message.Trim(), out var parsed))
            size = parsed;

        await client.Execute("TYPE I", ct);

        if (resumeOffset > 0)
        {
            var restReply = await client.Execute($"REST {resumeOffset}", ct);
            if (!restReply.Success)
            {
                Log.Warning("REST command failed: {Code} {Message} — downloading from start", restReply.Code, restReply.Message);
                resumeOffset = 0; // Reset so FileMode.Create is used instead of Append
            }
        }

        var tcp = await CpsvDataHelper.OpenDataTcp(client, ct);
        try
        {
            var retrReply = await client.Execute($"RETR {CpsvDataHelper.SanitizeFtpPath(remotePath)}", ct);
            if (retrReply.Code != "150" && retrReply.Code != "125")
                throw new IOException($"RETR failed: {retrReply.Code} {retrReply.Message}");

            var ssl = await CpsvDataHelper.NegotiateDataTls(tcp.GetStream(), ct);
            try
            {
                var fileMode = resumeOffset > 0 ? FileMode.Append : FileMode.Create;
                await using var fileStream = new FileStream(localPath, fileMode, FileAccess.Write, FileShare.None, _bufferSize, true);
                await CopyWithProgress(ssl, fileStream, size, resumeOffset, progress, ct);

                ssl.Close();
                tcp.Close();
                var completeReply = await client.GetReply(ct);
                Log.Debug("RETR complete: {Code} {Message}", completeReply.Code, completeReply.Message);
            }
            finally { ssl.Dispose(); }
        }
        finally { tcp.Dispose(); }
    }

    private async Task CopyWithProgress(
        Stream source, Stream destination, long totalSize, long initialOffset,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var buffer = new byte[_bufferSize];
        long totalRead = initialOffset;
        var sw = Stopwatch.StartNew();
        int bytesRead;

        while ((bytesRead = await source.ReadAsync(buffer.AsMemory(0, _bufferSize), ct)) > 0)
        {
            if (_writeBufferLimitBytes > 0 && totalRead + bytesRead > _writeBufferLimitBytes)
                throw new IOException($"Write buffer limit exceeded ({_writeBufferLimitBytes / (1024 * 1024)} MB)");

            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            totalRead += bytesRead;

            var elapsed = sw.Elapsed.TotalSeconds;
            var speed = elapsed > 0 ? (totalRead - initialOffset) / elapsed : 0;
            progress?.Report(new DownloadProgress(totalRead, totalSize, speed));

            // Speed limiting
            if (_speedLimitBytesPerSecond > 0 && elapsed > 0)
            {
                var expectedTime = (double)(totalRead - initialOffset) / _speedLimitBytesPerSecond;
                var delayMs = (expectedTime - elapsed) * 1000;
                if (delayMs > 10)
                    await Task.Delay((int)Math.Min(delayMs, 1000), ct);
            }
        }
    }
}
