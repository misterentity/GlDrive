namespace GlDrive.Downloads;

public enum SeriesMonitorMode { Legacy, Future, Missing, All, LatestSeason }
public enum SeriesEpisodeState { NotAired, Missing, OnSite, Downloading, Owned }

public sealed class SeriesEpisode
{
    public int TvMazeEpisodeId { get; set; }
    public int Season { get; set; }
    public int Number { get; set; }
    public string Name { get; set; } = "";
    public DateOnly? AirDate { get; set; }
    public DateTimeOffset? AirAtUtc { get; set; }
}

public sealed record SeriesReleaseDecision(bool Accepted, string Reason, IReadOnlyList<string> EpisodeKeys,
    bool SeasonPack = false);
public sealed record SeriesEpisodeSnapshot(int Season, int Number, string Name, DateOnly? AirDate,
    SeriesEpisodeState State, bool Monitored, string? OwnedPath, IReadOnlyList<string> SiteIds);

/// <summary>Episode identity, state and admission policy using already available local evidence.</summary>
public static class SeriesMonitorPolicy
{
    public static bool IsMonitored(WishlistItem item) => item.Type == MediaType.TvShow &&
        item.MonitorMode != SeriesMonitorMode.Legacy && Enum.IsDefined(item.MonitorMode) &&
        !item.Dead && item.Status == WishlistStatus.Watching;

    public static SeriesReleaseDecision Evaluate(WishlistItem item, string release, DateTimeOffset now)
    {
        if (item.Type != MediaType.TvShow || item.MonitorMode == SeriesMonitorMode.Legacy)
            return new(true, "legacy-monitoring", []);
        if (!Enum.IsDefined(item.MonitorMode)) return new(false, "invalid-monitor-mode", []);
        if (item.Dead || item.Status != WishlistStatus.Watching) return new(false, "series-inactive", []);
        if (!WishlistAcquisition.MatchesTitle(item, release)) return new(false, "series-title-mismatch", []);
        var inventory = Inventory(item);
        if (inventory.Count == 0) return new(false, "episode-inventory-unavailable", []);
        var keys = KeysForRelease(item, release);
        var pack = SceneNameParser.LogicalCategorySeason(release) is not null && SceneNameParser.Parse(release).Episode == null;
        if (keys.Count == 0) return new(false, "unknown-or-ambiguous-episode", [], pack);
        var covered = inventory.Where(e => keys.Contains(Key(e))).ToArray();
        if (covered.Length != keys.Count) return new(false, "episode-not-in-inventory", keys, pack);
        if (covered.Any(e => !HasAired(e, now))) return new(false, "episode-not-aired", keys, pack);
        if (covered.Any(e => !IsMonitored(item, e, now))) return new(false, "outside-monitor-mode", keys, pack);
        // An episode list alone cannot establish the end of its newest season.
        // A later season in the authoritative inventory is conservative evidence
        // that an earlier season pack has a bounded expected episode set.
        if (pack && (covered[0].Season >= inventory.Max(e => e.Season) || item.MonitorMode != SeriesMonitorMode.All))
            return new(false, "season-pack-needs-ended-season-and-all-mode", keys, true);
        if (item.BestOwned.Any(copy => Overlaps(item, copy.ReleaseName, keys)))
            return new(false, "episode-already-owned", keys, pack);
        if (item.PendingGrabs.Any(copy => Overlaps(item, copy.ReleaseName, keys)))
            return new(false, "episode-already-downloading", keys, pack);
        if (item.GrabbedReleases.Any(previous =>
                !item.BestOwned.Concat(item.PendingGrabs).Any(copy => copy.ReleaseName.Equals(previous, StringComparison.OrdinalIgnoreCase)) &&
                Overlaps(item, previous, keys)))
            return new(false, "previous-episode-grab-unverified", keys, pack);
        return new(true, pack ? "missing-season-pack" : "missing-episodes", keys, pack);
    }

