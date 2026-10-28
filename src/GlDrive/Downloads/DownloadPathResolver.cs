using System.IO;
using GlDrive.Config;

namespace GlDrive.Downloads;

public enum DownloadPathSource { Default, ExactSection, LogicalClass }
public sealed record DownloadPathResolution(string BasePath, string? LogicalClass, DownloadPathSource Source);

/// <summary>One category precedence policy for every implicit FTP download destination.</summary>
public static class DownloadPathResolver
{
    public static DownloadPathResolution Resolve(DownloadConfig config, string category, string? releaseName)
    {
        var logical = string.IsNullOrWhiteSpace(releaseName) ? null : SceneNameParser.ClassifyCategory(releaseName);
        if (!string.IsNullOrEmpty(category) && config.CategoryPaths.TryGetValue(category, out var exact)
            && !string.IsNullOrWhiteSpace(exact))
            return new(exact, logical, DownloadPathSource.ExactSection);
        if (logical != null)
        {
            if (config.CategoryPaths.TryGetValue(logical, out var canonical) && !string.IsNullOrWhiteSpace(canonical))
                return new(canonical, logical, DownloadPathSource.LogicalClass);
            var mapped = config.CategoryPaths.FirstOrDefault(p => p.Key.Equals(logical, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(p.Value));
            if (mapped.Value != null) return new(mapped.Value, logical, DownloadPathSource.LogicalClass);
        }
        return new(config.LocalPath, logical, DownloadPathSource.Default);
    }

    public static string ForScene(DownloadConfig config, string category, string releaseName)
    {
        var root = Resolve(config, category, releaseName);
        var parsed = SceneNameParser.Parse(releaseName);
        var logical = root.Source == DownloadPathSource.LogicalClass;
        var title = PathSanitizer.Sanitize(logical ? SceneNameParser.LogicalCategoryTitle(releaseName) : parsed.Title);
        var release = PathSanitizer.Sanitize(releaseName);
        if (parsed.Season != null || (logical && root.LogicalClass == "TV"))
        {
            var seasonNumber = logical ? SceneNameParser.LogicalCategorySeason(releaseName) : parsed.Season;
            var season = seasonNumber.HasValue ? $"Season {seasonNumber:D2}" : "Season 01";
            return Path.Combine(logical ? root.BasePath : Path.Combine(root.BasePath, "TV"), title, season, release);
        }
        if (parsed.Year != null || (logical && root.LogicalClass == "Movies"))
        {
            var year = parsed.Year.HasValue ? $" ({parsed.Year})" : "";
            return Path.Combine(logical ? root.BasePath : Path.Combine(root.BasePath, "Movies"), title + year, release);
        }
        return Path.Combine(root.BasePath, PathSanitizer.Sanitize(category), release);
    }

    public static string ForWishlist(DownloadConfig config, WishlistItem item, string category, string releaseName)
    {
        var root = Resolve(config, category, releaseName);
        var kind = item.Type == MediaType.Movie ? "Movies" : "TV";
        var basePath = root.Source == DownloadPathSource.LogicalClass && root.LogicalClass == kind
            ? root.BasePath : Path.Combine(root.BasePath, kind);
        var title = PathSanitizer.Sanitize(item.Title);
        var release = PathSanitizer.Sanitize(releaseName);
        if (item.Type == MediaType.Movie)
            return Path.Combine(basePath, title + (item.Year.HasValue ? $" ({item.Year})" : ""), release);
        var parsed = SceneNameParser.Parse(releaseName);
        var season = root.Source == DownloadPathSource.LogicalClass ? SceneNameParser.LogicalCategorySeason(releaseName) : parsed.Season;
        return Path.Combine(basePath, title, season.HasValue ? $"Season {season:D2}" : "Season 01", release);
    }

    public static string Flat(DownloadConfig config, string category, string releaseName, string? explicitPath = null) =>
        explicitPath ?? Path.Combine(Resolve(config, category, releaseName).BasePath, PathSanitizer.Sanitize(releaseName));
}
