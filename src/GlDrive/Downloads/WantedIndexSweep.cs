using GlDrive.Config;
using GlDrive.Spread;

namespace GlDrive.Downloads;

public sealed record WantedIndexCandidate(string WishlistItemId, string ReleaseName, string RemotePath,
    string Category, long? SizeBytes, int Score, string Reason, DateTime? IndexBuiltAtUtc)
{
    // Parent-directory LISTs do not inspect release contents. Consumers may join
    // fresh trusted completion facts, but must never promote index presence alone.
    public bool Complete => false;
}

/// <summary>Bounded local offers from the cache the ordinary indexer already builds.</summary>
public static class WantedIndexSweep
{
    public static IReadOnlyList<WantedIndexCandidate> Find(SearchIndexSnapshot snapshot, WishlistStore wishlist,
        DownloadConfig config, int maxCandidates = 100)
    {
        var limit = Math.Clamp(maxCandidates, 0, 1000);
        if (limit == 0 || snapshot.BuiltAtUtc == null) return [];
        var wanted = wishlist.Items.Where(item => !item.Dead && item.Status != WishlistStatus.Paused)
            .OrderBy(item => item.AddedAt).ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();
        var groups = new Dictionary<string, Dictionary<string, List<WantedIndexCandidate>>>(StringComparer.Ordinal);
        foreach (var entry in snapshot.Entries)
        {
            if (!SafeEntry(entry)) continue;
            foreach (var item in wanted)
            {
                WishlistQualityDecision? decision = null;
                if (config.ScoredQualityEnabled || item.MonitorMode != SeriesMonitorMode.Legacy)
                {
                    // A directory's LIST size is not the release's total bytes.
                    decision = wishlist.EvaluateScoredRelease(item.Id, entry.ReleaseName, config);
                    if (!decision.Accepted) continue;
                }
                else
                {
                    if (item.Status != WishlistStatus.Watching || item.GrabbedReleases.Contains(entry.ReleaseName,
                            StringComparer.OrdinalIgnoreCase)) continue;
                    var matches = item.Type == MediaType.Movie
                        ? SceneNameParser.MatchesMovie(entry.ReleaseName, item.Title, item.Year, item.Quality)
                        : SceneNameParser.MatchesTvEpisode(entry.ReleaseName, item.Title, item.Quality);
                    if (!matches) continue;
                }
                var keys = decision?.Keys ?? WishlistAcquisition.Keys(item, entry.ReleaseName);
                if (keys.Count == 0) continue;
                var key = string.Join('|', keys.Order(StringComparer.Ordinal));
                var candidate = new WantedIndexCandidate(item.Id, entry.ReleaseName, entry.RemotePath,
                    entry.Category, null, decision?.Score ?? 0,
                    (decision?.Reason ?? "matches legacy quality") + "; cached directory, completeness unknown",
                    snapshot.BuiltAtUtc);
                if (!groups.TryGetValue(item.Id, out var variants)) groups[item.Id] = variants = new(StringComparer.Ordinal);
                if (!variants.TryGetValue(key, out var alternatives)) variants[key] = alternatives = [];
                var sameRelease = alternatives.FindIndex(previous => string.Equals(previous.ReleaseName,
                    candidate.ReleaseName, StringComparison.OrdinalIgnoreCase));
                if (sameRelease >= 0)
                {
                    if (Compare(candidate, alternatives[sameRelease]) >= 0) continue;
                    alternatives.RemoveAt(sameRelease);
                }
                alternatives.Add(candidate);
                alternatives.Sort(Compare);
                // Retain runner-ups so joining completion evidence later can
                // choose a complete release over a better but unfinished variant.
                if (alternatives.Count > 8) alternatives.RemoveAt(alternatives.Count - 1);
                // Retain only this item's best bounded offers while scanning all
                // entries, so a later, better variant can still replace the first.
                if (variants.Values.Sum(values => values.Count) > limit)
                {
                    var worst = variants.OrderBy(pair => pair.Value[^1], CandidateComparer).Last();
                    worst.Value.RemoveAt(worst.Value.Count - 1);
                    if (worst.Value.Count == 0) variants.Remove(worst.Key);
                }
            }
        }
        var queues = wanted.Where(item => groups.ContainsKey(item.Id))
            .Select(item => new Queue<WantedIndexCandidate>(groups[item.Id].Values.SelectMany(values => values)
                .Order(CandidateComparer))).ToArray();
        var result = new List<WantedIndexCandidate>(limit);
        // Round-robin by wanted item prevents one series' archive from filling
        // the entire offer cap before another wanted title is considered.
        while (result.Count < limit)
        {
            var added = false;
            foreach (var queue in queues)
            {
                if (queue.Count == 0) continue;
                result.Add(queue.Dequeue());
                added = true;
                if (result.Count == limit) break;
            }
            if (!added) break;
        }
        return result.AsReadOnly();
    }

    private static readonly IComparer<WantedIndexCandidate> CandidateComparer =
        Comparer<WantedIndexCandidate>.Create(Compare);

    private static int Compare(WantedIndexCandidate left, WantedIndexCandidate right)
    {
        var score = right.Score.CompareTo(left.Score);
        if (score != 0) return score;
        var release = StringComparer.OrdinalIgnoreCase.Compare(left.ReleaseName, right.ReleaseName);
        return release != 0 ? release : StringComparer.Ordinal.Compare(left.RemotePath, right.RemotePath);
    }

    private static bool SafeEntry(SearchIndexEntry entry) => entry.ReleaseName.Length <= 1024
        && !entry.ReleaseName.StartsWith('.') && !entry.ReleaseName.StartsWith("-MISSING-", StringComparison.OrdinalIgnoreCase)
        && !entry.ReleaseName.Any(c => char.IsControl(c) || c is '/' or '\\')
        && IrcAnnounceListener.IsPlausibleReleaseName(entry.ReleaseName)
        && FtpSearchService.IsReleaseDirectory(entry.ReleaseName)
        && !FtpSearchService.SkipDirectory(entry.ReleaseName)
        && entry.RemotePath.StartsWith('/') && !entry.RemotePath.Any(c => char.IsControl(c) || c == '\\')
        && !entry.RemotePath.Split('/').Any(segment => segment is "." or "..")
        && entry.RemotePath.EndsWith('/' + entry.ReleaseName, StringComparison.Ordinal);
}
