using System.IO;
using Serilog;
using SharpCompress.Archives;
using SharpCompress.Archives.Rar;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace GlDrive.Downloads.ArchiveExtraction;

// Download auto-extract: every RAR set in one finished download directory, extracted in place.
internal sealed partial class ArchiveUnpacker
{
    private static readonly IProgress<ArchiveProgress> NoProgress = new Progress<ArchiveProgress>();

    /// <summary>
    /// Extract every first RAR volume in <paramref name="dirPath"/> into that directory on a
    /// BelowNormal thread. Disk space is reserved before any entry is written; a shortage (or a
    /// disk-full error mid-write) surfaces as <see cref="ExtractionSpaceException"/> so the
    /// download can wait and retry. Old-style .r00 sets are opened with an explicit volume list,
    /// and a set SharpCompress cannot read falls back to the external UnRAR/7-Zip tool.
    /// </summary>
    internal Task<ArchiveExtractionResult> ExtractDirectoryAsync(string dirPath, CancellationToken ct,
        DiskReservation disk, Action<Stream, string, CancellationToken, long>? copy = null)
    {
        var dir = new DirectoryInfo(dirPath);
        if (!dir.Exists) return Task.FromResult(new ArchiveExtractionResult(false, []));

        // RAR only: DownloadExtractionCheckpoint verifies the same inputs before extraction.
        var rarFiles = dir.GetFiles("*.rar")
            .Where(f => ArchiveVolumes.IsFirstVolume(f.FullName))
            .ToList();

        if (rarFiles.Count == 0) return Task.FromResult(new ArchiveExtractionResult(false, []));

        var tcs = new TaskCompletionSource<ArchiveExtractionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                foreach (var rarFile in rarFiles)
                {
                    ct.ThrowIfCancellationRequested();
                    ExtractRarSetInPlace(dirPath, rarFile, ct, disk, copy);
                }
                tcs.SetResult(new ArchiveExtractionResult(true,
                    rarFiles.Select(f => f.FullName).ToList()));
            }
            catch (OperationCanceledException)
            {
                tcs.SetCanceled(ct);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = $"Extract-{Path.GetFileName(dirPath)}"
        };
        thread.Start();
        return tcs.Task;
    }

    private void ExtractRarSetInPlace(string dirPath, FileInfo rarFile, CancellationToken ct,
        DiskReservation disk, Action<Stream, string, CancellationToken, long>? copy)
    {
        Log.Information("Extracting archive: {File}", rarFile.Name);

        var safeDirPath = Path.GetFullPath(dirPath);
        if (!safeDirPath.EndsWith(Path.DirectorySeparatorChar))
            safeDirPath += Path.DirectorySeparatorChar;

        string? reservedRoot = null;
        long requiredBytes = 0;

        void Reserve(long bytes)
        {
            requiredBytes = bytes;
            if (!disk.TryReserve(dirPath, requiredBytes, out var root))
                throw new ExtractionSpaceException(dirPath, requiredBytes);
            reservedRoot = root;
        }

        void ReleaseReservation()
        {
            if (reservedRoot == null) return;
            disk.Release(reservedRoot, requiredBytes);
            reservedRoot = null;
        }

        try
        {
            var volumes = ArchiveVolumes.TryDiscoverRarVolumes(rarFile.FullName);
            var volumeFiles = volumes ?? [rarFile];
            if (volumeFiles.Any(v => !DownloadExtractionCheckpoint.SafePath(dirPath, v.FullName)))
                throw new IOException("Archive source contains a reparse point or escaped its directory");

            try
            {
                using var archive = volumes is { Count: > 1 }
                    ? ArchiveFactory.OpenArchive(volumes, new ReaderOptions())
                    : RarArchive.OpenArchive(rarFile.FullName);
                var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();
                if (entries.Count == 0)
                {
                    Log.Warning("Archive has no file entries: {File}", rarFile.Name);
                    return;
                }

                Log.Information("Extracting {Count} entries from {File}", entries.Count, rarFile.Name);
                // Count exactly the entries the write loop admits. Existing outputs
                // remain on disk until their atomic replacements are complete.
                long needed = 0;
                foreach (var entry in entries)
                {
                    if (entry.Key == null || ExecutableExtensions.IsExecutable(entry.Key)) continue;
                    var target = Path.GetFullPath(Path.Combine(safeDirPath, entry.Key));
                    if (!target.StartsWith(safeDirPath, StringComparison.OrdinalIgnoreCase) ||
                        !DownloadExtractionCheckpoint.SafePath(dirPath, target)) continue;
                    needed = checked(needed + Math.Max(0, entry.Size));
                }
                Reserve(needed);

                foreach (var entry in entries)
                {
                    ct.ThrowIfCancellationRequested();

                    if (entry.Key == null) continue;

                    // Prevent path traversal (Zip Slip)
                    var fullPath = Path.GetFullPath(Path.Combine(safeDirPath, entry.Key));
                    if (!fullPath.StartsWith(safeDirPath, StringComparison.OrdinalIgnoreCase) ||
                        !DownloadExtractionCheckpoint.SafePath(dirPath, fullPath))
                    {
                        Log.Warning("Skipping archive entry with path traversal: {Key}", entry.Key);
                        continue;
                    }

                    // The torrent content gate allows archives — it has to, because that is how
                    // scene releases ship — and cannot see inside them. This is where the inside
                    // becomes files on disk, and the watch folders (E:\movies, D:\x265) are the
                    // same folders torrents are saved to. Without this check,
                    // Movie.2024.rar -> Sample/setup.exe walks straight past the torrent policy.
                    if (ExecutableExtensions.IsExecutable(entry.Key))
                    {
                        Log.Warning("Skipping executable archive entry: {Key}", entry.Key);
                        continue;
                    }

                    // Commit only complete entries so cancellation or corruption never
                    // leaves a truncated file at the final destination.
                    Log.Debug("Extracting entry: {Key} ({Size} bytes)", entry.Key, entry.Size);
                    using var entryStream = entry.OpenEntryStream();
                    if (copy != null) copy(entryStream, fullPath, ct, entry.Size);
                    else ArchiveFileOperations.CopyToFileAtomically(entryStream, fullPath, ct, entry.Size);
                }
                Log.Information("Extraction complete: {File} ({Count} files)", rarFile.Name, entries.Count);
            }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidFormatException
                                          or MultiVolumeExtractionException)
            {
                // Same SharpCompress failure family the watch-folder extractor falls back on
                // ("Unknown Rar Header" on scene .r00 sets). The external tool stages its output
                // and never writes into the release folder directly. A decoder may fail after
                // entry discovery, so retain the known unpacked requirement. The archive size
                // is only an estimate when discovery failed before sizes were available.
                Log.Warning("Download extract: SharpCompress failed ({Msg}), trying external tool: {File}",
                    ex.Message, rarFile.Name);
                ReleaseReservation();
                if (ContainsReparsePoint(dirPath))
                    throw new IOException("Archive output folder contains a reparse point");
                Reserve(Math.Max(requiredBytes, volumeFiles.Sum(v => v.Length)));
                if (!externalTool.TryExtract(rarFile.FullName, dirPath, "", overwriteMode: 0, NoProgress, ct))
                    throw;
                Log.Information("Extraction complete: {File} (external tool)", rarFile.Name);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (ExtractionSpaceException) { throw; }
        catch (Exception ex) when (ExtractSpaceParking.IsDiskFull(ex))
        {
            throw new ExtractionSpaceException(dirPath, requiredBytes, ex);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to extract {File}", rarFile.Name);
            throw;
        }
        finally
        {
            ReleaseReservation();
        }
    }

    private static bool ContainsReparsePoint(string dirPath)
    {
        if (!DownloadExtractionCheckpoint.SafePath(dirPath, dirPath)) return true;
        return Directory.EnumerateDirectories(dirPath, "*", SearchOption.AllDirectories)
            .Any(d => (File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0);
    }
}
