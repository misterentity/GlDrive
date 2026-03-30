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

    public static async Task<bool> ExtractIfNeeded(string dirPath, CancellationToken ct)
    {
        var dir = new DirectoryInfo(dirPath);
        if (!dir.Exists) return false;

        // Find .rar files only (first volume) — skip numbered volumes like .r00, .r01
        // and non-first modern multi-part volumes like .part02.rar, .part03.rar
        var rarFiles = dir.GetFiles("*.rar")
            .Where(f => !PartNonFirstRegex.IsMatch(f.Name))
            .ToList();

        if (rarFiles.Count == 0) return false;

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
                var safeDirPath = Path.GetFullPath(dirPath);
                foreach (var entry in entries)
                {
                    ct.ThrowIfCancellationRequested();

                    // Prevent path traversal (Zip Slip)
                    if (entry.Key != null)
                    {
                        var fullPath = Path.GetFullPath(Path.Combine(safeDirPath, entry.Key));
                        if (!fullPath.StartsWith(safeDirPath, StringComparison.OrdinalIgnoreCase))
                        {
                            Log.Warning("Skipping archive entry with path traversal: {Key}", entry.Key);
                            continue;
                        }
                    }

                    Log.Debug("Extracting entry: {Key} ({Size} bytes)", entry.Key, entry.Size);
                    await entry.WriteToDirectoryAsync(dirPath, new SharpCompress.Common.ExtractionOptions
                    {
                        ExtractFullPath = true,
                        Overwrite = true,
                    });
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

        return true;
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
    /// Try to delete a file with retries to handle Windows file handle release delays
    /// (e.g. after SharpCompress disposes archive streams).
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
