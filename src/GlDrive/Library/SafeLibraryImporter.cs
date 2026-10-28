using System.Buffers;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using GlDrive.Config;
using Microsoft.Win32.SafeHandles;

namespace GlDrive.Library;

public sealed record LibraryImportResult(string Status, string SourcePath, string DestinationPath,
    bool Verified, bool SourceRemoved, string Verification);

/// <summary>
/// Moves only the explicitly selected staged file. The source is locked against writes and
/// renames for the entire operation. Copies publish a verified temporary sibling by atomic,
/// no-replace rename; cleanup can delete only the temporary file created by this import.
/// </summary>
public sealed class SafeLibraryImporter
{
    private readonly Action<string> _ensureUnowned;
    internal bool ForceCopyForTests { get; init; }
    internal Action<string>? Checkpoint { get; init; }

    public SafeLibraryImporter(Action<string>? ensureUnowned = null) =>
        _ensureUnowned = ensureUnowned ?? new LibraryImportOwnership(ConfigManager.AppDataPath).EnsureUnowned;

    public Task<LibraryImportResult> ImportAsync(LibraryImportPlan plan, LibraryConfig config, CancellationToken ct = default,
        Func<LibraryConfig>? readCurrentSettings = null) =>
        Task.Run(() => Import(plan, config, ct, readCurrentSettings), ct);

