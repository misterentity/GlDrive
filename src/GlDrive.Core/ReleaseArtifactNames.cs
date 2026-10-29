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

    /// <summary>
    /// A section-listing entry that cannot be a release: zipscript status links
    /// ([incomplete]-X, (no-sfv)-X, [no-nfo]-X), dot metadata and -MISSING stubs. Keyed on the
    /// class property — release names begin with a letter or digit — not on observed tags.
    /// </summary>
    public static bool IsSectionIndicatorName(string name) =>
        name.Length == 0 || !char.IsLetterOrDigit(name[0]);

    public static bool IsZeroByteMarker(string name, long size) =>
        size == 0 && (name.StartsWith('-') || name.StartsWith('['));
}
