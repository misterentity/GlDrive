using System.IO;
using System.Text.RegularExpressions;

namespace GlDrive.Downloads;

public sealed record ArchiveExtractionResult(bool Extracted, IReadOnlyList<string> FirstVolumes);

public sealed class ExtractionSpaceException(string outputDirectory, long requiredBytes, Exception? inner = null)
    : IOException($"Waiting for extraction space: need {requiredBytes:N0} additional bytes on {Path.GetPathRoot(outputDirectory)}", inner)
{
    public string OutputDirectory { get; } = outputDirectory;
    public long RequiredBytes { get; } = requiredBytes;
}

public static partial class ArchiveExtractor
{
    internal static readonly DiskReservation DefaultDiskReservation = new();
    // Old-style volumes: .r00-.r99, .s00-.s99 (2 digits)
    // Extended volumes: .r000-.r999, .s000-.s999 (3 digits)
    // Split archives: .001-.999
    private static readonly Regex VolumeExtRegex = MyVolumeExtRegex();

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
}
