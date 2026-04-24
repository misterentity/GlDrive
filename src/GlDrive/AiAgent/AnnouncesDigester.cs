using System.Text.RegularExpressions;

namespace GlDrive.AiAgent;

public sealed class AnnouncesDigester
{
    private static readonly Regex _relSqueeze = new(@"[A-Z0-9][A-Za-z0-9\.\-_]{3,}", RegexOptions.Compiled);

    public AnnouncesDigest Build(IEnumerable<AnnounceNoMatchEvent> events)
    {
        var d = new AnnouncesDigest();
        string Normalize(string m) => _relSqueeze.Replace(m ?? "", "<rel>");
        var clusters = events.GroupBy(e => (e.Channel, e.BotNick, Normalize(e.Message)));
        foreach (var g in clusters.OrderByDescending(g => g.Count()).Take(30))
        {
            var rep = g.First();
            d.Clusters.Add(new AnnouncesDigest.AnnounceCluster
            {
                Representative = rep.Message,
                Count = g.Count(),
                Channel = rep.Channel,
                BotNick = rep.BotNick,
                NearestRule = rep.NearestRulePattern,
                NearestRuleDistance = rep.NearestRuleDistance
            });
        }
        return d;
    }
}
