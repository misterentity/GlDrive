using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Fsp;
using Fsp.Interop;
using FluentFTP;
using GlDrive.Ftp;
using Serilog;
using FileAttributes = System.IO.FileAttributes;
using FileInfo = Fsp.Interop.FileInfo;

namespace GlDrive.Filesystem;

public class GlDriveFileSystem : FileSystemBase
{
    private readonly FtpOperations _ftp;
    private readonly DirectoryCache _cache;
    private readonly string _rootPath;
    private readonly string _volumeLabel;
    private readonly int _fileInfoTimeoutMs;
    private readonly int _dirListTimeoutSeconds;
    private readonly long _spillThresholdBytes;
    private readonly SemaphoreSlim _ftpGate;
    private readonly byte[] _defaultSecurityDescriptor;
    private long _cachedTotalSize = 1L * 1024 * 1024 * 1024 * 1024; // 1 TB default
    private long _cachedFreeSize = 500L * 1024 * 1024 * 1024; // 500 GB default
    private DateTime _lastDiskQuery = DateTime.UtcNow; // delay first query until 5min after mount
    private int _diskQueryRunning; // 0 = idle, 1 = running
    private bool _diskQueryUnsupported;

    public GlDriveFileSystem(FtpOperations ftp, DirectoryCache cache, string rootPath, string volumeLabel,
        int fileInfoTimeoutMs = 1000, int dirListTimeoutSeconds = 30, int readBufferSpillThresholdMb = 50)
    {
        _ftp = ftp;
        _cache = cache;
        _rootPath = rootPath.TrimEnd('/');
        _volumeLabel = volumeLabel;
        _fileInfoTimeoutMs = fileInfoTimeoutMs;
        _dirListTimeoutSeconds = dirListTimeoutSeconds;
        _spillThresholdBytes = readBufferSpillThresholdMb > 0 ? readBufferSpillThresholdMb * 1024L * 1024 : 0;
        // Bound concurrent FTP operations to avoid saturating the global ThreadPool
        _ftpGate = new SemaphoreSlim(Environment.ProcessorCount, Environment.ProcessorCount);

        // Build a security descriptor granting the current user full access
        var sid = WindowsIdentity.GetCurrent().User!;
        var dacl = new RawAcl(2, 1);
        dacl.InsertAce(0, new CommonAce(
            AceFlags.ContainerInherit | AceFlags.ObjectInherit,
            AceQualifier.AccessAllowed,
            0x1F01FF, // FILE_ALL_ACCESS
            sid, false, null));
        var sd = new RawSecurityDescriptor(
            ControlFlags.DiscretionaryAclPresent | ControlFlags.SelfRelative,
            sid, sid, null, dacl);
        _defaultSecurityDescriptor = new byte[sd.BinaryLength];
        sd.GetBinaryForm(_defaultSecurityDescriptor, 0);
    }

    // Convert Windows path (backslashes) to FTP path (forward slashes)
    private string ToRemotePath(string fileName)
    {
        var ftpPath = fileName.Replace('\\', '/');
        if (string.IsNullOrEmpty(ftpPath) || ftpPath == "/")
            return string.IsNullOrEmpty(_rootPath) ? "/" : _rootPath;
        return _rootPath + ftpPath;
    }

    public override int Init(object host0)
    {
        var host = (FileSystemHost)host0;
        host.SectorSize = 4096;
        host.SectorsPerAllocationUnit = 1;
        host.MaxComponentLength = 255;
        host.FileInfoTimeout = (uint)_fileInfoTimeoutMs;
        host.CaseSensitiveSearch = false;
        host.CasePreservedNames = true;
        host.UnicodeOnDisk = true;
        host.PersistentAcls = true;
        host.PostCleanupWhenModifiedOnly = true;
        host.FlushAndPurgeOnCleanup = true;
        host.ReparsePoints = false;
        host.VolumeCreationTime = (ulong)DateTime.Now.ToFileTimeUtc();
        host.VolumeSerialNumber = (uint)(host.VolumeCreationTime / (10000 * 1000));
        host.FileSystemName = "GlDrive";
        return STATUS_SUCCESS;
    }

