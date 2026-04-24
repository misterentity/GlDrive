namespace GlDrive.AiAgent;

public sealed class SiteHealthDigester
{
    public SiteHealthDigest Build(IEnumerable<SiteHealthEvent> events)
    {
        var d = new SiteHealthDigest();
        foreach (var g in events.GroupBy(e => e.ServerId))
        {
            var rows = g.OrderBy(e => e.WindowStart).ToList();
            if (rows.Count == 0) continue;
            var first = rows.Take(Math.Max(1, rows.Count / 4)).ToList();
            var last  = rows.Skip(Math.Max(0, 3 * rows.Count / 4)).ToList();
            double PctChange(Func<SiteHealthEvent, double> sel)
            {
                var a = first.Count == 0 ? 0 : first.Average(sel);
                var b = last.Count == 0 ? 0 : last.Average(sel);
                return a == 0 ? 0 : (b - a) / a;
            }
            var delta = new SiteHealthDigest.HealthDelta
            {
                AvgConnectMsPctChange = PctChange(e => e.AvgConnectMs),
                TlsHandshakePctChange = PctChange(e => e.TlsHandshakeMs),
                DisconnectsTotal = rows.Sum(e => e.Disconnects),
                PoolExhaustTotal = rows.Sum(e => e.PoolExhaustCount),
                GhostKillsTotal  = rows.Sum(e => e.GhostKills)
            };
            if (delta.AvgConnectMsPctChange > 0.5) delta.Flagged.Add("connect-latency-regression");
            if (delta.PoolExhaustTotal > 5)         delta.Flagged.Add("pool-exhaustion");
            if (delta.DisconnectsTotal > 20)        delta.Flagged.Add("frequent-disconnects");
            d.ServerDeltas[g.Key] = delta;
        }
        return d;
    }
}
