using Serilog;

namespace GlDrive.Downloads.Segmented;

/// <summary>
/// plexo-style HTTP(S) download: a one-byte range probe, then N ranged connections per enabled
/// network writing into a <c>.gdpart</c> staging file. Servers that ignore ranges get one
/// ordinary stream. Shares the app-wide pacer and data-usage ledger with FTP downloads.
/// </summary>
public static class SegmentedHttpDownloader
{
    private const long HttpMinSplit = 2L * 1024 * 1024;
    private const long HttpMaxTake = 16L * 1024 * 1024;

    public static async Task DownloadAsync(Uri url, string localPath,
        IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var multi = DownloadNetworkServices.Config.MultiStream;
        var networks = DownloadNetworkServices.ResolveNetworks()
            .Where(n => !DownloadNetworkServices.IsOverAllowance(n)).ToList();
        if (networks.Count == 0)
            throw new NetworkAllowanceExhaustedException("every selected network");

        var clients = networks.Select(n => (Network: n, Http: NetworkHttpClients.Create(n))).ToList();
        try
        {
            var probe = await HttpRangeProbe.ProbeAsync(clients[0].Http, url, ct);
            var perNetwork = multi.Enabled && probe.AcceptsRanges && probe.Size > 0
                ? Math.Clamp(multi.HttpLanesPerNetwork, 1, 32)
                : 1;
            var lanes = new List<ISegmentLane>();
            foreach (var (network, http) in clients)
            {
                for (var i = 0; i < perNetwork; i++)
                    lanes.Add(new HttpLane(http, probe.FinalUri, network, network.DisplayName));
                if (perNetwork == 1 && !probe.AcceptsRanges) break; // no ranges: exactly one stream
            }

            Log.Information("HTTP download: {Url} -> {Local} ({Size:N0} bytes, {Lanes} stream(s) over {Networks})",
                url.GetLeftPart(UriPartial.Path), localPath, probe.Size, lanes.Count,
                string.Join(", ", networks.Select(n => n.DisplayName)));

            var file = new TransferFile(url.ToString(), localPath,
                probe.AcceptsRanges ? probe.Size : -1, probe.Validator);
            var options = new SegmentedTransferOptions
            {
                MinSplitBytes = HttpMinSplit,
                MaxTakeBytes = HttpMaxTake,
                MaxLanes = lanes.Count,
                Pacer = DownloadNetworkServices.Pacer,
                OnBytes = DownloadNetworkServices.RecordBytes,
            };

            using (KeepAwake.Acquire())
                await new SegmentedTransfer([file], options).RunAsync(lanes, null, progress, ct);
        }
        finally
        {
            foreach (var (_, http) in clients) http.Dispose();
            DownloadNetworkServices.Flush();
        }
    }
}
