using System.Buffers;

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

    private static readonly HashSet<string> AnnounceVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "brings", "leads", "racing", "wins", "complete", "completed", "incomplete",
        "pred", "nuke", "nuked", "unnuke", "unnuked", "delpre", "request", "filled",
        "the", "from", "for", "with", "and", "now", "new"
    };

    // Windows' Path.GetInvalidFileNameChars(), fixed here so Linux test runs judge names the same way.
    private static readonly SearchValues<char> InvalidReleaseNameChars = SearchValues.Create(
        "\"<>|\0\u0001\u0002\u0003\u0004\u0005\u0006\a\b\t\n\v\f\r\u000e\u000f\u0010\u0011\u0012\u0013\u0014" +
        "\u0015\u0016\u0017\u0018\u0019\u001a\u001b\u001c\u001d\u001e\u001f:*?\\/");

    /// <summary>
    /// Whether an announce capture can be a scene release name: one directory name of 10+ characters,
    /// ending in its group tag, with a structural token (dot/underscore separator, digit or upper-case
    /// letter). Rejects sitebot verbs, paths ("TV/Name"), prose tails ("Frankenstein,") and
    /// hyphenated lower-case prose ("father-to-be").
    /// </summary>
    public static bool IsPlausibleReleaseName(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        var value = candidate.Trim();
        if (value.Length < 10 || AnnounceVerbs.Contains(value)) return false;
        // A release is a single directory name ending in its group tag: a path ("TV/Name" from
        // an AUTONUKE notice) or a word glued to prose punctuation ("Frankenstein,") is not.
        if (value.AsSpan().IndexOfAny(InvalidReleaseNameChars) >= 0) return false;
        if (!(char.IsAsciiLetterOrDigit(value[^1]) || value[^1] is ')' or ']')) return false;
        return value.Any(c => c is '.' or '_' or >= '0' and <= '9' or >= 'A' and <= 'Z');
    }
}
