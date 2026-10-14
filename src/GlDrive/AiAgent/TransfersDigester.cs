namespace GlDrive.AiAgent;

public sealed class TransfersDigester
{
    public TransfersDigest Build(IEnumerable<FileTransferEvent> events, CancellationToken cancellationToken = default)
    {
        var d = new TransfersDigest();
        var routes = new Dictionary<(string Source, string Destination), (double Bytes, double Milliseconds)>();
        var latencies = new SortedDictionary<long, long>();
        long count = 0;
        // Throughput comes from completed transfers only: an aborted row carries the
        // file's EXPECTED size (non-relay modes can't measure bytes) over however long
        // it took to fail, which is not a speed.
        foreach (var e in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(e.AbortReason))
            {
                var key = (e.SrcServer, e.DstServer);
                var previous = routes.GetValueOrDefault(key);
                routes[key] = (previous.Bytes + e.Bytes, previous.Milliseconds + e.ElapsedMs);
            }
            latencies[e.TtfbMs] = latencies.GetValueOrDefault(e.TtfbMs) + 1;
            count++;
        }
        foreach (var (route, total) in routes)
            d.KbpsMatrix[$"{route.Source}->{route.Destination}"] = total.Milliseconds == 0
                ? 0 : total.Bytes * 8.0 / total.Milliseconds;
        if (count > 0)
        {
            var index = (long)Math.Round(0.99 * (count - 1));
            foreach (var (latency, occurrences) in latencies)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (index < occurrences) { d.TtfbP99Ms = latency; break; }
                index -= occurrences;
            }
        }
        return d;
    }
}
