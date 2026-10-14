namespace GlDrive.Downloads.Segmented;

/// <summary>Formats a download's lanes for the downloads list, e.g. "3 streams · Ethernet ×2, race login ×1".</summary>
public static class LaneSummary
{
    public static string Format(IReadOnlyList<LaneStats>? lanes)
    {
        if (lanes == null || lanes.Count == 0) return "";

        var groups = new List<(string Label, int Count)>();
        var running = 0;
        var yielded = 0;
        var failed = 0;
        LaneStats? onlyRunning = null;

        foreach (var lane in lanes)
        {
            switch (lane.State)
            {
                case LaneState.Active or LaneState.Connecting:
                    running++;
                    onlyRunning = lane;
                    var i = groups.FindIndex(g => g.Label == lane.Label);
                    if (i < 0) groups.Add((lane.Label, 1));
                    else groups[i] = (lane.Label, groups[i].Count + 1);
                    break;
                case LaneState.Yielded: yielded++; break;
                case LaneState.Failed: failed++; break;
            }
        }

        // A single stream on the default route is the pre-multi-stream behavior: nothing to say.
        if (running == 1 && yielded == 0 && failed == 0 && IsDefaultRoute(onlyRunning!.NetworkId))
            return "";

        var parts = new List<string>();
        if (running > 0)
        {
            var streams = running == 1 ? "1 stream" : $"{running} streams";
            parts.Add($"{streams} · {string.Join(", ", groups.Select(g => $"{g.Label} ×{g.Count}"))}");
        }
        if (yielded > 0) parts.Add($"{yielded} yielded");
        if (failed > 0) parts.Add($"{failed} failed");
        return string.Join(" · ", parts);
    }

    private static bool IsDefaultRoute(string? networkId) =>
        string.IsNullOrEmpty(networkId) || networkId == NetworkPath.DefaultId;
}
