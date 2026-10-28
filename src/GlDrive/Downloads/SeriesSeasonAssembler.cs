using GlDrive.Config;

namespace GlDrive.Downloads;

public sealed record SeriesSeasonPlan(int Season, bool Ready, IReadOnlyList<SearchResult> Releases,
    IReadOnlyList<string> MissingEpisodeKeys, string Reason);

/// <summary>Produces a reviewable plan from cached entries, without listing or enqueueing anything.</summary>
public static class SeriesSeasonAssembler
{
    public static SeriesSeasonPlan Plan(WishlistItem item, int season, IEnumerable<SearchResult> cachedCandidates,
        DownloadConfig config, DateTimeOffset now, Func<string, double>? sourceBytesPerSecond = null)
    {
        if (!SeriesMonitorPolicy.IsMonitored(item)) return new(season, false, [], [], "series-is-not-monitored");
        var inventory = SeriesMonitorPolicy.Inventory(item).Where(e => e.Season == season).ToArray();
        if (inventory.Length == 0) return new(season, false, [], [], "season-inventory-unavailable");
        var wanted = inventory.Where(e => SeriesMonitorPolicy.HasAired(e, now) && SeriesMonitorPolicy.IsMonitored(item, e, now))
            .Select(SeriesMonitorPolicy.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var copy in item.BestOwned.Concat(item.PendingGrabs))
            wanted.ExceptWith(SeriesMonitorPolicy.KeysForRelease(item, copy.ReleaseName));
        if (wanted.Count == 0) return new(season, true, [], [], "no-aired-episode-gaps");
        var profile = config.ScoredQualityEnabled ? WishlistAcquisition.ResolveProfile(item, config) : ScoredQualityProfile.Legacy(item.Quality);
        if (profile == null) return new(season, false, [], wanted.Order().ToArray(), "quality-profile-unavailable");
        var candidates = cachedCandidates.Take(100000)
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.ServerId) &&
                RacePullPolicy.SafeReleasePath(candidate.ReleaseName, candidate.RemotePath)).Select(candidate =>
        {
            var decision = SeriesMonitorPolicy.Evaluate(item, candidate.ReleaseName, now);
            var score = ReleaseScorer.Score(candidate.ReleaseName, profile, candidate.Size > 0 ? candidate.Size : null);
            var speed = sourceBytesPerSecond?.Invoke(candidate.ServerId) ?? 0;
            return (Candidate: candidate, Decision: decision, Score: config.ScoredQualityEnabled ? score.Score : 0,
                QualityAccepted: score.Accepted, Speed: double.IsFinite(speed) && speed > 0 ? speed : 0);
        }).Where(c => c.Decision.Accepted && c.QualityAccepted && c.Decision.EpisodeKeys.Count != 0 &&
            c.Decision.EpisodeKeys.All(wanted.Contains))
            .OrderByDescending(c => c.Score).ThenByDescending(c => c.Speed)
            .ThenBy(c => c.Candidate.ReleaseName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Candidate.ServerId, StringComparer.Ordinal).ToArray();
        var pack = candidates.FirstOrDefault(c => c.Decision.SeasonPack && wanted.SetEquals(c.Decision.EpisodeKeys));
        if (pack.Candidate != null) return new(season, true, [Copy(pack.Candidate)], [], "one-missing-season-pack");

        // Prefer ranked candidates, but do not let a high-ranked overlapping
        // multi-episode copy hide an available exact cover of the whole season.
        // Equivalent coverage needs only its best-ranked source/copy.
        var options = candidates.Where(c => !c.Decision.SeasonPack)
            .DistinctBy(c => string.Join(",", c.Decision.EpisodeKeys.Order(StringComparer.Ordinal))).ToArray();
        var searchRemaining = new HashSet<string>(wanted, StringComparer.Ordinal);
        var selected = new List<SearchResult>();
        var visits = 0;
        bool FindExactCover()
        {
            if (++visits > 10000 || selected.Count > 512) return false;
            if (searchRemaining.Count == 0) return true;
            var feasible = options.Where(c => c.Decision.EpisodeKeys.All(searchRemaining.Contains)).ToArray();
            var next = searchRemaining.OrderBy(key => feasible.Count(c => c.Decision.EpisodeKeys.Contains(key)))
                .ThenBy(key => key, StringComparer.Ordinal).First();
            foreach (var option in feasible.Where(c => c.Decision.EpisodeKeys.Contains(next)))
            {
                selected.Add(option.Candidate);
                searchRemaining.ExceptWith(option.Decision.EpisodeKeys);
                if (FindExactCover()) return true;
                searchRemaining.UnionWith(option.Decision.EpisodeKeys);
                selected.RemoveAt(selected.Count - 1);
                if (visits > 10000) break;
            }
            return false;
        }
        if (wanted.Count <= 512 && FindExactCover())
            return new(season, true, selected.Select(Copy).ToArray(), [], "all-aired-gaps-available");
        if (wanted.Count > 512 || visits > 10000)
            return new(season, false, [], wanted.Order().ToArray(), "assembly-search-budget-exhausted");

        var remaining = new HashSet<string>(wanted, StringComparer.Ordinal);
        var releases = new List<SearchResult>();
        foreach (var episode in wanted.Order(StringComparer.Ordinal))
        {
            if (!remaining.Contains(episode)) continue;
            var choice = candidates.FirstOrDefault(c => !c.Decision.SeasonPack && c.Decision.EpisodeKeys.Contains(episode) &&
                c.Decision.EpisodeKeys.All(remaining.Contains));
            if (choice.Candidate == null) continue;
            releases.Add(Copy(choice.Candidate));
            remaining.ExceptWith(choice.Decision.EpisodeKeys);
        }
        return new(season, remaining.Count == 0, releases, remaining.Order().ToArray(),
            remaining.Count == 0 ? "all-aired-gaps-available" : "some-aired-gaps-unavailable");
    }

    private static SearchResult Copy(SearchResult entry) => new()
    {
        ReleaseName = entry.ReleaseName, Category = entry.Category, RemotePath = entry.RemotePath,
        Size = entry.Size, Modified = entry.Modified, ServerId = entry.ServerId, ServerName = entry.ServerName,
        Files = new List<string>(entry.Files ?? [])
    };
}
