using System.IO;
using System.Text.RegularExpressions;
using Serilog;

namespace GlDrive.Downloads;

internal static class ArchiveFileOperations
{
    internal const int BufferSize = 256 * 1024;

    internal static void CopyToFileAtomically(Stream source, string destination, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(destination)
            ?? throw new IOException($"Destination has no parent directory: {destination}");
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.gldrive-tmp");

        try
        {
            using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, BufferSize, FileOptions.SequentialScan))
            {
                var buffer = GC.AllocateUninitializedArray<byte>(BufferSize);
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var read = source.Read(buffer, 0, buffer.Length);
                    if (read == 0) break;
                    output.Write(buffer, 0, read);
                }
                ct.ThrowIfCancellationRequested();
                output.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(tempPath, destination, overwrite: true);
        }
        finally
        {
            try { File.Delete(tempPath); }
            catch (Exception ex) { Log.Debug(ex, "Failed to remove extraction temp file {Path}", tempPath); }
        }
    }

    internal static IReadOnlyList<string> GetRarSetFiles(string firstVolumePath)
    {
        var directory = Path.GetDirectoryName(firstVolumePath);
        if (directory == null || !Directory.Exists(directory)) return [];

        var fileName = Path.GetFileName(firstVolumePath);
        var modern = Regex.Match(fileName, @"^(?<base>.+)\.part\d+\.rar$", RegexOptions.IgnoreCase);
        var plainBase = Path.GetFileNameWithoutExtension(fileName);
        var setBase = modern.Success ? modern.Groups["base"].Value : plainBase;
        var modernPattern = modern.Success
            ? new Regex($@"^{Regex.Escape(setBase)}\.part\d+\.rar$", RegexOptions.IgnoreCase)
            : null;
        var oldVolumePattern = new Regex(
            $@"^{Regex.Escape(plainBase)}\.(?:[rs]\d{{2,3}}|\d{{3,}})$",
            RegexOptions.IgnoreCase);

        var result = new List<string>();
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            var candidate = Path.GetFileName(path);
            var matches = modernPattern != null
                ? modernPattern.IsMatch(candidate)
                : candidate.Equals(fileName, StringComparison.OrdinalIgnoreCase) ||
                  oldVolumePattern.IsMatch(candidate);
            matches |= candidate.Equals($"{setBase}.sfv", StringComparison.OrdinalIgnoreCase);
            if (matches) result.Add(path);
        }
        return result;
    }

    internal static bool DeleteRarSet(string firstVolumePath)
    {
        IReadOnlyList<string> files;
        try { files = GetRarSetFiles(firstVolumePath); }
        catch (Exception ex)
        {
            Log.Warning(ex, "Unable to enumerate archive set for {Path}", firstVolumePath);
            return false;
        }

        var allDeleted = true;
        foreach (var path in files)
        {
            var deleted = false;
            for (var attempt = 1; attempt <= 5 && !deleted; attempt++)
            {
                try { File.Delete(path); deleted = true; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 5)
                {
                    Thread.Sleep(200 * attempt);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to delete archive volume {File}", path);
                    allDeleted = false;
                    break;
                }
            }
        }
        return allDeleted;
    }
}
