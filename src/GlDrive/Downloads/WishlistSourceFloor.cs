using System.IO;
using GlDrive.Config;
using GlDrive.Library;

namespace GlDrive.Downloads;

/// <summary>A wishlist admission floor. Unknown source names are not evidence of a bad copy.</summary>
public static class WishlistSourceFloor
{
    public static bool Allows(WishlistItem item, string release, DownloadConfig config)
    {
        if (item.Type != MediaType.Movie || !IsLowQuality(release)) return true;
        if (item.AllowLowQualitySources is { } allow) return allow;
        // Migrated legacy profiles allow all sources for compatibility, but that is not
        // an explicit choice to acquire CAMs. A configured custom profile is such a choice.
        if (!string.IsNullOrEmpty(item.ProfileId) && config.Profiles.Any(p =>
                p.Id.Equals(item.ProfileId, StringComparison.OrdinalIgnoreCase) && p.AllowLowQualitySources)) return true;
        return !config.BlockLowQualityWishlistSources;
    }

    public static bool IsLowQuality(string? release) => !string.IsNullOrWhiteSpace(release) && release.Length <= 1024 &&
        ReleaseTraits.Parse(release).LowQualitySource;

    public static bool IsLowQualityLibrary(LibraryIndexEntry entry)
        => entry.Identity.Kind == "movie" && LibraryTraits(entry).LowQualitySource;

    public static ReleaseTraits LibraryTraits(LibraryIndexEntry entry)
    {
        // Inspect only the file stem and its immediate release folder. A .ts container,
        // group named CAM, or unrelated ancestor folder must not become source evidence.
        var stem = Path.GetFileNameWithoutExtension(entry.Path);
        var traits = ReleaseTraits.Parse(stem);
        if (traits.LowQualitySource) return traits;
        var folder = Path.GetFileName(Path.GetDirectoryName(entry.Path));
        var identity = LibraryPlanner.TryIdentity(folder ?? "");
        if (identity != null && LibraryIndex.SameIdentity(identity, entry.Identity))
        {
            var folderTraits = ReleaseTraits.Parse(folder!);
            if (folderTraits.LowQualitySource || traits.Source == ReleaseSource.Unknown) return folderTraits;
        }
        return traits;
    }
}
