namespace GlDrive.AiAgent;

public sealed class WishlistDigester
{
    public WishlistDigest Build(IEnumerable<WishlistAttemptEvent> events)
    {
        var list = events.ToList();
        var d = new WishlistDigest();
        var byItem = list.GroupBy(e => e.WishlistItemId).ToList();
        foreach (var g in byItem)
        {
            if (!g.Any(e => e.Matched))
                d.DeadItems.Add(new WishlistDigest.DeadItem
                {
                    ItemId = g.Key,
                    AttemptsInWindow = g.Count(),
                    DaysSinceLastMatch = 60
                });
        }
        foreach (var g in list.Where(e => !e.Matched && !string.IsNullOrEmpty(e.MissReason))
                              .GroupBy(e => (e.WishlistItemId, e.MissReason!))
                              .OrderByDescending(g => g.Count()).Take(25))
            d.NearMissPatterns.Add(new WishlistDigest.NearMiss
            {
                ItemId = g.Key.WishlistItemId,
                MissReason = g.Key.Item2,
                Count = g.Count()
            });
        return d;
    }
}
