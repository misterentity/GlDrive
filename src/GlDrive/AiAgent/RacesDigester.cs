namespace GlDrive.AiAgent;

public sealed class RacesDigester
{
    public RacesDigest Build(IEnumerable<RaceOutcomeEvent> events)
    {
        var list = events.ToList();
        var d = new RacesDigest { TotalRaces = list.Count };
        if (list.Count == 0) return d;

        // Win rate per server: how often this server was the winner out of the races it participated in.
        var serverParticipation = list
            .SelectMany(r => r.Participants.Select(p => (p.ServerId, won: r.Winner == p.ServerId)))
            .GroupBy(x => x.ServerId);
        foreach (var g in serverParticipation)
        {
            var items = g.ToList();
            d.WinRateByServer[g.Key] = items.Count == 0 ? 0 : (double)items.Count(x => x.won) / items.Count;
        }

        // Per-route kbps average (src->dst pairs)
        var routeKbps = new Dictionary<string, List<double>>();
        foreach (var r in list)
        {
            var srcId = r.Participants.FirstOrDefault(p => p.Role == "src")?.ServerId;
            if (string.IsNullOrEmpty(srcId)) continue;
            foreach (var p in r.Participants.Where(p => p.Role == "dst"))
            {
                var key = $"{srcId}->{p.ServerId}";
                if (!routeKbps.TryGetValue(key, out var bag)) routeKbps[key] = bag = new List<double>();
                if (p.AvgKbps > 0) bag.Add(p.AvgKbps);
            }
        }
        foreach (var (k, v) in routeKbps)
            d.KbpsByRoute[k] = v.Count == 0 ? 0 : v.Average();

        // Abort reason histogram (counts non-null AbortReason values across participants)
        foreach (var g in list.SelectMany(r => r.Participants)
                              .Where(p => !string.IsNullOrEmpty(p.AbortReason))
                              .GroupBy(p => p.AbortReason!))
            d.AbortReasonHistogram[g.Key] = g.Count();

        // Completion rate per section
        foreach (var g in list.GroupBy(r => r.Section))
        {
            var items = g.ToList();
            var complete = items.Count(r => r.Result == "complete");
            d.CompletionRateBySection[g.Key] = items.Count == 0 ? 0 : (double)complete / items.Count;
        }

        return d;
    }
}