    public override int GetVolumeInfo(out VolumeInfo volumeInfo)
    {
        volumeInfo = default;
        volumeInfo.TotalSize = (ulong)Interlocked.Read(ref _cachedTotalSize);
        volumeInfo.FreeSize = (ulong)Interlocked.Read(ref _cachedFreeSize);
        volumeInfo.SetVolumeLabel(_volumeLabel);

        // Refresh disk info in background every 5 minutes (skip if unsupported or already running)
        if (!_diskQueryUnsupported &&
            (DateTime.UtcNow - _lastDiskQuery).TotalMinutes >= 5 &&
            Interlocked.CompareExchange(ref _diskQueryRunning, 1, 0) == 0)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await _ftp.GetDiskFree();
                    if (result is { } disk)
                    {
                        Interlocked.Exchange(ref _cachedTotalSize, disk.totalBytes);
                        Interlocked.Exchange(ref _cachedFreeSize, disk.freeBytes);
                        Log.Debug("SITE DISKFREE: total={Total} free={Free}", disk.totalBytes, disk.freeBytes);
                    }
                    else
                    {
                        _diskQueryUnsupported = true;
                        Log.Debug("SITE DISKFREE not supported — using defaults");
                    }
                }
                catch
                {
                    _diskQueryUnsupported = true;
                }
                finally
                {
                    _lastDiskQuery = DateTime.UtcNow;
                    Interlocked.Exchange(ref _diskQueryRunning, 0);
                }
            });
        }

        return STATUS_SUCCESS;
    }

    public override int GetSecurityByName(
        string fileName,
        out uint fileAttributes,
        ref byte[] securityDescriptor)
    {
        fileAttributes = default;

        try
        {
            var remotePath = ToRemotePath(fileName);
            Log.Debug("GetSecurityByName: {FileName} -> {RemotePath}", fileName, remotePath);

            if (securityDescriptor != null)
                securityDescriptor = _defaultSecurityDescriptor;

            // Root always exists
            if (remotePath == _rootPath || remotePath == "/")
            {
                fileAttributes = (uint)FileAttributes.Directory;
                return STATUS_SUCCESS;
            }

            // Check cache first
            var cached = _cache.FindItem(remotePath);
            if (cached != null)
            {
                fileAttributes = cached.Type == FtpObjectType.Directory
                    ? (uint)FileAttributes.Directory
                    : (uint)(FileAttributes.Archive | FileAttributes.Normal);
                return STATUS_SUCCESS;
            }

            // Cache miss — list parent to populate cache
            var parentRemote = GetParentRemotePath(remotePath);
            var items = ListDirectoryCached(parentRemote);
            var itemName = GetItemName(remotePath);
            var match = items.FirstOrDefault(i =>
                string.Equals(i.Name, itemName, StringComparison.Ordinal));

            if (match == null)
                return STATUS_OBJECT_NAME_NOT_FOUND;

            fileAttributes = match.Type == FtpObjectType.Directory
                ? (uint)FileAttributes.Directory
                : (uint)(FileAttributes.Archive | FileAttributes.Normal);
            return STATUS_SUCCESS;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "GetSecurityByName failed: {FileName}", fileName);
            return NtStatusMapper.MapException(ex);
        }
    }

    public override int Open(
        string fileName,
        uint createOptions,
        uint grantedAccess,
        out object fileNode,
        out object fileDesc,
        out FileInfo fileInfo,
        out string normalizedName)
    {
        fileNode = null!;
        fileDesc = null!;
        fileInfo = default;
        normalizedName = null!;

        try
        {
            var remotePath = ToRemotePath(fileName);
            Log.Debug("Open: {FileName} -> {RemotePath}", fileName, remotePath);

            FileNode node;
            if (remotePath == _rootPath || remotePath == "/")
            {
                node = new FileNode(remotePath, true) { SpillThresholdBytes = _spillThresholdBytes };
            }
            else
            {
                var parentRemote = GetParentRemotePath(remotePath);
                var items = ListDirectoryCached(parentRemote);
                var itemName = GetItemName(remotePath);
                var match = items.FirstOrDefault(i =>
                    string.Equals(i.Name, itemName, StringComparison.Ordinal));

                if (match == null)
                    return STATUS_OBJECT_NAME_NOT_FOUND;

                node = FileNode.FromListItem(parentRemote, match);
                node.SpillThresholdBytes = _spillThresholdBytes;
            }

            fileNode = node;
            fileDesc = node;
            FillFileInfo(node, out fileInfo);
            normalizedName = fileName;
            return STATUS_SUCCESS;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Open failed: {FileName}", fileName);
            return NtStatusMapper.MapException(ex);
        }
    }

    public override int Create(
        string fileName,
        uint createOptions,
        uint grantedAccess,
        uint fileAttributes,
        byte[] securityDescriptor,
        ulong allocationSize,
        out object fileNode,
        out object fileDesc,
        out FileInfo fileInfo,
        out string normalizedName)
    {
        fileNode = null!;
        fileDesc = null!;
        fileInfo = default;
        normalizedName = null!;

        try
        {
            var remotePath = ToRemotePath(fileName);
            bool isDir = (createOptions & 0x00000001) != 0; // FILE_DIRECTORY_FILE
            Log.Debug("Create: {FileName} -> {RemotePath} (dir={IsDir})", fileName, remotePath, isDir);

            if (isDir)
            {
                _ftp.CreateDirectory(remotePath).GetAwaiter().GetResult();
            }
            else
            {
                // Create empty file
                _ftp.UploadFile(remotePath, []).GetAwaiter().GetResult();
            }

            _cache.InvalidateParent(remotePath);

            var node = new FileNode(remotePath, isDir) { SpillThresholdBytes = _spillThresholdBytes };
            if (!isDir)
            {
                node.WriteBuffer = new MemoryStream();
                node.IsDirty = false;
            }

            fileNode = node;
            fileDesc = node;
            FillFileInfo(node, out fileInfo);
            normalizedName = fileName;
            return STATUS_SUCCESS;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Create failed: {FileName}", fileName);
            return NtStatusMapper.MapException(ex);
        }
    }

    public override int Overwrite(
        object fileNode0,
        object fileDesc0,
        uint fileAttributes,
        bool replaceFileAttributes,
        ulong allocationSize,
        out FileInfo fileInfo)
    {
        fileInfo = default;
        var node = (FileNode)fileNode0;

        try
        {
            Log.Debug("Overwrite: {Path}", node.RemotePath);
            node.ReadBuffer?.Dispose();
            node.ReadBuffer = null;
            node.ReadBufferLoaded = false;
            node.WriteBufferFile?.Dispose();
            node.WriteBufferFile = null;
            node.WriteBuffer = new MemoryStream();
            node.IsDirty = true;
            node.FileSize = 0;
            FillFileInfo(node, out fileInfo);
            return STATUS_SUCCESS;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Overwrite failed: {Path}", node.RemotePath);
            return NtStatusMapper.MapException(ex);
        }
    }

    public override int Read(
        object fileNode0,
        object fileDesc0,
        IntPtr buffer,
        ulong offset,
        uint length,
        out uint bytesTransferred)
    {
        bytesTransferred = 0;
        var node = (FileNode)fileNode0;

        try
        {
            // Load entire file on first read
            if (!node.ReadBufferLoaded)
            {
                Log.Debug("Read: downloading {Path}", node.RemotePath);
                var data = _ftp.DownloadFile(node.RemotePath).GetAwaiter().GetResult();
                node.ReadBuffer = new MemoryStream(data);
                node.ReadBufferLoaded = true;
                node.FileSize = data.Length;
            }

            var stream = (Stream?)node.WriteBufferFile ?? node.WriteBuffer ?? node.ReadBuffer;
            if (stream == null)
                return STATUS_END_OF_FILE;

            if ((long)offset >= stream.Length)
                return STATUS_END_OF_FILE;

            stream.Position = (long)offset;
            var toRead = (int)Math.Min(length, stream.Length - (long)offset);
            var buf = ArrayPool<byte>.Shared.Rent(toRead);
            try
            {
                var read = stream.Read(buf, 0, toRead);
                Marshal.Copy(buf, 0, buffer, read);
                bytesTransferred = (uint)read;
                return STATUS_SUCCESS;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Read failed: {Path}", node.RemotePath);
            return NtStatusMapper.MapException(ex);
        }
    }

    public override int Write(
        object fileNode0,
        object fileDesc0,
        IntPtr buffer,
        ulong offset,
        uint length,
        bool writeToEndOfFile,
        bool constrainedIo,
        out uint bytesTransferred,
        out FileInfo fileInfo)
    {
        bytesTransferred = 0;
        fileInfo = default;
        var node = (FileNode)fileNode0;

        try
        {
            var writeStream = node.GetOrCreateWriteStream();

            // If we had a read buffer but no writes yet, copy read data to write stream
            if (node.ReadBufferLoaded && writeStream.Length == 0 && node.ReadBuffer != null && node.ReadBuffer.Length > 0)
            {
                node.ReadBuffer.Position = 0;
                node.ReadBuffer.CopyTo(writeStream);
            }

            var pos = writeToEndOfFile ? writeStream.Length : (long)offset;

            if (constrainedIo && pos + length > writeStream.Length)
            {
                length = (uint)Math.Max(0, writeStream.Length - pos);
                if (length == 0)
                    return STATUS_SUCCESS;
            }

            // Expand if needed
            if (pos + length > writeStream.Length)
                writeStream.SetLength(pos + length);

            writeStream.Position = pos;
            var buf = ArrayPool<byte>.Shared.Rent((int)length);
            try
            {
                Marshal.Copy(buffer, buf, 0, (int)length);
                writeStream.Write(buf, 0, (int)length);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }

            bytesTransferred = length;
            node.IsDirty = true;
            node.FileSize = writeStream.Length;
            node.LastWriteTime = DateTime.UtcNow;
            FillFileInfo(node, out fileInfo);
            return STATUS_SUCCESS;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Write failed: {Path}", node.RemotePath);
            return NtStatusMapper.MapException(ex);
        }
    }

    public override int Flush(
        object fileNode0,
        object fileDesc0,
        out FileInfo fileInfo)
    {
        fileInfo = default;
        if (fileNode0 is not FileNode node)
            return STATUS_SUCCESS;

        FillFileInfo(node, out fileInfo);
        return STATUS_SUCCESS;
    }

    public override int GetFileInfo(
        object fileNode0,
        object fileDesc0,
        out FileInfo fileInfo)
    {
        fileInfo = default;
        var node = (FileNode)fileNode0;
        FillFileInfo(node, out fileInfo);
        return STATUS_SUCCESS;
    }

    public override int SetBasicInfo(
        object fileNode0,
        object fileDesc0,
        uint fileAttributes,
        ulong creationTime,
        ulong lastAccessTime,
        ulong lastWriteTime,
        ulong changeTime,
        out FileInfo fileInfo)
    {
        fileInfo = default;
        var node = (FileNode)fileNode0;
        // FTP doesn't support setting timestamps, just acknowledge
        FillFileInfo(node, out fileInfo);
        return STATUS_SUCCESS;
    }

    public override int SetFileSize(
        object fileNode0,
        object fileDesc0,
        ulong newSize,
        bool setAllocationSize,
        out FileInfo fileInfo)
    {
        fileInfo = default;
        var node = (FileNode)fileNode0;

        if (!setAllocationSize)
        {
            var writeStream = node.GetOrCreateWriteStream();
            writeStream.SetLength((long)newSize);
            node.FileSize = (long)newSize;
            node.IsDirty = true;
        }

        FillFileInfo(node, out fileInfo);
        return STATUS_SUCCESS;
    }

    public override void Cleanup(
        object fileNode0,
        object fileDesc0,
        string fileName,
        uint flags)
    {
        var node = (FileNode)fileNode0;

        try
        {
            // Flags: bit 0 = SetDelete
            bool setDelete = (flags & 1) != 0;

            if (setDelete)
            {
                var remotePath = ToRemotePath(fileName ?? "");
                if (string.IsNullOrEmpty(remotePath)) remotePath = node.RemotePath;

                Log.Debug("Cleanup delete: {Path} (dir={IsDir})", remotePath, node.IsDirectory);

                if (node.IsDirectory)
                    _ftp.DeleteDirectory(remotePath).GetAwaiter().GetResult();
                else
                    _ftp.DeleteFile(remotePath).GetAwaiter().GetResult();

                _cache.InvalidateParent(remotePath);
                _cache.Invalidate(remotePath);
                return;
            }

            // Upload dirty write buffer on close
            if (node.IsDirty)
            {
                var uploadStream = node.GetWriteStreamForUpload();
                if (uploadStream != null)
                {
                    Log.Debug("Cleanup upload: {Path} ({Bytes} bytes)", node.RemotePath, uploadStream.Length);
                    _ftp.UploadFile(node.RemotePath, uploadStream).GetAwaiter().GetResult();
                    node.IsDirty = false;
                    _cache.InvalidateParent(node.RemotePath);
                }
            }

            // Free read buffer eagerly to reduce memory pressure
            node.ReadBuffer?.Dispose();
            node.ReadBuffer = null;
            node.ReadBufferLoaded = false;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Cleanup failed: {Path}", node.RemotePath);
        }
    }

    public override void Close(object fileNode0, object fileDesc0)
    {
        if (fileNode0 is FileNode node)
            node.Dispose();
    }

    public override bool ReadDirectoryEntry(
        object fileNode0,
        object fileDesc0,
        string pattern,
        string marker,
        ref object context,
        out string fileName,
        out FileInfo fileInfo)
    {
        fileName = null!;
        fileInfo = default;
        var node = (FileNode)fileNode0;

        try
        {
            var index = context as int? ?? 0;
            if (index == 0)
                Log.Debug("ReadDirectoryEntry: {Path} (pattern={Pattern}, marker={Marker})",
                    node.RemotePath, pattern, marker);

            // Load directory listing on first call
            if (node.DirEntries == null)
            {
                var items = ListDirectoryCached(node.RemotePath);
                node.DirEntries = items;
            }

            // Skip to marker position (WinFsp restart enumeration after marker)
            if (marker != null && index == 0)
            {
                // "." and ".." are at positions 0 and 1
                if (marker == ".")
                {
                    index = 1;
                }
                else if (marker == "..")
                {
                    index = 2;
                }
                else
                {
                    // Find the marker in entries
                    for (int i = 0; i < node.DirEntries.Length; i++)
                    {
                        if (string.Equals(node.DirEntries[i].Name, marker, StringComparison.Ordinal))
                        {
                            index = i + 3; // skip past ".", "..", and the marker entry
                            break;
                        }
                    }
                }
            }

            // Emit "." and ".."
            if (index == 0)
            {
                fileName = ".";
                FillFileInfo(node, out fileInfo);
                context = 1;
                return true;
            }
            if (index == 1)
            {
                fileName = "..";
                FillFileInfo(node, out fileInfo);
                context = 2;
                return true;
            }

            var entryIndex = index - 2;
            if (entryIndex >= node.DirEntries.Length)
                return false;

            var entry = node.DirEntries[entryIndex];
            fileName = entry.Name;
            FillFileInfoFromListItem(entry, out fileInfo);
            context = index + 1;
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ReadDirectoryEntry failed: {Path}", node.RemotePath);
            return false;
        }
    }

    public override int Rename(
        object fileNode0,
        object fileDesc0,
        string fileName,
        string newFileName,
        bool replaceIfExists)
    {
        try
        {
            var fromPath = ToRemotePath(fileName);
            var toPath = ToRemotePath(newFileName);
            Log.Debug("Rename: {From} -> {To}", fromPath, toPath);

            _ftp.Rename(fromPath, toPath).GetAwaiter().GetResult();
            _cache.InvalidateParent(fromPath);
            _cache.InvalidateParent(toPath);
            _cache.Invalidate(fromPath);
            return STATUS_SUCCESS;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Rename failed: {FileName} -> {NewFileName}", fileName, newFileName);
            return NtStatusMapper.MapException(ex);
        }
    }

    public override int GetSecurity(
        object fileNode0,
        object fileDesc0,
        ref byte[] securityDescriptor)
    {
        securityDescriptor = _defaultSecurityDescriptor;
        return STATUS_SUCCESS;
    }

public override int CanDelete(
        object fileNode0,
        object fileDesc0,
        string fileName)
    {
        return STATUS_SUCCESS; // Allow delete attempts; actual errors surface in Cleanup
    }

    // Helper: List directory with caching and timeout
    private FtpListItem[] ListDirectoryCached(string remotePath)
    {
        if (_cache.TryGet(remotePath, out var cached))
            return cached;

        Log.Debug("Listing {Path} (cache miss, fetching from server)...", remotePath);

        // Bound concurrent FTP calls to avoid ThreadPool starvation
        _ftpGate.Wait();
        try
        {
            // Re-check cache after acquiring gate (another thread may have populated it)
            if (_cache.TryGet(remotePath, out cached))
                return cached;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_dirListTimeoutSeconds));
            FtpListItem[] items;
            try
            {
                items = _ftp.ListDirectory(remotePath, cts.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                Log.Warning("ListDirectory timed out after {Seconds}s: {Path}", _dirListTimeoutSeconds, remotePath);
                throw new TimeoutException($"ListDirectory timed out: {remotePath}");
            }
            Log.Debug("Listed {Path}: {Count} entries", remotePath, items.Length);
            _cache.Set(remotePath, items);
            return items;
        }
        finally
        {
            _ftpGate.Release();
        }
    }

    private static void FillFileInfo(FileNode node, out FileInfo fileInfo)
    {
        fileInfo = default;
        fileInfo.FileAttributes = node.GetFileAttributes();
        fileInfo.FileSize = node.IsDirectory ? 0 : (ulong)node.FileSize;
        fileInfo.AllocationSize = (fileInfo.FileSize + 4095) / 4096 * 4096;
        fileInfo.CreationTime = FileNode.ToFileTime(node.CreationTime);
        fileInfo.LastAccessTime = FileNode.ToFileTime(node.LastAccessTime);
        fileInfo.LastWriteTime = FileNode.ToFileTime(node.LastWriteTime);
        fileInfo.ChangeTime = FileNode.ToFileTime(node.LastWriteTime);
    }

    private static void FillFileInfoFromListItem(FtpListItem item, out FileInfo fileInfo)
    {
        fileInfo = default;
        bool isDir = item.Type == FtpObjectType.Directory;
        fileInfo.FileAttributes = isDir
            ? (uint)FileAttributes.Directory
            : (uint)(FileAttributes.Archive | FileAttributes.Normal);
        fileInfo.FileSize = isDir ? 0 : (ulong)item.Size;
        fileInfo.AllocationSize = (fileInfo.FileSize + 4095) / 4096 * 4096;
        var modified = item.Modified != DateTime.MinValue ? item.Modified : DateTime.UtcNow;
        var ft = FileNode.ToFileTime(modified);
        fileInfo.CreationTime = ft;
        fileInfo.LastAccessTime = ft;
        fileInfo.LastWriteTime = ft;
        fileInfo.ChangeTime = ft;
    }

    private static string GetParentRemotePath(string path)
    {
        var idx = path.LastIndexOf('/');
        return idx <= 0 ? "/" : path[..idx];
    }

    private static string GetItemName(string path)
    {
        var idx = path.LastIndexOf('/');
        return idx < 0 ? path : path[(idx + 1)..];
    }
}