    private LibraryImportResult Import(LibraryImportPlan plan, LibraryConfig config, CancellationToken ct,
        Func<LibraryConfig>? readCurrentSettings)
    {
        if (!config.Enabled) throw new InvalidOperationException("Library imports are disabled.");
        if (config.HashVerifyMaxBytes < 0) throw new InvalidOperationException("The hash verification threshold cannot be negative.");
        // Recompute destination/roots from current settings; callers cannot supply a forged plan.
        var current = LibraryPlanner.Plan(config, plan.SourcePath, plan.ReleaseName);
        if (current != plan) throw new IOException("The staged file or import settings changed after this plan was made. Build a fresh plan.");
        ct.ThrowIfCancellationRequested();
        if (config.DryRun) return new("dry-run", plan.SourcePath, plan.DestinationPath, false, false, "none");

        // Hold the configured roots and each descendant directory without FILE_SHARE_DELETE.
        // Windows refuses renaming an ancestor containing open handles, so this also protects
        // higher ancestors without requesting handles to unrelated profile/system folders.
        using var directories = new DirectoryLocks();
        directories.Acquire(plan.StagingRoot, Path.GetDirectoryName(plan.SourcePath)!, create: false);
        directories.Acquire(plan.LibraryRoot, Path.GetDirectoryName(plan.DestinationPath)!, create: true);
        using var sourceHandle = Open(plan.SourcePath, GenericRead | DeleteAccess, ShareRead, OpenExisting, OpenReparsePoint);
        var before = Information(sourceHandle);
        if ((before.Attributes & (ReparseAttribute | DirectoryAttribute)) != 0 || before.Length != plan.ExpectedLength
            || before.LastWriteUtc != plan.ExpectedLastWriteUtc || !SamePath(FinalPath(sourceHandle), plan.SourcePath))
            throw new IOException("The staged file identity changed or is not a regular file.");
        using var source = new FileStream(sourceHandle, FileAccess.Read);
        _ensureUnowned(plan.SourcePath);
        var hash = before.Length <= config.HashVerifyMaxBytes ? Hash(source) : null;
        var verification = hash == null ? "size" : "size+sha256";
        ct.ThrowIfCancellationRequested();

        if (!ForceCopyForTests)
        {
            Checkpoint?.Invoke("before-rename");
            ct.ThrowIfCancellationRequested();
            _ensureUnowned(plan.SourcePath);
            EnsureStillAuthorized(plan, config, readCurrentSettings);
            var renameError = Rename(sourceHandle, plan.DestinationPath);
            if (renameError == 0)
            {
                // Atomic same-volume rename preserves the opened identity. Keep the handle
                // locked while verifying that it now names the destination and retains data.
                try { Verify(source, sourceHandle, plan.DestinationPath, before.Length, hash); }
                catch (IOException ex)
                {
                    throw new IOException("The file was renamed to the destination, which is retained; final verification failed.", ex);
                }
                return new("imported", plan.SourcePath, plan.DestinationPath, true, true, "rename:" + verification);
            }
            if (renameError != ErrorNotSameDevice) throw NativeFailure("The destination was not replaced; the staged source is retained", renameError);
        }

        Checkpoint?.Invoke("before-copy");
        ct.ThrowIfCancellationRequested();
        _ensureUnowned(plan.SourcePath);
        var temporaryPath = Path.Combine(Path.GetDirectoryName(plan.DestinationPath)!,
            "." + Path.GetFileName(plan.DestinationPath) + "." + Guid.NewGuid().ToString("N") + ".gldrive-tmp");
        using var temporaryHandle = Open(temporaryPath, GenericRead | GenericWrite | DeleteAccess, ShareRead, CreateNew, OpenReparsePoint);
        using var destination = new FileStream(temporaryHandle, FileAccess.ReadWrite);
        var published = false;
        try
        {
            source.Position = 0;
            var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
            try
            {
                int read;
                while ((read = source.Read(buffer)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    destination.Write(buffer, 0, read);
                    Checkpoint?.Invoke("copy-chunk");
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
            destination.Flush(flushToDisk: true);
            Checkpoint?.Invoke("copied");
            ct.ThrowIfCancellationRequested();
            Verify(destination, temporaryHandle, temporaryPath, before.Length, hash);
            // Confirm the still-open source is the identity we copied. Writes/rename/delete are
            // denied by its sharing mode, including attempts through another hard-link name.
            var after = Information(sourceHandle);
            if (before.Volume != after.Volume || before.Index != after.Index || before.Length != after.Length
                || before.LastWriteUtc != after.LastWriteUtc || !SamePath(FinalPath(sourceHandle), plan.SourcePath))
                throw new IOException("The source changed during import; it was retained.");
            Checkpoint?.Invoke("copy-verified");
            ct.ThrowIfCancellationRequested();
            EnsureStillAuthorized(plan, config, readCurrentSettings);
            var renameError = Rename(temporaryHandle, plan.DestinationPath);
            if (renameError != 0) throw NativeFailure("The destination was not replaced; the staged source is retained", renameError);
            published = true;
            Verify(destination, temporaryHandle, plan.DestinationPath, before.Length, hash);
            Checkpoint?.Invoke("verified");
            ct.ThrowIfCancellationRequested();
            _ensureUnowned(plan.SourcePath);
            EnsureStillAuthorized(plan, config, readCurrentSettings);
            MarkSourceForDeletion(sourceHandle);
            // Disposition removes this locked identity when its handle closes, not a potentially
            // replaced path after close. Destination remains locked until this method returns.
            source.Dispose();
            return new("imported", plan.SourcePath, plan.DestinationPath, true, true, "copy:" + verification);
        }
        finally
        {
            if (!published)
            {
                // Use the handle rather than a path: only this call's CreateNew file is owned.
                try { MarkSourceForDeletion(temporaryHandle); }
                catch (IOException) { Serilog.Log.Warning("Could not remove an unpublished library import temporary file"); }
            }
        }
    }

    private static void EnsureStillAuthorized(LibraryImportPlan plan, LibraryConfig initial,
        Func<LibraryConfig>? readCurrentSettings)
    {
        var current = readCurrentSettings?.Invoke() ?? initial;
        if (!current.Enabled || current.DryRun || !SamePath(LibraryPlanner.NormalizeRoot(current.StagingRoot), plan.StagingRoot)
            || !SamePath(LibraryPlanner.NormalizeRoot(plan.Identity.Kind == "movie" ? current.MovieRoot : current.TvRoot), plan.LibraryRoot))
            throw new IOException("Library import permission or roots changed; the staged source is retained.");
    }

    private static void Verify(FileStream stream, SafeFileHandle handle, string path, long length, byte[]? hash)
    {
        if (stream.Length != length || Information(handle).Length != length || !SamePath(FinalPath(handle), path))
            throw new IOException("Destination size or identity verification failed.");
        if (hash != null && !CryptographicOperations.FixedTimeEquals(hash, Hash(stream)))
            throw new IOException("Destination hash verification failed.");
    }

    // Callers must hold DirectoryLocks for both parent directories. These operations
    // act on the opened identity and never overwrite a destination or follow a link.
    internal static void RenameStagedFile(string source, string destination, long length, DateTime lastWriteUtc)
    {
        using var handle = Open(source, GenericRead | DeleteAccess, ShareRead, OpenExisting, OpenReparsePoint);
        ValidateStagedIdentity(handle, source, length, lastWriteUtc);
        var error = Rename(handle, destination);
        if (error != 0) throw NativeFailure("The staged file was retained; quarantine could not be created", error);
    }

    internal static void DeleteStagedFile(string path, long length, DateTime lastWriteUtc)
    {
        using var handle = Open(path, GenericRead | DeleteAccess, ShareRead, OpenExisting, OpenReparsePoint);
        ValidateStagedIdentity(handle, path, length, lastWriteUtc);
        MarkSourceForDeletion(handle);
    }

    internal static void DeleteEmptyStagedDirectory(string path)
    {
        using var handle = Open(path, ListDirectory | ReadAttributes | DeleteAccess, ShareRead, OpenExisting,
            BackupSemantics | OpenReparsePoint);
        var info = Information(handle);
        if ((info.Attributes & ReparseAttribute) != 0 || (info.Attributes & DirectoryAttribute) == 0 ||
            !SamePath(FinalPath(handle), path))
            throw new IOException("The staging directory identity changed; it was retained.");
        // Windows refuses this operation for a nonempty directory. The opened
        // identity cannot be swapped and no descendants are ever deleted.
        MarkSourceForDeletion(handle);
    }

    private static void ValidateStagedIdentity(SafeFileHandle handle, string path, long length, DateTime lastWriteUtc)
    {
        var info = Information(handle);
        if ((info.Attributes & (ReparseAttribute | DirectoryAttribute)) != 0 || info.Links != 1 ||
            info.Length != length || info.LastWriteUtc != lastWriteUtc || !SamePath(FinalPath(handle), path))
            throw new IOException("The staged file identity changed; it was retained.");
    }

    private static byte[] Hash(FileStream stream)
    {
        stream.Position = 0;
        return SHA256.HashData(stream);
    }

    internal sealed class DirectoryLocks : IDisposable
    {
        private readonly Dictionary<string, SafeFileHandle> _handles = new(StringComparer.OrdinalIgnoreCase);
        internal void Acquire(string root, string directory, bool create)
        {
            EnsureRoot(root, create);
            var relative = Path.GetRelativePath(root, directory);
            if (relative == ".") return;
            if (!LibraryPlanner.IsWithin(directory, root)) throw new IOException("Import directory is outside its locked root.");
            var current = root;
            foreach (var component in relative.Split(Path.DirectorySeparatorChar))
            {
                current = Path.Combine(current, component);
                if (create && !Directory.Exists(current)) Directory.CreateDirectory(current);
                LockDirectory(current);
            }
        }

        private void EnsureRoot(string directory, bool create)
        {
            directory = Path.TrimEndingDirectorySeparator(directory);
            if (_handles.ContainsKey(directory)) return;
            if (!Directory.Exists(directory))
            {
                if (!create) throw new DirectoryNotFoundException("The staging root no longer exists.");
                var parent = Path.GetDirectoryName(directory) ?? throw new DirectoryNotFoundException("The library drive is unavailable.");
                // Creating a missing root starts at its nearest existing parent, already locked.
                EnsureRoot(parent, create: true);
                Directory.CreateDirectory(directory);
            }
            LockDirectory(directory);
        }

        private void LockDirectory(string directory)
        {
            if (_handles.ContainsKey(directory)) return;
            LibraryPlanner.RejectReparseAncestors(directory);
            var handle = Open(directory, ListDirectory | ReadAttributes, ShareRead | ShareWrite, OpenExisting, BackupSemantics | OpenReparsePoint);
            try
            {
                var info = Information(handle);
                if ((info.Attributes & ReparseAttribute) != 0 || (info.Attributes & DirectoryAttribute) == 0
                    || !SamePath(FinalPath(handle), directory))
                    throw new IOException("Import directories must be regular local folders with stable identities.");
                // Recheck after taking the lock so an acquisition-time reparse swap cannot pass.
                LibraryPlanner.RejectReparseAncestors(directory);
                _handles.Add(directory, handle);
            }
            catch { handle.Dispose(); throw; }
        }
        public void Dispose()
        {
            foreach (var handle in _handles.Values.Reverse()) handle.Dispose();
        }
    }

    // Ancestor rename protection is part of Windows filesystem semantics, not a sandbox bypass:
    // https://learn.microsoft.com/windows-hardware/drivers/ddi/ntifs/ns-ntifs-_file_rename_information
    // https://learn.microsoft.com/openspecs/windows_protocols/ms-fsa/87f86c9b-6c2a-4803-84b7-131a74a434fa

    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, DeleteAccess = 0x10000, ReadAttributes = 0x80, ListDirectory = 1;
    private const uint ShareRead = 1, ShareWrite = 2, CreateNew = 1, OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000, BackupSemantics = 0x02000000;
    private const uint ReparseAttribute = 0x400, DirectoryAttribute = 0x10;
    private const int ErrorNotSameDevice = 17;

    private static SafeFileHandle Open(string path, uint access, uint share, uint disposition, uint flags)
    {
        var handle = CreateFileW(path, access, share, IntPtr.Zero, disposition, flags, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw NativeFailure("Could not lock the import path", error);
    }

    private static IOException NativeFailure(string context, int error) => new(context + ": " + new Win32Exception(error).Message);

    private static FileInformation Information(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw NativeFailure("Could not verify file identity", Marshal.GetLastWin32Error());
        return info;
    }

    private static string FinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(32768);
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity) throw NativeFailure("Could not resolve file identity", Marshal.GetLastWin32Error());
        var path = buffer.ToString();
        return path.StartsWith("\\\\?\\", StringComparison.Ordinal) ? path[4..] : path;
    }

    private static bool SamePath(string left, string right) => string.Equals(Path.TrimEndingDirectorySeparator(left),
        Path.TrimEndingDirectorySeparator(right), StringComparison.OrdinalIgnoreCase);

    // FILE_RENAME_INFO is pointer-aligned: filename starts at offset 20 on x64,
    // 12 on x86. FileNameLength is UTF-16 bytes, with an absolute destination and
    // null RootDirectory. ReplaceIfExists/Flags remains zero. Microsoft reference:
    // https://learn.microsoft.com/windows/win32/api/winbase/ns-winbase-file_rename_info
    private static int Rename(SafeFileHandle source, string destination)
    {
        var bytes = Encoding.Unicode.GetBytes(destination);
        var offset = Marshal.OffsetOf<RenameHeader>(nameof(RenameHeader.FileName)).ToInt32();
        var size = Math.Max(Marshal.SizeOf<RenameHeader>(), offset + bytes.Length + 2);
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, buffer, size);
            Marshal.WriteInt32(buffer, Marshal.OffsetOf<RenameHeader>(nameof(RenameHeader.FileNameLength)).ToInt32(), bytes.Length);
            Marshal.Copy(bytes, 0, IntPtr.Add(buffer, offset), bytes.Length);
            return SetFileInformationByHandle(source, 3, buffer, (uint)size) ? 0 : Marshal.GetLastWin32Error();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static void MarkSourceForDeletion(SafeFileHandle source)
    {
        var buffer = Marshal.AllocHGlobal(1);
        try
        {
            Marshal.WriteByte(buffer, 1);
            if (!SetFileInformationByHandle(source, 4, buffer, 1))
                throw NativeFailure("The verified copy is retained and the staged source could not be removed", Marshal.GetLastWin32Error());
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RenameHeader
    {
        public uint Flags;
        public IntPtr RootDirectory;
        public uint FileNameLength;
        public char FileName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        public readonly long Length => ((long)SizeHigh << 32) | SizeLow;
        public readonly ulong Index => ((ulong)IndexHigh << 32) | IndexLow;
        public readonly DateTime LastWriteUtc => DateTime.FromFileTimeUtc(((long)WriteHigh << 32) | WriteLow);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint length, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, IntPtr information, uint size);
}
