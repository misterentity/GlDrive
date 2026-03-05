using System.IO;
using FluentFTP;

namespace GlDrive.Filesystem;

public class FileNode
{
    public string RemotePath { get; }
    public bool IsDirectory { get; set; }
    public long FileSize { get; set; }
    public DateTime CreationTime { get; set; }
    public DateTime LastWriteTime { get; set; }
    public DateTime LastAccessTime { get; set; }

    // Read buffer — whole-file download on first Read
    public MemoryStream? ReadBuffer { get; set; }
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
    public Stream GetOrCreateWriteStream()
    {
        if (WriteBufferFile != null)
            return WriteBufferFile;

        if (WriteBuffer == null)
            WriteBuffer = new MemoryStream();

        // Check if we need to spill to temp file
        if (SpillThresholdBytes > 0 && WriteBuffer.Length >= SpillThresholdBytes)
        {
            var tempPath = Path.Combine(Path.GetTempPath(), $"gldrive-{Guid.NewGuid():N}.tmp");
            var fs = new FileStream(tempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);

            // Copy existing memory buffer to file
            WriteBuffer.Position = 0;
            WriteBuffer.CopyTo(fs);
            WriteBuffer.Dispose();
            WriteBuffer = null;

            WriteBufferFile = fs;
            WriteBufferTempPath = tempPath;
            return fs;
        }

        return WriteBuffer;
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
