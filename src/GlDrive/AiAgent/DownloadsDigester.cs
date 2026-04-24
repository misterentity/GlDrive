namespace GlDrive.AiAgent;

public sealed class DownloadsDigester
{
    public DownloadsDigest Build(IEnumerable<DownloadOutcomeEvent> events)
    {
        var list = events.ToList();
        var d = new DownloadsDigest
        {
            TotalComplete = list.Count(e => e.Result == "complete"),
            TotalFailed   = list.Count(e => e.Result == "failed")
        };
        foreach (var g in list.Where(e => !string.IsNullOrEmpty(e.FailureClass))
                              .GroupBy(e => e.FailureClass!))
            d.FailureClassHistogram[g.Key] = g.Count();
        return d;
    }
}
