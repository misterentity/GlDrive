namespace GlDrive.AiAgent;

public sealed class ErrorsDigester
{
    public ErrorsDigest Build(IEnumerable<ErrorSignatureEvent> events, DateTime? windowStart = null, DateTime? windowEnd = null)
    {
        var list = events.ToList();
        var d = new ErrorsDigest();
        if (list.Count == 0) return d;
        var dated = list.Select(e => (Event: e, Timestamp: DateTimeOffset.TryParse(e.Ts,
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal,
            out var timestamp) ? timestamp.UtcDateTime : (DateTime?)null)).ToList();
        var first = windowStart?.ToUniversalTime() ?? dated.Min(e => e.Timestamp);
        var last = windowEnd?.ToUniversalTime() ?? dated.Max(e => e.Timestamp);
        var midpoint = first.HasValue && last.HasValue ? first.Value + (last.Value - first.Value) / 2 : (DateTime?)null;
        var prior = dated.Where(e => midpoint.HasValue && e.Timestamp < midpoint).Select(e => e.Event);
        var priorCounts = prior
            .GroupBy(e => (e.Component, e.ExceptionType, e.NormalizedMessage))
            .ToDictionary(g => g.Key, g => g.Sum(e => e.Count));

        foreach (var g in list.GroupBy(e => (e.Component, e.ExceptionType, e.NormalizedMessage))
                              .OrderByDescending(g => g.Sum(e => e.Count)).Take(15))
        {
            var total = g.Sum(e => e.Count);
            var priorCount = priorCounts.GetValueOrDefault(g.Key, 0);
            var recentCount = total - priorCount;
            var trend = !midpoint.HasValue ? "unknown" : recentCount > priorCount * 1.25 ? "up"
                       : recentCount < priorCount * 0.75 ? "down"
                       : "flat";
            d.TopSignatures.Add(new ErrorsDigest.Sig
            {
                Component = g.Key.Component,
                ExceptionType = g.Key.ExceptionType,
                NormalizedMessage = g.Key.NormalizedMessage,
                Count = total,
                TrendVsPrior = trend
            });
        }
        return d;
    }
}
