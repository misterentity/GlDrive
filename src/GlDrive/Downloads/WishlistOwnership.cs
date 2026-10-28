using System.IO;
using GlDrive.Config;
using GlDrive.Library;

namespace GlDrive.Downloads;

/// <summary>Positive, local ownership evidence. Unknown catalogs never close wanted entries.</summary>
public static class WishlistOwnership
{
    internal static bool SameIdentity(WishlistItem left, WishlistItem right)
    {
        if (left.Type != right.Type) return false;
        var pairs = new (string? Left, string? Right)[]
        {
            (left.PlexWatchlistId, right.PlexWatchlistId), (left.ImdbId, right.ImdbId),
            (left.TmdbId?.ToString(), right.TmdbId?.ToString()), (left.TvdbId?.ToString(), right.TvdbId?.ToString()),
            (left.TvMazeId?.ToString(), right.TvMazeId?.ToString())
        };
        var comparable = pairs.Where(p => !string.IsNullOrEmpty(p.Left) && !string.IsNullOrEmpty(p.Right)).ToArray();
        if (comparable.Any(p => !string.Equals(p.Left, p.Right, StringComparison.OrdinalIgnoreCase))) return false;
        if (comparable.Length > 0) return true;
        if (Normalize(left.Title).Length == 0 || Normalize(left.Title) != Normalize(right.Title)) return false;
        if (left.Year.HasValue && right.Year.HasValue) return left.Year == right.Year;
        // A manual title-only request already expresses the user's intent. Prefer
        // skipping an ambiguous import to creating a second request at another quality.
        // Unknown years still cannot merge two independent provider imports.
        return left.Source != GlDrive.Plex.PlexWatchlistSync.Source || right.Source != GlDrive.Plex.PlexWatchlistSync.Source;
    }

    internal sealed record CompletedDownload(string Id, string? WishlistItemId, string ReleaseName,
        long TotalBytes, ParsedRelease Parsed, bool LowQuality);

    internal static IReadOnlyList<CompletedDownload> PrepareDownloads(IEnumerable<DownloadItem> downloads,
        Func<string, ParsedRelease>? parse = null) => downloads.Where(d => d.Status == DownloadStatus.Completed)
        .Select(d => new CompletedDownload(d.Id, d.WishlistItemId, d.ReleaseName, d.TotalBytes,
            (parse ?? SceneNameParser.Parse)(d.ReleaseName), WishlistSourceFloor.IsLowQuality(d.ReleaseName))).ToArray();

    public static bool IsOwned(WishlistItem item, DownloadConfig config, LibraryIndexSnapshot index,
        IEnumerable<DownloadItem> downloads, DateTimeOffset now)
    {
        if (!CanComplete(item, now)) return false;
        return IsOwnedPrepared(item, config, index, PrepareDownloads(downloads), now);
    }

