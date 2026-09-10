using System.IO;
using FluentFTP;

namespace GlDrive.Filesystem;

public class FileNode : IDisposable
{
    public string RemotePath { get; internal set; }
    internal object SyncRoot { get; } = new();
    internal int OpenCount { get; set; }
    public bool IsDirectory { get; set; }
    public long FileSize { get; set; }
    public DateTime CreationTime { get; set; }
    public DateTime LastWriteTime { get; set; }
    public DateTime LastAccessTime { get; set; }

    // Reads are staged on disk, keeping large media files out of the managed heap.
    public Stream? ReadBuffer { get; set; }
    public bool ReadBufferLoaded { get; set; }

    // Write buffer — accumulate writes, upload on Cleanup
    // For large files, spills to a temp file on disk
    public MemoryStream? WriteBuffer { get; set; }
    public FileStream? WriteBufferFile { get; set; }
    public string? WriteBufferTempPath { get; set; }
    public bool IsDirty { get; set; }

    // Directory enumeration state
    public FtpListItem[]? DirEntries { get; set; }

    // Spill threshold (bytes) — writes above this go to temp file
    public long SpillThresholdBytes { get; set; } = 50L * 1024 * 1024;

    public FileNode(string remotePath, bool isDirectory = false)
    {
        RemotePath = remotePath;
        IsDirectory = isDirectory;
        var now = DateTime.UtcNow;
        CreationTime = now;
        LastWriteTime = now;
        LastAccessTime = now;
    }

    public static FileNode FromListItem(string parentPath, FtpListItem item)
    {
        var remotePath = parentPath.TrimEnd('/') + "/" + item.Name;
        return new FileNode(remotePath, item.Type == FtpObjectType.Directory)
        {
            FileSize = item.Size,
            LastWriteTime = item.Modified != DateTime.MinValue ? item.Modified : DateTime.UtcNow,
            CreationTime = item.Created != DateTime.MinValue ? item.Created : item.Modified,
            LastAccessTime = item.Modified != DateTime.MinValue ? item.Modified : DateTime.UtcNow
        };
    }

    /// <summary>
    /// Returns the active write stream, spilling to disk if memory exceeds threshold.
    /// </summary>
    public Stream GetOrCreateWriteStream(long requiredLength = 0)
    {
        if (WriteBufferFile != null)
            return WriteBufferFile;

        if (WriteBuffer == null)
            WriteBuffer = new MemoryStream();

        // Check if we need to spill to temp file
        if (SpillThresholdBytes > 0 && Math.Max(WriteBuffer.Length, requiredLength) >= SpillThresholdBytes)
        {
            var tempPath = Path.Combine(Path.GetTempPath(), $"gldrive-{Guid.NewGuid():N}.tmp");
            var fs = new FileStream(tempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);

            // Copy existing memory buffer to file
            var position = WriteBuffer.Position;
            try
            {
                WriteBuffer.Position = 0;
                WriteBuffer.CopyTo(fs);
                fs.Position = position;
            }
            catch
            {
                fs.Dispose();
                WriteBuffer.Position = position;
                throw;
            }
            WriteBuffer.Dispose();
            WriteBuffer = null;

            WriteBufferFile = fs;
            WriteBufferTempPath = tempPath;
            return fs;
        }

        return WriteBuffer;
    }

    /// <summary>
    /// Seed the first write with the existing file, even if the caller never read
    /// it. An empty write buffer would otherwise replace untouched bytes with zeros
    /// or truncate them when uploaded. A created/overwritten file already has a
    /// write buffer, so it never fetches old contents.
    /// </summary>
    internal Stream PrepareWriteStream(Func<byte[]> loadExisting, long requiredLength = 0)
    {
        if (WriteBuffer != null || WriteBufferFile != null)
            return GetOrCreateWriteStream(requiredLength);

        using var downloaded = !ReadBufferLoaded && FileSize != 0
            ? new MemoryStream(loadExisting(), writable: false) : null;
        var existing = ReadBufferLoaded ? ReadBuffer : downloaded;
        var position = existing?.Position ?? 0;
        try
        {
            var stream = GetOrCreateWriteStream(Math.Max(requiredLength, existing?.Length ?? 0));
            if (existing != null)
            {
                existing.Position = 0;
                existing.CopyTo(stream);
            }
            return stream;
        }
        catch
        {
            WriteBuffer?.Dispose();
            WriteBuffer = null;
            WriteBufferFile?.Dispose();
            WriteBufferFile = null;
            WriteBufferTempPath = null;
            throw;
        }
        finally
        {
            if (existing != null) existing.Position = position;
        }
    }