    /// <summary>Maps SxxEyy, multi-episode and date aliases onto inventory identities.</summary>
    public static IReadOnlyList<string> KeysForRelease(WishlistItem item, string release)
    {
        if (!WishlistAcquisition.MatchesTitle(item, release)) return [];
        var inventory = Inventory(item);
        if (SceneNameParser.TryEpisodeDate(release, out var date))
        {
            // A date with two episodes does not identify which one this file holds.
            var sameDate = inventory.Where(e => e.AirDate == date).ToArray();
            return sameDate.Length == 1 ? [Key(sameDate[0])] : [];
        }
        var rawKeys = WishlistAcquisition.Keys(item, release).Select(key => key[(key.LastIndexOf(':') + 1)..]).ToArray();
        if (rawKeys.Length == 1 && IsSeasonKey(rawKeys[0]))
        {
            var season = int.Parse(rawKeys[0][1..]);
            return inventory.Where(e => e.Season == season).Select(Key).ToArray();
        }
        return rawKeys.Where(key => key.StartsWith('S') && key.Contains('E')).Distinct(StringComparer.Ordinal).ToArray();
    }

    public static IReadOnlyList<SeriesEpisodeSnapshot> GetEpisodes(WishlistItem item,
        IEnumerable<SearchResult> cachedCandidates, DateTimeOffset now)
    {
        var candidates = cachedCandidates.Where(c => WishlistAcquisition.MatchesTitle(item, c.ReleaseName))
            .Select(c => (Candidate: c, Keys: KeysForRelease(item, c.ReleaseName))).ToArray();
        return Inventory(item).Select(episode =>
        {
            var key = Key(episode);
            var owned = item.BestOwned.FirstOrDefault(copy => KeysForRelease(item, copy.ReleaseName).Contains(key));
            var pending = item.PendingGrabs.Any(copy => KeysForRelease(item, copy.ReleaseName).Contains(key));
            var sites = candidates.Where(c => c.Keys.Contains(key)).Select(c => c.Candidate.ServerId)
                .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).Order().ToArray();
            var state = owned != null ? SeriesEpisodeState.Owned : pending ? SeriesEpisodeState.Downloading :
                !HasAired(episode, now) ? SeriesEpisodeState.NotAired : sites.Length != 0 ? SeriesEpisodeState.OnSite : SeriesEpisodeState.Missing;
            return new SeriesEpisodeSnapshot(episode.Season, episode.Number, episode.Name, episode.AirDate,
                state, IsMonitored(item, episode, now), owned?.LocalPath, sites);
        }).ToArray();
    }

    public static bool HasAired(SeriesEpisode episode, DateTimeOffset now) =>
        episode.AirAtUtc.HasValue ? episode.AirAtUtc <= now :
        episode.AirDate.HasValue && episode.AirDate.Value < DateOnly.FromDateTime(now.UtcDateTime);

    public static bool IsMonitored(WishlistItem item, SeriesEpisode episode, DateTimeOffset now)
    {
        if (item.Dead || item.Status != WishlistStatus.Watching) return false;
        var since = item.MonitorSinceUtc ?? new DateTimeOffset(DateTime.SpecifyKind(item.AddedAt, DateTimeKind.Utc));
        return item.MonitorMode switch
        {
            SeriesMonitorMode.All or SeriesMonitorMode.Legacy => true,
            SeriesMonitorMode.Missing => HasAired(episode, now),
            SeriesMonitorMode.Future => episode.AirAtUtc.HasValue ? episode.AirAtUtc >= since :
                episode.AirDate.HasValue && episode.AirDate >= DateOnly.FromDateTime(since.UtcDateTime),
            SeriesMonitorMode.LatestSeason => episode.Season == Inventory(item)
                .Where(e => HasAired(e, now)).Select(e => e.Season).DefaultIfEmpty(-1).Max(),
            _ => false
        };
    }

    internal static string Key(SeriesEpisode episode) => $"S{episode.Season:D2}E{episode.Number:D2}";
    private static bool IsSeasonKey(string key) => key.Length > 1 && key[0] == 'S' && key[1..].All(char.IsDigit);
    private static bool Overlaps(WishlistItem item, string release, IReadOnlyList<string> keys) =>
        KeysForRelease(item, release).Any(keys.Contains);
    internal static IReadOnlyList<SeriesEpisode> Inventory(WishlistItem item) =>
        (item.SeriesInventory ?? []).Where(e => e != null && e.Season > 0 && e.Number > 0)
            .DistinctBy(e => (e.Season, e.Number)).OrderBy(e => e.Season).ThenBy(e => e.Number).ToArray();
}
