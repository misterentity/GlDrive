using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace GlDrive.Downloads;

/// <summary>Signals written by a site's zipscript, never release payload.</summary>
internal static class ReleaseSourceState
{
    private static readonly Regex Progress = new(@"(?<percent>\d+(?:\.\d+)?)\s*%[\s-]*Complete",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ArchiveVolume = new(@"\.(?:rar|[rs]\d{2,3})$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex StatusMarker = new(@"^\[\s*(?:in[-_ ]?complete|complete|nuked)\s*\]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex IncompleteStatus = new(@"^\[\s*in[-_ ]?complete\s*\]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CompleteStatus = new(@"^\[\s*complete\s*\]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static bool IsMissingPlaceholder(string name) =>
        name.StartsWith("-missing-", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".missing", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("-missing", StringComparison.OrdinalIgnoreCase);

    internal static bool IsIncompleteMarker(string name)
    {
        if (IsMissingPlaceholder(name)) return true;
        if (IncompleteStatus.IsMatch(name)) return true;
        if (name.Trim(' ', '-', '[', ']').Equals("incomplete", StringComparison.OrdinalIgnoreCase)) return true;
        var match = Progress.Match(name);
        return match.Success && double.TryParse(match.Groups["percent"].Value,
            NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var percent) && percent < 100;
    }

    internal static bool IsCompletionMarker(string name)
    {
        if (CompleteStatus.IsMatch(name)) return true;
        var match = Progress.Match(name);
        return match.Success && double.TryParse(match.Groups["percent"].Value,
            NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var percent) && percent == 100;
    }

    internal static bool IsSiteMetadata(string name, long size) =>
        IsMissingPlaceholder(name) || name.StartsWith('.') ||
        name.EndsWith(".imdb.html", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".imdb.nfo", StringComparison.OrdinalIgnoreCase) ||
        Progress.IsMatch(name) ||
        StatusMarker.IsMatch(name) ||
        (size == 0 && (name.StartsWith('-') || name.StartsWith('[')));

    internal static bool IsArchive(string path) => ArchiveVolume.IsMatch(Path.GetFileName(path));

    // A source can remain incomplete for hours. Bound network polling, not how
    // long the user is allowed to wait, and keep this outside the failure budget.
    internal static TimeSpan RecheckDelay(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(300, 30 * (1 << Math.Clamp(attempt - 1, 0, 4))));
}
