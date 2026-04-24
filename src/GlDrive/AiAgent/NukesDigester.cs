namespace GlDrive.AiAgent;

public sealed class NukesDigester
{
    public NukesDigest Build(IEnumerable<NukeDetectedEvent> events)
    {
        var list = events.ToList();
        var d = new NukesDigest
        {
            Total = list.Count,
            Correlated = list.Count(n => !string.IsNullOrEmpty(n.OurRaceRef))
        };
        d.TopNukedReleases = list
            .GroupBy(n => n.Release)
            .OrderByDescending(g => g.Count())
            .Take(20)
            .Select(g => new NukesDigest.NukeTop
            {
                Release = g.Key,
                Count = g.Count(),
                Reason = g.First().Reason,
                Section = g.First().Section
            }).ToList();
        foreach (var g in list.GroupBy(n => n.Section))
            d.NukeRateBySection[g.Key] = g.Count();
        return d;
    }
}
