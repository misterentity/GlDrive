using System.IO;
using System.Text;
using System.Text.Json;
using FluentFTP;
using GlDrive.Ftp;

namespace GlDrive.Services.Automation;

public sealed partial class AutomationService
{
    internal const int MaxInlineFileBytes = 1024 * 1024;

    private async Task<object?> FtpAsync(string op, JsonElement a, CancellationToken ct)
    {
        var service = Mounted(Required(a, "serverId", 128));
        var ftp = service.Ftp ?? throw new InvalidOperationException("FTP service unavailable.");
        if (op == "ftp.command")
        {
            var command = Line(Required(a, "command", 4096), "command");
            var verb = command.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].ToUpperInvariant();
            if (verb is "RETR" or "STOR" or "STOU" or "APPE" or "LIST" or "NLST" or "MLSD" or "PASV" or "EPSV" or "CPSV" or "PORT" or "EPRT")
                throw new ArgumentException("Use the ftp file/list tools for data commands; raw commands cannot open a data channel.");
            await using var conn = await (service.Pool ?? throw new InvalidOperationException("FTP pool unavailable.")).BorrowFor(FtpBorrowPurpose.Automation, ct);
            // Arbitrary control commands may change CWD, authentication, TLS mode or transfer state.
            // Never return this connection to the pool, including on cancellation/command failure.
            conn.Poison("automation raw FTP command");
            var reply = await conn.Client.Execute(command, ct);
            service.RefreshCache();
            return new { reply.Code, reply.Success, reply.Message, reply.InfoMessages };
        }
        var path = RemotePath(a, allowRoot: op is "ftp.list" or "ftp.stat" or "ftp.mkdir");
        switch (op)
        {
            case "ftp.list":
                var listing = await ftp.ListDirectory(path, ct);
                return Page(listing.Select(item => new
                {
                    item.Name, path = item.FullName, type = item.Type.ToString(), item.Size,
                    item.Modified, item.Created, item.Chmod, item.LinkTarget
                }).ToArray(), a);
            case "ftp.stat":
                if (await ftp.DirectoryExists(path, ct)) return new { path, exists = true, type = "Directory" };
                if (!await ftp.FileExists(path, ct)) return new { path, exists = false };
                return new { path, exists = true, type = "File", size = await ftp.GetFileSize(path, ct), modified = await ftp.GetModifiedTime(path, ct) };
            case "ftp.read":
            {
                var max = Integer(a, "maxBytes", MaxInlineFileBytes, 1, MaxInlineFileBytes);
                var encoding = Optional(a, "encoding", 16) ?? "utf8";
                if (encoding is not ("utf8" or "base64")) throw new ArgumentException("'encoding' must be utf8 or base64.");
                using var output = new LimitedMemoryStream(max);
                await ftp.DownloadToStream(path, output, ct);
                var bytes = output.ToArray();
                return new { path, size = bytes.Length, encoding, content = encoding == "base64" ? Convert.ToBase64String(bytes) : Encoding.UTF8.GetString(bytes) };
            }
            case "ftp.write":
            {
                var content = Optional(a, "content", MaxInlineFileBytes * 2) ?? throw new ArgumentException("'content' is required (empty is allowed).");
                var encoding = Optional(a, "encoding", 16) ?? "utf8";
                var bytes = encoding switch
                {
                    "utf8" => Encoding.UTF8.GetBytes(content),
                    "base64" => Convert.FromBase64String(content),
                    _ => throw new ArgumentException("'encoding' must be utf8 or base64.")
                };
                if (bytes.Length > MaxInlineFileBytes) throw new ArgumentException("Inline files are limited to 1 MiB; use ftp.upload.");
                await CheckDestination(ftp, path, Flag(a, "overwrite"), ct);
                await ftp.UploadFile(path, bytes, ct);
                service.RefreshCache();
                return new { path, writtenBytes = bytes.Length };
            }
            case "ftp.upload":
            {
                var local = LocalFile(a);
                await CheckDestination(ftp, path, Flag(a, "overwrite"), ct);
                await using var source = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
                await ftp.UploadFile(path, source, ct);
                service.RefreshCache();
                return new { path, localPath = local, writtenBytes = source.Length };
            }
            case "ftp.download":
            {
                var local = LocalFile(a);
                var overwrite = Flag(a, "overwrite");
                if (!overwrite && File.Exists(local)) throw new IOException("Local destination exists. Set overwrite=true to replace it.");
                var parent = Path.GetDirectoryName(local)!;
                Directory.CreateDirectory(parent);
                // A failed/cancelled download must not destroy a preexisting destination.
                var temporary = Path.Combine(parent, $".gldrive-{Guid.NewGuid():N}.partial");
                try
                {
                    long bytes;
                    await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
                    {
                        await ftp.DownloadToStream(path, output, ct);
                        bytes = output.Length;
                    }
                    ct.ThrowIfCancellationRequested();
                    File.Move(temporary, local, overwrite);
                    return new { path, localPath = local, writtenBytes = bytes };
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            case "ftp.mkdir":
                await ftp.CreateDirectory(path, ct);
                break;
            case "ftp.rename":
                var destination = RemotePath(a, "destinationPath", false);
                await CheckDestination(ftp, destination, false, ct);
                await ftp.Rename(path, destination, ct);
                break;
            case "ftp.delete":
                if (await ftp.DirectoryExists(path, ct))
                    await DeleteRemoteDirectory(service, path, Flag(a, "recursive"), 0, ct);
                else if (await ftp.FileExists(path, ct)) await ftp.DeleteFile(path, ct);
                else throw new FileNotFoundException("Remote path does not exist.", path);
                break;
            default: throw new ArgumentException($"Unknown operation: {op}");
        }
        service.RefreshCache();
        return new { path, completed = true };
    }

    private static string LocalFile(JsonElement a)
    {
        var path = Required(a, "localPath", 32767);
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("'localPath' must be an absolute filesystem path.");
        return Path.GetFullPath(path);
    }

    private static async Task CheckDestination(FtpOperations ftp, string path, bool overwrite, CancellationToken ct)
    {
        if (await ftp.DirectoryExists(path, ct)) throw new IOException("The destination is a directory.");
        if (!overwrite && await ftp.FileExists(path, ct))
            throw new IOException("Remote destination exists. Set overwrite=true to replace it.");
    }

    private static async Task DeleteRemoteDirectory(MountService service, string path, bool recursive, int depth, CancellationToken ct)
    {
        if (depth > 64) throw new IOException("Directory nesting exceeds 64 levels.");
        var ftp = service.Ftp!;
        // Use FtpOperations for listing so CPSV-only sites use the correct data transport.
        var items = (await ftp.ListDirectory(path, ct)).Where(i => i.Name is not ("." or "..")).ToArray();
        if (items.Length > 0 && !recursive)
            throw new IOException("Directory is not empty. Set recursive=true to delete its contents.");
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            var child = path.TrimEnd('/') + "/" + item.Name;
            if (item.Name.Contains('/') || item.Name.Contains('\\') || item.Name.IndexOfAny(['\r', '\n', '\0']) >= 0)
                throw new IOException("The FTP server returned an unsafe child name.");
            if (item.Type == FtpObjectType.Directory) await DeleteRemoteDirectory(service, child, true, depth + 1, ct);
            else await ftp.DeleteFile(child, ct); // Delete symlinks themselves; do not follow them.
        }
        await using var conn = await service.Pool!.BorrowFor(FtpBorrowPurpose.Automation, ct);
        try
        {
            var reply = await conn.Client.Execute($"RMD {path}", ct);
            if (!reply.Success) throw new IOException($"FTP RMD rejected: {reply.Code} {reply.Message}");
        }
        catch (OperationCanceledException) { conn.Poison("automation RMD cancelled"); throw; }
    }

    /// <summary>Enforces the limit during copying, even if SIZE was missing or the remote file grows.</summary>
    internal sealed class LimitedMemoryStream(int limit) : MemoryStream
    {
        private void Check(int count)
        {
            if (Position + count > limit) throw new IOException($"File exceeds the {limit}-byte inline limit; use ftp.download.");
        }
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { Check(1); base.WriteByte(value); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            Check(count);
            return base.WriteAsync(buffer, offset, count, ct);
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            Check(buffer.Length);
            return base.WriteAsync(buffer, ct);
        }
    }
}
