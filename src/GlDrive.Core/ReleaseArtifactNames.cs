namespace GlDrive.Downloads;

/// <summary>Shared glftpd filename facts; each consumer owns its completion/status policy.</summary>
public static class ReleaseArtifactNames
{
    public static bool IsMissingPlaceholderName(string name) =>
        name.StartsWith("-missing-", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".missing", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("-missing", StringComparison.OrdinalIgnoreCase);

    public static bool IsSiteMetadataName(string name) =>
        name.StartsWith('.') ||
        name.EndsWith(".imdb.html", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".imdb.nfo", StringComparison.OrdinalIgnoreCase);

    public static bool IsZeroByteMarker(string name, long size) =>
        size == 0 && (name.StartsWith('-') || name.StartsWith('['));
}
