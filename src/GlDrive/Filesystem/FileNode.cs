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
    public MemoryStream? WriteBuffer { get; set; }
    public bool IsDirty { get; set; }

    // Directory enumeration state
    public FtpListItem[]? DirEntries { get; set; }

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
        WriteBuffer?.Dispose();
        WriteBuffer = null;
    }
}
