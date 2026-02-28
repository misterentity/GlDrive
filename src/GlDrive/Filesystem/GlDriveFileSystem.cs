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
    private readonly byte[] _defaultSecurityDescriptor;

    public GlDriveFileSystem(FtpOperations ftp, DirectoryCache cache, string rootPath, string volumeLabel)
    {
        _ftp = ftp;
        _cache = cache;
        _rootPath = rootPath.TrimEnd('/');
        _volumeLabel = volumeLabel;

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
        host.FileInfoTimeout = 1000;
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
        volumeInfo.TotalSize = 1L * 1024 * 1024 * 1024 * 1024; // 1 TB virtual
        volumeInfo.FreeSize = 500L * 1024 * 1024 * 1024; // 500 GB free
        volumeInfo.SetVolumeLabel(_volumeLabel);
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
                node = new FileNode(remotePath, true);
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
                Task.Run(() => _ftp.CreateDirectory(remotePath)).GetAwaiter().GetResult();
            }
            else
            {
                // Create empty file
                Task.Run(() => _ftp.UploadFile(remotePath, [])).GetAwaiter().GetResult();
            }

            _cache.InvalidateParent(remotePath);

            var node = new FileNode(remotePath, isDir);
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
                var data = Task.Run(() => _ftp.DownloadFile(node.RemotePath)).GetAwaiter().GetResult();
                node.ReadBuffer = new MemoryStream(data);
                node.ReadBufferLoaded = true;
                node.FileSize = data.Length;
            }

            var stream = node.WriteBuffer ?? node.ReadBuffer;
            if (stream == null)
                return STATUS_END_OF_FILE;

            if ((long)offset >= stream.Length)
                return STATUS_END_OF_FILE;

            stream.Position = (long)offset;
            var toRead = (int)Math.Min(length, stream.Length - (long)offset);
            var buf = new byte[toRead];
            var read = stream.Read(buf, 0, toRead);
            Marshal.Copy(buf, 0, buffer, read);
            bytesTransferred = (uint)read;
            return STATUS_SUCCESS;
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
            node.WriteBuffer ??= new MemoryStream();

            // If we had a read buffer but no write buffer yet, copy read data to write buffer
            if (node.ReadBufferLoaded && node.WriteBuffer.Length == 0 && node.ReadBuffer != null && node.ReadBuffer.Length > 0)
            {
                node.ReadBuffer.Position = 0;
                node.ReadBuffer.CopyTo(node.WriteBuffer);
            }

            var pos = writeToEndOfFile ? node.WriteBuffer.Length : (long)offset;

            if (constrainedIo && pos + length > node.WriteBuffer.Length)
            {
                length = (uint)Math.Max(0, node.WriteBuffer.Length - pos);
                if (length == 0)
                    return STATUS_SUCCESS;
            }

            // Expand if needed
            if (pos + length > node.WriteBuffer.Length)
                node.WriteBuffer.SetLength(pos + length);

            node.WriteBuffer.Position = pos;
            var buf = new byte[length];
            Marshal.Copy(buffer, buf, 0, (int)length);
            node.WriteBuffer.Write(buf, 0, (int)length);

            bytesTransferred = length;
            node.IsDirty = true;
            node.FileSize = node.WriteBuffer.Length;
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
            node.WriteBuffer ??= new MemoryStream();
            node.WriteBuffer.SetLength((long)newSize);
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
                    Task.Run(() => _ftp.DeleteDirectory(remotePath)).GetAwaiter().GetResult();
                else
                    Task.Run(() => _ftp.DeleteFile(remotePath)).GetAwaiter().GetResult();

                _cache.InvalidateParent(remotePath);
                _cache.Invalidate(remotePath);
                return;
            }

            // Upload dirty write buffer on close
            if (node.IsDirty && node.WriteBuffer != null)
            {
                Log.Debug("Cleanup upload: {Path} ({Bytes} bytes)", node.RemotePath, node.WriteBuffer.Length);
                node.WriteBuffer.Position = 0;
                var uploadStream = node.WriteBuffer;
                Task.Run(() => _ftp.UploadFile(node.RemotePath, uploadStream)).GetAwaiter().GetResult();
                node.IsDirty = false;
                _cache.InvalidateParent(node.RemotePath);
            }
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

            var entryNode = FileNode.FromListItem(node.RemotePath, entry);
            FillFileInfo(entryNode, out fileInfo);
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

            Task.Run(() => _ftp.Rename(fromPath, toPath)).GetAwaiter().GetResult();
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

        // Use Task.Run to avoid potential deadlocks, with a 30s timeout
        var task = Task.Run(() => _ftp.ListDirectory(remotePath));
        if (!task.Wait(TimeSpan.FromSeconds(30)))
        {
            Log.Warning("ListDirectory timed out after 30s: {Path}", remotePath);
            throw new TimeoutException($"ListDirectory timed out: {remotePath}");
        }

        var items = task.Result;
        Log.Debug("Listed {Path}: {Count} entries", remotePath, items.Length);
        _cache.Set(remotePath, items);
        return items;
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
