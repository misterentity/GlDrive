namespace GlDrive.AiAgent;

public sealed class SectionActivityDigester
{
    public SectionActivityDigest Build(IEnumerable<SectionActivityEvent> events)
    {
        var d = new SectionActivityDigest();
        foreach (var g in events.GroupBy(e => (e.ServerId, e.Section)))
        {
            var filesIn = g.Sum(e => e.FilesIn);
            var races = g.Sum(e => e.OurRaces);
            // Legacy rollups have no measured denominator. Exclude their wins
            // too, rather than dividing old wins by only newer known races.
            var observed = g.Where(e => e.KnownWinnerRaces.HasValue).ToList();
            var knownRaces = observed.Sum(e => e.KnownWinnerRaces.GetValueOrDefault());
            var wins = observed.Sum(e => e.OurWins);
            d.PerServerSection.Add(new SectionActivityDigest.Row
            {
                ServerId = g.Key.ServerId,
                Section = g.Key.Section,
                FilesIn = filesIn,
                OurRaces = races,
                KnownWinnerRaces = knownRaces,
                OurWinRate = knownRaces == 0 ? null : (double)wins / knownRaces
            });
        }
        return d;
    }
}
