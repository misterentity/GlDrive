using System.IO;
using System.Text.RegularExpressions;
using FluentFTP;
using FluentFTP.Exceptions;
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
        return await ListDirectory(conn, remotePath, ct);
    }

    // Some callers need the completion reply on the SAME borrowed connection
    // (e.g. account credits in a LIST trailer). Keep protocol selection and
    // failure quarantine identical to ordinary directory operations.
    internal async Task<FtpListItem[]> ListDirectory(PooledConnection conn, string remotePath,
        CancellationToken ct = default)
    {
        try
        {
            Log.Debug("LIST {Path}", remotePath);

            if (_pool.UseCpsv)
                return await CpsvDataHelper.ListDirectory(conn.Client, remotePath, _pool.ControlHost, ct);

            return await FtpListing.GetListing(conn.Client, remotePath, ct);
        }
        catch (Exception ex) when (!FtpCommandRejection.IsClean(ex))
        {
            // Data-channel failure (TLS error, cancellation, IOException, etc.) may leave
            // the control channel with an unread completion reply. Returning this connection
            // to the pool would desync subsequent ops (e.g., CPSV reply reading TYPE A's
            // stale response). Poison so the pool discards it. A clean final rejection
            // (LIST 550 / 425) is exempt: the reply IS the completion, the channel is in
            // sync, and CpsvDataHelper already cleared its pending mark.
            conn.Poison($"LIST {remotePath}: {ex.GetType().Name}");
            throw;
        }
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
        var conn = await _pool.Borrow(ct);
        try
        {
            Log.Debug("RETR {Path}", remotePath);

            if (_pool.UseCpsv)
                return await CpsvDataHelper.DownloadFile(conn.Client, remotePath, _pool.ControlHost, ct);

            using var ms = new MemoryStream();
            var ok = await conn.Client.DownloadStream(ms, remotePath, token: ct);
            if (!ok)
                throw new IOException($"Failed to download {remotePath}");
            return ms.ToArray();
        }
        catch (Exception ex) when (!FtpCommandRejection.IsClean(ex))
        {
            conn.Poison($"RETR {remotePath}: {ex.GetType().Name}");
            throw;
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }

    public async Task<Stream> DownloadFileStream(string remotePath, CancellationToken ct = default)
    {
        var data = await DownloadFile(remotePath, ct);
        return new MemoryStream(data, false);
    }

    public async Task DownloadToStream(string remotePath, Stream destination, CancellationToken ct = default)
    {
        await using var conn = await _pool.Borrow(ct);
        conn.Poison("RETR-to-stream abandoned before completion");
        try
        {
            if (_pool.UseCpsv)
                await CpsvDataHelper.DownloadFileToStream(conn.Client, remotePath, destination, null, ct);
            else if (!await conn.Client.DownloadStream(destination, remotePath, token: ct))
                throw new IOException($"Failed to download {remotePath}");
        }
        catch (Exception ex) when (FtpCommandRejection.IsClean(ex))
        {
            conn.Poisoned = false;
            throw;
        }
        conn.Poisoned = false;
    }

    public async Task UploadFile(string remotePath, byte[] data, CancellationToken ct = default)
    {
        var conn = await _pool.Borrow(ct);
        try
        {
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
        catch (Exception ex) when (!FtpCommandRejection.IsClean(ex))
        {
            conn.Poison($"STOR {remotePath}: {ex.GetType().Name}");
            throw;
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }

    public async Task UploadFile(string remotePath, Stream stream, CancellationToken ct = default)
    {
        var conn = await _pool.Borrow(ct);
        try
        {
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
        catch (Exception ex) when (!FtpCommandRejection.IsClean(ex))
        {
            conn.Poison($"STOR {remotePath}: {ex.GetType().Name}");
            throw;
        }
        finally
        {
            await conn.DisposeAsync();
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
        // A filesystem directory deletion is nonrecursive. FluentFTP's
        // DeleteDirectory deletes its contents too, including files created after
        // CanDelete checked the listing. Let the server atomically reject those.
        var reply = await conn.Client.Execute($"RMD {CpsvDataHelper.SanitizeFtpPath(remotePath)}", ct);
        if (!reply.Success) throw new FtpCommandException(reply);
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

    internal enum DiskFreeVerdict { Parsed, Unsupported, Transient }

    // Owned here, not by a caller: the dashboard (every 30s) and the filesystem (every 5 min)
    // both poll, and each used to decide "unsupported" its own way — the dashboard never, the
    // filesystem on any null including a borrow timeout. One instance per mount, so a remount
    // re-probes once.
    private volatile bool _diskFreeUnsupported;
    public bool DiskFreeUnsupported => _diskFreeUnsupported;

    /// <summary>
    /// Try SITE DISKFREE to get server disk usage. Returns (totalBytes, freeBytes), or null if
    /// unsupported or the attempt failed. glftpd responds: "200 Total: X MB Free: Y MB"
    /// </summary>
    public Task<(long totalBytes, long freeBytes)?> GetDiskFree(CancellationToken ct = default) =>
        GetDiskFree(async token =>
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            await using var conn = await _pool.Borrow(cts.Token);
            var reply = await conn.Client.Execute("SITE DISKFREE", cts.Token);
            return (reply.Code ?? "", reply.Message ?? "");
        }, ct);

    internal async Task<(long totalBytes, long freeBytes)?> GetDiskFree(
        Func<CancellationToken, Task<(string code, string message)>> execute, CancellationToken ct)
    {
        if (_diskFreeUnsupported) return null;
        try
        {
            var (code, message) = await execute(ct);
            switch (ClassifyDiskFreeReply(code, message, out var total, out var free))
            {
                case DiskFreeVerdict.Parsed:
                    return (total, free);
                case DiskFreeVerdict.Unsupported:
                    _diskFreeUnsupported = true;
                    Log.Debug("SITE DISKFREE unsupported ({Code} {Message}) — not asking again on this mount",
                        code, message);
                    return null;
                default:
                    return null;
            }
        }
        catch
        {
            // Borrow timeout, cancellation, socket fault: nothing was learned about the server.
            return null;
        }
    }

    /// <summary>
    /// RFC 959: a 5yz reply is a permanent negative completion, and a 2xx body we cannot read
    /// will read the same way next time. Only those are findings; a 4yz or a missing reply is not.
    /// </summary>
    internal static DiskFreeVerdict ClassifyDiskFreeReply(string code, string message,
        out long totalBytes, out long freeBytes)
    {
        totalBytes = freeBytes = 0;
        if (code.StartsWith('5')) return DiskFreeVerdict.Unsupported;
        if (!code.StartsWith('2')) return DiskFreeVerdict.Transient;

        var totalMatch = DiskTotalRegex.Match(message);
        var freeMatch = DiskFreeRegex.Match(message);
        if (!totalMatch.Success || !freeMatch.Success) return DiskFreeVerdict.Unsupported;

        totalBytes = ParseDiskSize(long.Parse(totalMatch.Groups[1].Value), totalMatch.Groups[2].Value);
        freeBytes = ParseDiskSize(long.Parse(freeMatch.Groups[1].Value), freeMatch.Groups[2].Value);
        return DiskFreeVerdict.Parsed;
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
