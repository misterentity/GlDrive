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

    // Modern RAR multi-part: name.part02.rar, name.part003.rar
    private static readonly Regex PartNRegex = PartVolumeRegex();

    public static async Task<bool> ExtractIfNeeded(string dirPath, CancellationToken ct)
    {
        var dir = new DirectoryInfo(dirPath);
        if (!dir.Exists) return false;

        // Find .rar files only (first volume) — skip numbered volumes like .r00, .r01, etc.
        var rarFiles = dir.GetFiles("*.rar")
            .Where(f => f.Extension.Equals(".rar", StringComparison.OrdinalIgnoreCase))
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

        var deleted = 0;
        // Scan all files including subdirectories
        foreach (var file in dir.GetFiles("*", SearchOption.AllDirectories))
        {
            if (IsArchiveFile(file.Name))
            {
                try
                {
                    file.Delete();
                    deleted++;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to delete archive file: {File}", file.Name);
                }
            }
        }

        if (deleted > 0)
            Log.Information("Deleted {Count} archive files from {Dir}", deleted, dir.Name);
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

    // Matches .partNN.rar pattern (for reference, not used in IsArchiveFile since .rar catches it)
    [GeneratedRegex(@"\.part\d+\.rar$", RegexOptions.IgnoreCase)]
    private static partial Regex PartVolumeRegex();
}
