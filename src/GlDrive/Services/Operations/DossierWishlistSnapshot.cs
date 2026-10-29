using GlDrive.Downloads;

namespace GlDrive.Services.Operations;

internal static class DossierWishlistSnapshot
{
    internal static WishlistItem? Copy(WishlistItem item, ref int remainingChars)
    {
        if (item.GrabbedReleases is not { Count: <= 128 } || item.BestOwned is not { Count: <= 128 } ||
            item.PendingGrabs is not { Count: <= 128 } || item.SeriesInventory is not { Count: <= 512 }) return null;
        var text = new List<string?> { item.Id, item.Title, item.ImdbId, item.ProfileId };
        text.AddRange(item.GrabbedReleases);
        foreach (var copy in item.BestOwned.Concat(item.PendingGrabs))
        {
            if (copy == null) return null;
            text.Add(copy.Key); text.Add(copy.ReleaseName); text.Add(copy.DownloadId); text.Add(copy.ServerId);
        }
        if (item.GrabbedReleases.Any(value => value == null) || text.Any(value => value?.Length > 1024)) return null;
        var size = text.Sum(value => value?.Length ?? 0);
        if (size > 65536 || size > remainingChars || item.SeriesInventory.Any(e => e == null)) return null;
        remainingChars -= size;
        return new WishlistItem
        {
            Id = item.Id, Title = item.Title, Type = item.Type, Year = item.Year, ImdbId = item.ImdbId,
            TvMazeId = item.TvMazeId, Quality = item.Quality, ProfileId = item.ProfileId,
            AllowLowQualitySources = item.AllowLowQualitySources, MonitorMode = item.MonitorMode,
            MonitorSinceUtc = item.MonitorSinceUtc, SeriesInventoryUpdatedAtUtc = item.SeriesInventoryUpdatedAtUtc,
            SeriesInventoryLastAttemptAtUtc = item.SeriesInventoryLastAttemptAtUtc,
            Status = item.Status, Dead = item.Dead, AddedAt = item.AddedAt,
            GrabbedReleases = item.GrabbedReleases.ToList(),
            BestOwned = item.BestOwned.Select(CopyClaim).ToList(), PendingGrabs = item.PendingGrabs.Select(CopyClaim).ToList(),
            SeriesInventory = item.SeriesInventory.Select(e => new SeriesEpisode
            { TvMazeEpisodeId = e.TvMazeEpisodeId, Season = e.Season, Number = e.Number, AirDate = e.AirDate, AirAtUtc = e.AirAtUtc }).ToList()
        };
    }

    private static WishlistCopy CopyClaim(WishlistCopy row) => new()
    {
        Key = row.Key, ReleaseName = row.ReleaseName, DownloadId = row.DownloadId,
        ServerId = row.ServerId, SizeBytes = row.SizeBytes, CompletedAt = row.CompletedAt
    };
}
