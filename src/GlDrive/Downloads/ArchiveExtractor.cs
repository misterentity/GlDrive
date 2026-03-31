using System.IO;
using System.Text.RegularExpressions;
using SharpCompress.Archives;
using SharpCompress.Archives.Rar;
using Serilog;

namespace GlDrive.Downloads;

public static partial class ArchiveExtractor
{
    // Old-style volumes: .r00-.r99, .s00-.s99 (2 digits)
    // Extended volumes: .r000-.r999, .s000-.s999 (3 digits)
    // Split archives: .001-.999
    private static readonly Regex VolumeExtRegex = MyVolumeExtRegex();

    // Modern RAR multi-part: name.part02.rar, name.part003.rar (non-first volumes)
    private static readonly Regex PartNonFirstRegex = PartNonFirstVolumeRegex();

    private const int ExtractBufferSize = 256 * 1024; // 256 KB

    public static Task<bool> ExtractIfNeeded(string dirPath, CancellationToken ct)
    {
        var dir = new DirectoryInfo(dirPath);
        if (!dir.Exists) return Task.FromResult(false);

        var rarFiles = dir.GetFiles("*.rar")
            .Where(f => !PartNonFirstRegex.IsMatch(f.Name))
            .ToList();

        if (rarFiles.Count == 0) return Task.FromResult(false);

        // Run extraction on a dedicated low-priority thread to avoid starving
        // the UI thread and thread pool. Decompression is CPU-heavy.
        var tcs = new TaskCompletionSource<bool>();
        var thread = new Thread(() =>
        {
            try
            {
                ExtractOnThread(dirPath, rarFiles, ct);
                tcs.SetResult(true);
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

    private static void ExtractOnThread(string dirPath, List<FileInfo> rarFiles, CancellationToken ct)
    {
        var safeDirPath = Path.GetFullPath(dirPath);

        foreach (var rarFile in rarFiles)
        {
            ct.ThrowIfCancellationRequested();
            Log.Information("Extracting archive: {File}", rarFile.Name);

            try
            {
                using var archive = RarArchive.OpenArchive(rarFile.FullName);
                var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();
                if (entries.Count == 0)
                {
                    Log.Warning("Archive has no file entries: {File}", rarFile.Name);
                    continue;
                }

                Log.Information("Extracting {Count} entries from {File}", entries.Count, rarFile.Name);
                foreach (var entry in entries)
                {
                    ct.ThrowIfCancellationRequested();

                    if (entry.Key == null) continue;

                    // Prevent path traversal (Zip Slip)
                    var fullPath = Path.GetFullPath(Path.Combine(safeDirPath, entry.Key));
                    if (!fullPath.StartsWith(safeDirPath, StringComparison.OrdinalIgnoreCase))
                    {
                        Log.Warning("Skipping archive entry with path traversal: {Key}", entry.Key);
                        continue;
                    }

                    // Ensure target directory exists
                    var entryDir = Path.GetDirectoryName(fullPath);
                    if (entryDir != null) Directory.CreateDirectory(entryDir);

                    // Manual streaming extraction with large buffer and sequential I/O hints
                    Log.Debug("Extracting entry: {Key} ({Size} bytes)", entry.Key, entry.Size);
                    using var entryStream = entry.OpenEntryStream();
                    using var outStream = new FileStream(fullPath, FileMode.Create, FileAccess.Write,
                        FileShare.None, ExtractBufferSize, FileOptions.SequentialScan);
                    entryStream.CopyTo(outStream, ExtractBufferSize);
                }
                Log.Information("Extraction complete: {File} ({Count} files)", rarFile.Name, entries.Count);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to extract {File}", rarFile.Name);
                throw;
            }
        }
    }

    public static void DeleteArchives(string dirPath)
    {
        var dir = new DirectoryInfo(dirPath);
        if (!dir.Exists) return;

        // Collect archive files to delete
        var toDelete = dir.GetFiles("*", SearchOption.AllDirectories)
            .Where(f => IsArchiveFile(f.Name))
            .ToList();

        if (toDelete.Count == 0)
        {
            Log.Information("No archive files found to delete in {Dir}", dir.Name);
            return;
        }

        Log.Information("Deleting {Count} archive files from {Dir}: {Files}",
            toDelete.Count, dir.Name, string.Join(", ", toDelete.Select(f => f.Name)));

        var deleted = 0;
        var failed = 0;
        foreach (var file in toDelete)
        {
            if (TryDeleteWithRetry(file.FullName))
                deleted++;
            else
                failed++;
        }

        Log.Information("Archive cleanup: {Deleted} deleted, {Failed} failed in {Dir}", deleted, failed, dir.Name);
    }

    /// <summary>
    /// Try to delete a file with retries to handle Windows file handle release delays.
    /// </summary>
    private static bool TryDeleteWithRetry(string path, int maxAttempts = 3, int delayMs = 500)
    {
        for (var i = 0; i < maxAttempts; i++)
        {
            try
            {
                File.Delete(path);
                return true;
            }
            catch (IOException) when (i < maxAttempts - 1)
            {
                Thread.Sleep(delayMs * (i + 1));
            }
            catch (UnauthorizedAccessException) when (i < maxAttempts - 1)
            {
                Thread.Sleep(delayMs * (i + 1));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to delete archive file: {File}", Path.GetFileName(path));
                return false;
            }
        }
        return false;
    }

    public static bool IsArchiveFile(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(ext)) return false;

        // .rar (includes .part01.rar, .part02.rar since extension is still .rar)
        if (ext.Equals(".rar", StringComparison.OrdinalIgnoreCase))
            return true;

        // .sfv (checksum file, part of the archive set)
        if (ext.Equals(".sfv", StringComparison.OrdinalIgnoreCase))
            return true;

        // .nfo (info file, part of scene release)
        // NOT deleted — users want to keep these

        // .zip, .7z, .tar, .gz, .bz2, .xz, .lz, .lzma, .iso, .cab
        if (ext.Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".7z", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".tar", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".gz", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".bz2", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".xz", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".iso", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".cab", StringComparison.OrdinalIgnoreCase))
            return true;

        // Old-style RAR volumes: .r00-.r99, .r000-.r999, .s00-.s99, .s000-.s999
        // Split archives: .001-.999
        if (VolumeExtRegex.IsMatch(ext))
            return true;

        return false;
    }

    // Matches .r00-.r999, .s00-.s999, .001-.999
    [GeneratedRegex(@"^\.[rs]\d{2,3}$|^\.\d{3}$", RegexOptions.IgnoreCase)]
    private static partial Regex MyVolumeExtRegex();

    // Matches non-first modern multi-part volumes: .part02.rar, .part003.rar (but NOT .part01.rar or .part001.rar)
    [GeneratedRegex(@"\.part(?!0*1\.rar)\d+\.rar$", RegexOptions.IgnoreCase)]
    private static partial Regex PartNonFirstVolumeRegex();
}