    internal void LoadReadBuffer(Action<Stream> download)
    {
        if (ReadBufferLoaded) return;
        var path = Path.Combine(Path.GetTempPath(), $"gldrive-read-{Guid.NewGuid():N}.tmp");
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.None, 81920, FileOptions.DeleteOnClose);
        try
        {
            download(stream);
            stream.Position = 0;
            ReadBuffer = stream;
            FileSize = stream.Length;
            ReadBufferLoaded = true;
        }
        catch { stream.Dispose(); throw; }
    }

    internal Stream PrepareWriteStream(Action<Stream> download, long requiredLength = 0)
    {
        if (WriteBuffer == null && WriteBufferFile == null && !ReadBufferLoaded && FileSize != 0)
            LoadReadBuffer(download);
        return PrepareWriteStream(() => throw new InvalidOperationException("Read buffer was not loaded"), requiredLength);
    }

    internal string? PreserveFailedWrite(string recoveryDirectory, string volume)
    {
        if (!IsDirty) return null;
        var source = GetWriteStreamForUpload();
        if (source == null) return null;
        Directory.CreateDirectory(recoveryDirectory);
        var path = Path.Combine(recoveryDirectory, Guid.NewGuid().ToString("N") + ".data");
        // Establish a restricted ACL before copying file content.
        GlDrive.Util.SecureFile.WriteAllTextRestricted(path, "");
        using (var target = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            source.CopyTo(target);
            target.Flush(flushToDisk: true);
        }
        GlDrive.Util.SecureFile.WriteAllTextRestricted(path + ".json",
            System.Text.Json.JsonSerializer.Serialize(new { Volume = volume, RemotePath, Length = source.Length, SavedUtc = DateTime.UtcNow }));
        return path;
    }

    /// <summary>
    /// Gets the readable write stream for upload (seeks to 0).
    /// </summary>
    public Stream? GetWriteStreamForUpload()
    {
        if (WriteBufferFile != null)
        {
            WriteBufferFile.Position = 0;
            return WriteBufferFile;
        }
        if (WriteBuffer != null)
        {
            WriteBuffer.Position = 0;
            return WriteBuffer;
        }
        return null;
    }

    /// <summary>
    /// Gets length of the active write buffer (memory or file).
    /// </summary>
    public long GetWriteBufferLength()
    {
        if (WriteBufferFile != null) return WriteBufferFile.Length;
        if (WriteBuffer != null) return WriteBuffer.Length;
        return 0;
    }

    public uint GetFileAttributes()
    {
        const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
        const uint FILE_ATTRIBUTE_NORMAL = 0x80;
        const uint FILE_ATTRIBUTE_ARCHIVE = 0x20;

        if (IsDirectory)
            return FILE_ATTRIBUTE_DIRECTORY;

        return FILE_ATTRIBUTE_ARCHIVE | FILE_ATTRIBUTE_NORMAL;
    }

    public static ulong ToFileTime(DateTime dt)
    {
        if (dt == DateTime.MinValue) dt = DateTime.UtcNow;
        if (dt.Kind != DateTimeKind.Utc) dt = dt.ToUniversalTime();
        return (ulong)dt.ToFileTimeUtc();
    }

    public void Dispose()
    {
        ReadBuffer?.Dispose();
        ReadBuffer = null;
        ReadBufferLoaded = false;
        WriteBuffer?.Dispose();
        WriteBuffer = null;
        WriteBufferFile?.Dispose();
        WriteBufferFile = null;
        WriteBufferTempPath = null;
    }
}
