namespace GlDrive.AiAgent;

/// <summary>
/// Deterministically correlates matched IRC announces with race outcomes so the AiAgent can
/// learn which remote folder a given (IRC section, parsed type, quality) actually routes to.
/// Pure LINQ, no I/O — fed pre-read enumerables by <see cref="LogDigester"/>.
/// </summary>
public sealed class SectionFolderDigester
{
    public SectionFolderDigest Build(IEnumerable<MatchedAnnounceEvent> announces, IEnumerable<RaceOutcomeEvent> races)
    {
        var d = new SectionFolderDigest();

        // Materialize once: both inputs are forward-only enumerables and we scan races per group.
        var raceList = races as IReadOnlyList<RaceOutcomeEvent> ?? races.ToList();
        var announceList = announces as IReadOnlyList<MatchedAnnounceEvent> ?? announces.ToList();
        if (announceList.Count == 0) return d; // empty announces → nothing to learn, empty digest

        foreach (var g in announceList.GroupBy(a => (a.ServerId, a.Section, a.ParsedType, a.Quality)))
        {
            // Races for this IRC section (case-insensitive) tell us the destination folder actually used.
            var sectionRaces = raceList
                .Where(r => string.Equals(r.Section, g.Key.Section, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // ObservedRemoteSection = most-frequent non-empty resolved destination among those races.
            var observed = sectionRaces
                .Select(r => r.ResolvedRemoteSection)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .GroupBy(s => s!, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(rg => rg.Count())
                .ThenBy(rg => rg.Key, StringComparer.OrdinalIgnoreCase) // deterministic tie-break
                .Select(rg => rg.First()!)
                .FirstOrDefault() ?? "";

            var raceCount = sectionRaces.Count;
            var completed = sectionRaces.Count(r => string.Equals(r.Result, "complete", StringComparison.OrdinalIgnoreCase));

            d.Rows.Add(new SectionFolderDigest.Row
            {
                ServerId = g.Key.ServerId,
                IrcSection = g.Key.Section,
                ParsedType = g.Key.ParsedType,
                Quality = g.Key.Quality,
                AnnounceCount = g.Count(),
                ObservedRemoteSection = observed,
                RaceCount = raceCount,
                RaceCompletionRate = raceCount == 0 ? 0 : (double)completed / raceCount
            });
        }

        // Cap to the top ~40 by announce volume — keeps the LLM prompt bounded and deterministic.
        d.Rows = d.Rows
            .OrderByDescending(r => r.AnnounceCount)
            .ThenBy(r => r.ServerId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.IrcSection, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.ParsedType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Quality, StringComparer.OrdinalIgnoreCase)
            .Take(40)
            .ToList();

        return d;
    }
}
