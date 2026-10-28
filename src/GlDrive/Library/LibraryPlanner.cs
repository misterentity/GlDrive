using System.IO;
using System.Text.RegularExpressions;
using GlDrive.Config;
using GlDrive.Downloads;

namespace GlDrive.Library;

public sealed record LibraryMediaIdentity(string Title, int? Year, int? Season, int? Episode, QualityProfile Quality)
{
    public string Kind => Season.HasValue ? "episode" : "movie";
}

public sealed record LibraryImportPlan(string SourcePath, string DestinationPath, string StagingRoot,
    string LibraryRoot, string ReleaseName, long ExpectedLength, DateTime ExpectedLastWriteUtc,
    LibraryMediaIdentity Identity);

/// <summary>Read-only plan for one completed video. Ambiguous packs and extras stay in staging.</summary>
public static class LibraryPlanner
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".ts" };
    private static readonly Regex MultiEpisode = new(@"S\d{1,3}E\d{1,3}(?:[ ._-]*E\d|[-+]\d)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex EpisodeToken = new(@"S\d{1,3}E\d{1,3}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex ParenthesizedYear = new(@"\((?<year>(?:19|20)\d{2})\)",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex SdLabel = new(@" - SD$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static bool IsVideoPath(string path) => VideoExtensions.Contains(Path.GetExtension(path));

    public static LibraryMediaIdentity? TryIdentity(string releaseName)
    {
        if (string.IsNullOrWhiteSpace(releaseName) || releaseName.Length > 512 || MultiEpisode.IsMatch(releaseName)
            || EpisodeToken.Matches(releaseName).Count > 1 || SceneNameParser.TryEpisodeDate(releaseName, out _)) return null;
        // The shared scene parser expects bare year tokens; Plex names use (year).
        var parsed = SceneNameParser.Parse(ParenthesizedYear.Replace(releaseName, " ${year} "));
        if (string.IsNullOrWhiteSpace(parsed.Title) || parsed.IsSeasonPack) return null;
        if (parsed.Season.HasValue != parsed.Episode.HasValue) return null;
        if (!parsed.Season.HasValue && !parsed.Year.HasValue) return null;
        var title = parsed.Title.Trim().TrimEnd('(', ' ', '-');
        if (title.Length == 0 || PathSanitizer.Sanitize(title) == "_") return null;
        var quality = parsed.Quality == QualityProfile.Any && SdLabel.IsMatch(releaseName) ? QualityProfile.SD : parsed.Quality;
        return new(title, parsed.Year, parsed.Season, parsed.Episode, quality);
    }

    public static LibraryImportPlan Plan(LibraryConfig config, string sourcePath, string releaseName)
    {
        var staging = NormalizeRoot(config.StagingRoot);
        var source = NormalizePath(sourcePath);
        if (!IsWithin(source, staging) || !IsVideoPath(source) || Path.GetFileName(source).StartsWith('.')
            || Path.GetFileName(source).Contains("-MISSING-", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose a completed video file inside the staging root.");
        if (Path.GetRelativePath(staging, source).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(LibraryIndex.ExcludedName))
            throw new InvalidOperationException("Samples, extras and metadata remain in staging.");
        var identity = TryIdentity(releaseName) ?? throw new InvalidOperationException("A movie with a year or one SxxExx episode is required; packs and ambiguous names remain staged.");
        if (LibraryIndex.IdentityForFile(source) is { } fileIdentity && !LibraryIndex.SameIdentity(fileIdentity, identity))
            throw new InvalidOperationException("The staged file identity does not match the requested release.");
        var root = NormalizeRoot(identity.Kind == "movie" ? config.MovieRoot : config.TvRoot);
        foreach (var configured in new[] { config.MovieRoot, config.TvRoot }.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            var library = NormalizeRoot(configured);
            if (staging.Equals(library, StringComparison.OrdinalIgnoreCase) || IsWithin(staging, library) || IsWithin(library, staging))
                throw new InvalidOperationException("Staging and library roots must be separate, non-overlapping folders.");
        }
        RejectReparseAncestors(source);
        RejectReparseAncestors(root);
        var file = new FileInfo(source);
        if (!file.Exists || file.Attributes.HasFlag(FileAttributes.Directory) || file.Length <= 0)
            throw new InvalidOperationException("The staged video must be an existing, nonempty regular file.");
        var title = PathSanitizer.Sanitize(identity.Title);
        var folder = identity.Year is { } year ? $"{title} ({year})" : title;
        var quality = identity.Quality switch
        {
            QualityProfile.SD => " - SD", QualityProfile.Q720p => " - 720p",
            QualityProfile.Q1080p => " - 1080p", QualityProfile.Q2160p => " - 2160p", _ => ""
        };
        var stem = identity.Kind == "movie" ? folder
            : $"{folder} - S{identity.Season:00}E{identity.Episode:00}";
        // Preserve positive low-source evidence from the original release through
        // normalization so a later index refresh cannot call this copy cutoff-owned.
        if (identity.Kind == "movie")
            stem += ReleaseTraits.Parse(releaseName).Source switch
            {
                ReleaseSource.Cam => " - CAM", ReleaseSource.Telesync => " - TS",
                ReleaseSource.Telecine => " - TC", _ => ""
            };
        var destination = identity.Kind == "movie"
            ? Path.Combine(root, folder, stem + quality + Path.GetExtension(source).ToLowerInvariant())
            : Path.Combine(root, folder, $"Season {identity.Season:00}", stem + quality + Path.GetExtension(source).ToLowerInvariant());
        destination = NormalizePath(destination);
        if (!IsWithin(destination, root)) throw new InvalidOperationException("The import path must remain within its library root.");
        RejectReparseAncestors(destination);
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new IOException("The import destination already exists; it will never be replaced.");
        return new(source, destination, staging, root, releaseName, file.Length, file.LastWriteTimeUtc, identity);
    }

    internal static string NormalizeRoot(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(NormalizePath(path));
        if (full.Equals(Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose a dedicated folder, not a drive root.");
        return full;
    }

    internal static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal))
            throw new InvalidOperationException("Library imports require an absolute local drive path.");
        var full = Path.GetFullPath(path);
        if (full[(Path.GetPathRoot(full)?.Length ?? 0)..].Contains(':'))
            throw new InvalidOperationException("Alternate data streams are not supported for library imports.");
        return full;
    }

    internal static bool IsWithin(string path, string root) =>
        path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    internal static void RejectReparseAncestors(string path)
    {
        for (var current = path; current != null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Reparse points are not supported in import paths.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
