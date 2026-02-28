using System.IO;
using FluentFTP;
using Serilog;

namespace GlDrive.Ftp;

public class FtpOperations
{
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
        return new MemoryStream(data);
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
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        await UploadFile(remotePath, ms.ToArray(), ct);
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
}
