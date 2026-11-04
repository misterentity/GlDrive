using GlDrive.Config;
using GlDrive.Library;

namespace GlDrive.Downloads;

public sealed record CollectionGapFilm(int TmdbId, string Title, int? Year, string Status);

/// <summary>
/// Read-only collection completeness: any good movie copy counts, independent of the
/// default quality for a new wish. Low source copies remain outside C4's completion floor.
/// A film in any linked Plex server's catalog is owned here even without a local file — the
/// servers live on other machines. Missing is proven only by a complete inventory (the disk
/// scan, or every Plex server read with no disk-scan issues); absent evidence is unknown.
/// </summary>
public static class CollectionGapFinder
{
    public static IReadOnlyList<CollectionGapFilm> Compare(TmdbCollectionData collection, LibraryIndexSnapshot index,
        IEnumerable<DownloadItem> downloads, IEnumerable<WishlistItem> wishlist, DownloadConfig config)
    {
        var downloadRows = downloads.ToArray();
        var wanted = wishlist.ToArray();
        var completed = WishlistOwnership.PrepareDownloads(downloadRows);
        var pending = downloadRows.Where(d => d.Status is DownloadStatus.Queued or DownloadStatus.Downloading or
            DownloadStatus.Extracting or DownloadStatus.Paused).Select(d => (Item: d, Parsed: SceneNameParser.Parse(d.ReleaseName))).ToArray();
        var plexMovies = index.Entries.Where(e => e.Identity.Kind == "movie" && e.PlexRatingKey != null &&
            !WishlistSourceFloor.IsLowQualityLibrary(e)).ToArray();
        var absenceProven = index.Complete || index.PlexComplete && index.ScanIssues == 0;
        return collection.Films.Select(film =>
        {
            var candidate = new WishlistItem { Type = MediaType.Movie, Title = film.Title, Year = film.Year,
                TmdbId = film.TmdbId, Quality = QualityProfile.Any };
            var identifiedWish = wanted.FirstOrDefault(w => w.Type == MediaType.Movie && w.TmdbId == film.TmdbId);
            if (identifiedWish != null) candidate.Id = identifiedWish.Id;
            var status = WishlistOwnership.IsOwnedPrepared(candidate, config, index, completed, DateTimeOffset.UtcNow)
                    || plexMovies.Any(e => WishlistOwnership.MatchesMovie(candidate, e)) ? "owned"
                : pending.Any(d => MatchesPending(candidate, d.Item, d.Parsed, wanted, config)) ? "downloading"
                : wanted.Any(w => MatchesWishlist(film, w)) ? "wanted"
                : absenceProven && film.Year.HasValue ? "missing" : "unknown";
            return new CollectionGapFilm(film.TmdbId, film.Title, film.Year, status);
        }).ToArray();
    }

    internal static bool MatchesWishlist(TmdbCollectionFilm film, WishlistItem item) => WishlistOwnership.SameIdentity(
        new WishlistItem { Type = MediaType.Movie, TmdbId = film.TmdbId, Title = film.Title, Year = film.Year, Quality = QualityProfile.Any }, item);

    private static bool MatchesPending(WishlistItem candidate, DownloadItem download, ParsedRelease parsed,
        IReadOnlyList<WishlistItem> wanted, DownloadConfig config) =>
        WishlistAcquisition.MatchesTitle(candidate, download.ReleaseName, parsed) &&
        (candidate.Year.HasValue ? candidate.Year == parsed.Year : wanted.Any(w => w.Id == download.WishlistItemId &&
            w.TmdbId == candidate.TmdbId && w.Type == MediaType.Movie)) &&
        WishlistOwnership.MeetsReleaseCutoff(candidate, download.ReleaseName, config, download.TotalBytes);
}
