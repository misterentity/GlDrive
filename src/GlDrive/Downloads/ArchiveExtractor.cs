using System.IO;
using System.Text.RegularExpressions;
using SharpCompress.Archives;
using SharpCompress.Archives.Rar;
using Serilog;

namespace GlDrive.Downloads;

public static partial class ArchiveExtractor
{
    private static readonly Regex VolumeExtRegex = MyVolumeExtRegex();

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

        foreach (var file in dir.GetFiles())
        {
            if (IsArchiveFile(file.Name))
            {
                try
                {
                    file.Delete();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to delete archive file: {File}", file.Name);
                }
            }
        }
    }

    public static bool IsArchiveFile(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(ext)) return false;

        // .rar, .sfv
        if (ext.Equals(".rar", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".sfv", StringComparison.OrdinalIgnoreCase))
            return true;

        // .r00-.r99, .s00-.s99
        return VolumeExtRegex.IsMatch(ext);
    }

    [GeneratedRegex(@"^\.[rs]\d{2}$", RegexOptions.IgnoreCase)]
    private static partial Regex MyVolumeExtRegex();
}
