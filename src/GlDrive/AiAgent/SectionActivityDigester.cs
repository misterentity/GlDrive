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
            var wins = g.Sum(e => e.OurWins);
            d.PerServerSection.Add(new SectionActivityDigest.Row
            {
                ServerId = g.Key.ServerId,
                Section = g.Key.Section,
                FilesIn = filesIn,
                OurRaces = races,
                OurWinRate = races == 0 ? 0 : (double)wins / races
            });
        }
        return d;
    }
}
