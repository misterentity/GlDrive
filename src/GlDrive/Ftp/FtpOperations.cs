using System.IO;
using System.Text.RegularExpressions;
using FluentFTP;
using Serilog;

namespace GlDrive.Ftp;

public class FtpOperations
{
    private static readonly Regex DiskTotalRegex = new(@"Total:\s*(\d+)\s*(KB|MB|GB|TB)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DiskFreeRegex = new(@"Free:\s*(\d+)\s*(KB|MB|GB|TB)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly FtpConnectionPool _pool;

    public FtpOperations(FtpConnectionPool pool)
    {
        _pool = pool;
    }

    public async Task<FtpListItem[]> ListDirectory(string remotePath, CancellationToken ct = default)
    {
        await using var conn = await _pool.Borrow(ct);
        Log.Debug("LIST {Path}", remotePath);

        if (_pool.UseCpsv)
            return await CpsvDataHelper.ListDirectory(conn.Client, remotePath, _pool.ControlHost, ct);

        var items = await conn.Client.GetListing(remotePath, FtpListOption.AllFiles, ct);
        return items;
    }

    public async Task<bool> FileExists(string remotePath, CancellationToken ct = default)
    {
        // FileExists uses SIZE command (control-only, no data connection)
        await using var conn = await _pool.Borrow(ct);
        return await conn.Client.FileExists(remotePath, ct);
    }

    public async Task<bool> DirectoryExists(string remotePath, CancellationToken ct = default)
    {
        // DirectoryExists uses CWD command (control-only)
        await using var conn = await _pool.Borrow(ct);
        return await conn.Client.DirectoryExists(remotePath, ct);
    }

    public async Task<byte[]> DownloadFile(string remotePath, CancellationToken ct = default)
    {
        await using var conn = await _pool.Borrow(ct);
        Log.Debug("RETR {Path}", remotePath);

        if (_pool.UseCpsv)
            return await CpsvDataHelper.DownloadFile(conn.Client, remotePath, _pool.ControlHost, ct);

        using var ms = new MemoryStream();
        var ok = await conn.Client.DownloadStream(ms, remotePath, token: ct);
        if (!ok)
            throw new IOException($"Failed to download {remotePath}");
        return ms.ToArray();
    }

    public async Task<Stream> DownloadFileStream(string remotePath, CancellationToken ct = default)
    {
        var data = await DownloadFile(remotePath, ct);
        return new MemoryStream(data, false);
    }

    public async Task UploadFile(string remotePath, byte[] data, CancellationToken ct = default)
    {
        await using var conn = await _pool.Borrow(ct);
        Log.Debug("STOR {Path} ({Bytes} bytes)", remotePath, data.Length);

        if (_pool.UseCpsv)
        {
            await CpsvDataHelper.UploadFile(conn.Client, remotePath, data, _pool.ControlHost, ct);
            return;
        }

        using var ms = new MemoryStream(data);
        var status = await conn.Client.UploadStream(ms, remotePath, FtpRemoteExists.Overwrite, true, null, ct);
        if (status != FtpStatus.Success)
            throw new IOException($"Failed to upload {remotePath}: {status}");
    }

    public async Task UploadFile(string remotePath, Stream stream, CancellationToken ct = default)
    {
        await using var conn = await _pool.Borrow(ct);
        Log.Debug("STOR {Path} (stream)", remotePath);

        if (_pool.UseCpsv)
        {
            await CpsvDataHelper.UploadFileStream(conn.Client, remotePath, stream, _pool.ControlHost, ct);
            return;
        }

        if (!stream.CanSeek)
        {
            // FluentFTP needs seekable stream — buffer only if necessary
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct);
            ms.Position = 0;
            var status = await conn.Client.UploadStream(ms, remotePath, FtpRemoteExists.Overwrite, true, null, ct);
            if (status != FtpStatus.Success)
                throw new IOException($"Failed to upload {remotePath}: {status}");
        }
        else
        {
            stream.Position = 0;
            var status = await conn.Client.UploadStream(stream, remotePath, FtpRemoteExists.Overwrite, true, null, ct);
            if (status != FtpStatus.Success)
                throw new IOException($"Failed to upload {remotePath}: {status}");
        }
    }

    public async Task DeleteFile(string remotePath, CancellationToken ct = default)
    {
        await using var conn = await _pool.Borrow(ct);
        Log.Debug("DELE {Path}", remotePath);
        await conn.Client.DeleteFile(remotePath, ct);
    }

    public async Task CreateDirectory(string remotePath, CancellationToken ct = default)
    {
        await using var conn = await _pool.Borrow(ct);
        Log.Debug("MKD {Path}", remotePath);
        await conn.Client.CreateDirectory(remotePath, ct);
    }

    public async Task DeleteDirectory(string remotePath, CancellationToken ct = default)
    {
        await using var conn = await _pool.Borrow(ct);
        Log.Debug("RMD {Path}", remotePath);
        await conn.Client.DeleteDirectory(remotePath, ct);
    }

    public async Task Rename(string fromPath, string toPath, CancellationToken ct = default)
    {
        await using var conn = await _pool.Borrow(ct);
        Log.Debug("RNFR {From} RNTO {To}", fromPath, toPath);
        await conn.Client.Rename(fromPath, toPath, ct);
    }

    public async Task<long> GetFileSize(string remotePath, CancellationToken ct = default)
    {
        await using var conn = await _pool.Borrow(ct);
        return await conn.Client.GetFileSize(remotePath, -1, ct);
    }

    public async Task<DateTime> GetModifiedTime(string remotePath, CancellationToken ct = default)
    {
        await using var conn = await _pool.Borrow(ct);
        return await conn.Client.GetModifiedTime(remotePath, ct);
    }

    public async Task<bool> Noop(CancellationToken ct = default)
    {
        try
        {
            await using var conn = await _pool.Borrow(ct);
            await conn.Client.Execute("NOOP", ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Try SITE DISKFREE to get server disk usage. Returns (totalBytes, freeBytes) or null if unsupported.
    /// glftpd responds: "200 Total: X MB Free: Y MB"
    /// </summary>
    public async Task<(long totalBytes, long freeBytes)?> GetDiskFree(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            await using var conn = await _pool.Borrow(cts.Token);
            var reply = await conn.Client.Execute("SITE DISKFREE", cts.Token);
            if (!reply.Success) return null;

            // Parse glftpd format: "200 Total: 1234 MB Free: 567 MB"
            // Also handle KB/GB variants
            var msg = reply.Message;
            var totalMatch = DiskTotalRegex.Match(msg);
            var freeMatch = DiskFreeRegex.Match(msg);

            if (!totalMatch.Success || !freeMatch.Success) return null;

            var total = ParseDiskSize(long.Parse(totalMatch.Groups[1].Value), totalMatch.Groups[2].Value);
            var free = ParseDiskSize(long.Parse(freeMatch.Groups[1].Value), freeMatch.Groups[2].Value);
            return (total, free);
        }
        catch
        {
            return null;
        }
    }

    private static long ParseDiskSize(long value, string unit)
    {
        if (string.Equals(unit, "KB", StringComparison.OrdinalIgnoreCase)) return value * 1024;
        if (string.Equals(unit, "MB", StringComparison.OrdinalIgnoreCase)) return value * 1024 * 1024;
        if (string.Equals(unit, "GB", StringComparison.OrdinalIgnoreCase)) return value * 1024L * 1024 * 1024;
        if (string.Equals(unit, "TB", StringComparison.OrdinalIgnoreCase)) return value * 1024L * 1024 * 1024 * 1024;
        return value;
    }
}
