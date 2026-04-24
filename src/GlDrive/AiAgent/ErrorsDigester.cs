namespace GlDrive.AiAgent;

public sealed class ErrorsDigester
{
    public ErrorsDigest Build(IEnumerable<ErrorSignatureEvent> events)
    {
        var list = events.ToList();
        var d = new ErrorsDigest();
        var half = list.Count / 2;
        var prior = list.Take(half).ToList();
        var priorCounts = prior
            .GroupBy(e => (e.Component, e.ExceptionType, e.NormalizedMessage))
            .ToDictionary(g => g.Key, g => g.Sum(e => e.Count));

        foreach (var g in list.GroupBy(e => (e.Component, e.ExceptionType, e.NormalizedMessage))
                              .OrderByDescending(g => g.Sum(e => e.Count)).Take(15))
        {
            var total = g.Sum(e => e.Count);
            var priorCount = priorCounts.GetValueOrDefault(g.Key, 0);
            var trend = total > priorCount * 1.25 ? "up"
                       : total < priorCount * 0.75 ? "down"
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
