namespace GlDrive.AiAgent;

public sealed class TransfersDigester
{
    public TransfersDigest Build(IEnumerable<FileTransferEvent> events)
    {
        var list = events.ToList();
        var d = new TransfersDigest();
        // Throughput comes from completed transfers only: an aborted row carries the
        // file's EXPECTED size (non-relay modes can't measure bytes) over however long
        // it took to fail, which is not a speed.
        foreach (var g in list.Where(e => string.IsNullOrEmpty(e.AbortReason))
                              .GroupBy(e => $"{e.SrcServer}->{e.DstServer}"))
        {
            var bytes = g.Sum(e => e.Bytes);
            var ms = g.Sum(e => e.ElapsedMs);
            d.KbpsMatrix[g.Key] = ms == 0 ? 0 : bytes * 8.0 / ms;
        }
        if (list.Count > 0)
        {
            var ttfbs = list.Select(e => (double)e.TtfbMs).OrderBy(x => x).ToList();
            var idx = (int)Math.Clamp(Math.Round(0.99 * (ttfbs.Count - 1)), 0, ttfbs.Count - 1);
            d.TtfbP99Ms = ttfbs[idx];
        }
        return d;
    }
}