    internal static bool IsOwnedPrepared(WishlistItem item, DownloadConfig config, LibraryIndexSnapshot index,
        IReadOnlyList<CompletedDownload> downloads, DateTimeOffset now)
    {
        if (!CanComplete(item, now)) return false;
        var matching = downloads.Where(d =>
            !(item.Type == MediaType.TvShow && item.Year.HasValue && d.Parsed.Year is { } year && year != item.Year) &&
            WishlistAcquisition.MatchesTitle(item, d.ReleaseName, d.Parsed) &&
            (item.Type != MediaType.Movie || item.Year.HasValue && item.Year == d.Parsed.Year ||
                !item.Year.HasValue && d.WishlistItemId == item.Id)).ToArray();
        var completed = matching.Where(d => MeetsReleaseCutoff(item, d.ReleaseName, config,
            d.TotalBytes, d.Parsed, d.LowQuality)).ToArray();
        if (item.Type == MediaType.Movie)
            return completed.Length > 0 || index.Entries.Any(e => e.Identity.Kind == "movie" && e.VerifiedOnDisk &&
                MatchesMovie(item, e) && MeetsLibraryCutoff(item, e, config) &&
                (!matching.Any(d => d.LowQuality) || WishlistSourceFloor.LibraryTraits(e).Source != ReleaseSource.Unknown));

        var inventory = SeriesMonitorPolicy.Inventory(item);
        var owned = completed.SelectMany(d => SeriesMonitorPolicy.KeysForRelease(item, d.ReleaseName, d.Parsed)).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in index.Entries.Where(e => e.VerifiedOnDisk && e.Identity.Kind == "episode" &&
                     Normalize(e.Identity.Title) == Normalize(item.Title) &&
                     (!item.Year.HasValue || !e.Identity.Year.HasValue || item.Year == e.Identity.Year) && MeetsLibraryCutoff(item, e, config)))
            owned.Add($"S{entry.Identity.Season:D2}E{entry.Identity.Episode:D2}");
        return inventory.All(e => owned.Contains(SeriesMonitorPolicy.Key(e)));
    }

    private static bool CanComplete(WishlistItem item, DateTimeOffset now)
    {
        if (item.Type == MediaType.Movie) return true;
        if (!item.WatchlistSeriesEnded || item.SeriesInventoryUpdatedAtUtc == null) return false;
        var inventory = SeriesMonitorPolicy.Inventory(item);
        return inventory.Count > 0 && inventory.All(e => SeriesMonitorPolicy.HasAired(e, now));
    }

    public static bool MeetsReleaseCutoff(WishlistItem item, string release, DownloadConfig config, long? sizeBytes = null)
        => MeetsReleaseCutoff(item, release, config, sizeBytes, SceneNameParser.Parse(release), WishlistSourceFloor.IsLowQuality(release));

    private static bool MeetsReleaseCutoff(WishlistItem item, string release, DownloadConfig config, long? sizeBytes,
        ParsedRelease parsed, bool lowQuality)
    {
        // Explicit permission to grab a CAM is not permission to stop looking for a good copy.
        if (item.Type == MediaType.Movie && lowQuality) return false;
        var profile = WishlistAcquisition.ResolveProfile(item, config);
        if (profile == null) return false;
        var resolution = parsed.Quality;
        if (IsBuiltInLegacy(profile, config))
            return MeetsLegacyResolution(profile, resolution);
        var score = ReleaseScorer.Score(release, profile, sizeBytes);
        return score.Accepted && score.MeetsCutoff;
    }

    public static bool MeetsLibraryCutoff(WishlistItem item, LibraryIndexEntry entry, DownloadConfig config)
    {
        if (item.Type == MediaType.Movie && WishlistSourceFloor.IsLowQualityLibrary(entry)) return false;
        var profile = WishlistAcquisition.ResolveProfile(item, config);
        if (profile == null) return false;
        if (IsBuiltInLegacy(profile, config))
            return MeetsLegacyResolution(profile, entry.Identity.Quality);
        // A resolution label is not proof of a custom source/format score.
        return MeetsReleaseCutoff(item, Path.GetFileNameWithoutExtension(entry.Path), config);
    }

    private static bool MatchesMovie(WishlistItem item, LibraryIndexEntry entry)
    {
        var expected = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        { ["imdb"] = item.ImdbId, ["tmdb"] = item.TmdbId?.ToString() };
        var matched = false;
        foreach (var guid in entry.Guids)
        {
            var split = guid.IndexOf("://", StringComparison.Ordinal);
            if (split < 1 || !expected.TryGetValue(guid[..split], out var wanted) || string.IsNullOrEmpty(wanted)) continue;
            if (!guid[(split + 3)..].Equals(wanted, StringComparison.OrdinalIgnoreCase)) return false;
            matched = true;
        }
        if (matched) return true;
        return item.Year.HasValue && item.Year == entry.Identity.Year && Normalize(item.Title) == Normalize(entry.Identity.Title);
    }

    private static bool IsBuiltInLegacy(ScoredQualityProfile profile, DownloadConfig config) =>
        !config.Profiles.Any(p => p.Id.Equals(profile.Id, StringComparison.OrdinalIgnoreCase)) &&
        Enum.GetValues<QualityProfile>().Any(q => ScoredQualityProfile.Legacy(q).Id.Equals(profile.Id, StringComparison.OrdinalIgnoreCase));

    private static bool MeetsLegacyResolution(ScoredQualityProfile profile, QualityProfile resolution) =>
        profile.AllowedResolutions.Count == 0 || resolution != QualityProfile.Any && resolution >= profile.AllowedResolutions.Min();

    private static string Normalize(string text) => new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
