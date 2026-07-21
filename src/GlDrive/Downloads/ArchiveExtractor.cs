using System.IO;
using System.Text.RegularExpressions;
using SharpCompress.Archives;
using SharpCompress.Archives.Rar;
using Serilog;

namespace GlDrive.Downloads;

public sealed record ArchiveExtractionResult(bool Extracted, IReadOnlyList<string> FirstVolumes);

public static partial class ArchiveExtractor
{
    // Old-style volumes: .r00-.r99, .s00-.s99 (2 digits)
    // Extended volumes: .r000-.r999, .s000-.s999 (3 digits)
    // Split archives: .001-.999
    private static readonly Regex VolumeExtRegex = MyVolumeExtRegex();

    // Modern RAR multi-part: name.part02.rar, name.part003.rar (non-first volumes)
    private static readonly Regex PartNonFirstRegex = PartNonFirstVolumeRegex();

    public static Task<ArchiveExtractionResult> ExtractIfNeeded(string dirPath, CancellationToken ct)
    {
        var dir = new DirectoryInfo(dirPath);
        if (!dir.Exists) return Task.FromResult(new ArchiveExtractionResult(false, []));

        var rarFiles = dir.GetFiles("*.rar")
            .Where(f => !PartNonFirstRegex.IsMatch(f.Name))
            .ToList();

        if (rarFiles.Count == 0) return Task.FromResult(new ArchiveExtractionResult(false, []));

        // Run extraction on a dedicated low-priority thread to avoid starving
        // the UI thread and thread pool. Decompression is CPU-heavy.
        var tcs = new TaskCompletionSource<ArchiveExtractionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                ExtractOnThread(dirPath, rarFiles, ct);
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

    private static void ExtractOnThread(string dirPath, List<FileInfo> rarFiles, CancellationToken ct)
    {
        var safeDirPath = Path.GetFullPath(dirPath);
        if (!safeDirPath.EndsWith(Path.DirectorySeparatorChar))
            safeDirPath += Path.DirectorySeparatorChar;

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

                    // Commit only complete entries so cancellation or corruption never
                    // leaves a truncated file at the final destination.
                    Log.Debug("Extracting entry: {Key} ({Size} bytes)", entry.Key, entry.Size);
                    using var entryStream = entry.OpenEntryStream();
                    ArchiveFileOperations.CopyToFileAtomically(entryStream, fullPath, ct);
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

    public static bool DeleteArchiveSet(string firstVolumePath) =>
        ArchiveFileOperations.DeleteRarSet(firstVolumePath);

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
