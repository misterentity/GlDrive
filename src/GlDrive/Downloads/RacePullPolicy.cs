using GlDrive.Spread;

namespace GlDrive.Downloads;

public sealed record RacePullCandidate(string ServerId, string ReleaseName, string RemotePath,
    string Category, IReadOnlyList<string> WishlistItemIds, long? SizeBytes);

/// <summary>Uses the completed race's existing inventory and resolved paths; never probes a site.</summary>
public static class RacePullPolicy
{
    public static IReadOnlyList<RacePullCandidate> Find(RaceDetail race, IEnumerable<WishlistItem> followed)
    {
        if (!IrcAnnounceListener.IsPlausibleReleaseName(race.Release)) return [];
        var items = followed.Where(item => item.Type == MediaType.TvShow && !item.Dead &&
                item.Status == WishlistStatus.Watching && WishlistAcquisition.MatchesTitle(item, race.Release) &&
                WishlistAcquisition.Keys(item, race.Release).Count > 0)
            .Select(item => item.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (items.Length == 0) return [];
        var size = InventoryBytes(race.Files);
        return race.Sites.Where(site => !site.IsSource && site.IsComplete &&
                site.State == nameof(DestState.Complete) && !string.IsNullOrWhiteSpace(site.ServerId) &&
                SafeReleasePath(race.Release, site.Path))
            .DistinctBy(site => (site.ServerId, site.Path))
            .OrderBy(site => site.ServerId, StringComparer.Ordinal)
            .Select(site => new RacePullCandidate(site.ServerId, race.Release, site.Path!,
                string.IsNullOrWhiteSpace(site.RemoteSection) ? race.Section : site.RemoteSection,
                Array.AsReadOnly(items), size)).ToArray();
    }

    internal static bool SafeReleasePath(string release, string? path) =>
        !string.IsNullOrWhiteSpace(release) && release.Length <= 1024 && !release.StartsWith('.') &&
        !release.StartsWith("-MISSING-", StringComparison.OrdinalIgnoreCase) &&
        !release.Any(character => char.IsControl(character) || character is '/' or '\\') &&
        !string.IsNullOrWhiteSpace(path) && path.Length <= 4096 && path.StartsWith('/') &&
        !path.Any(character => char.IsControl(character) || character == '\\') &&
        !path.Split('/').Any(segment => segment is "." or "..") &&
        path.EndsWith('/' + release, StringComparison.Ordinal);

    private static long? InventoryBytes(IEnumerable<RaceFileDetail> files)
    {
        long size = 0;
        foreach (var file in files)
        {
            var leaf = file.Name.Split('/')[^1];
            if (file.Size <= 0 || ReleaseSourceState.IsSiteMetadata(leaf, file.Size)) continue;
            if (file.Size > long.MaxValue - size) return null;
            size += file.Size;
        }
        return size > 0 ? size : null;
    }
}
