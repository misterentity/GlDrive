using GlDrive.Config;

namespace GlDrive.Spread;

public class SpreadScorer
{
    private readonly SpeedTracker _speedTracker;

    public SpreadScorer(SpeedTracker speedTracker)
    {
        _speedTracker = speedTracker;
    }

    public int Score(SpreadFileInfo file, string srcId, string dstId,
        SitePriority dstPriority, double ownedPercent,
        long maxFileSize, double maxSpeedBps,
        TimeSpan elapsed, SpreadMode mode,
        int priorFailures = 0)
    {
        // SFV always first
        if (file.Name.EndsWith(".sfv", StringComparison.OrdinalIgnoreCase))
            return Math.Max(65535 - priorFailures * 1000, 50000);

        // NFO after 15s
        if (file.Name.EndsWith(".nfo", StringComparison.OrdinalIgnoreCase) && elapsed.TotalSeconds >= 15)
            return Math.Max(65535 - priorFailures * 1000, 50000);

        int score = 0;

        // File size: larger files = higher priority (2000 max)
        if (maxFileSize > 0)
            score += (int)(file.Size / (double)maxFileSize * 2000);

        // Average speed for this route (3000 max)
        if (maxSpeedBps > 0)
        {
            var avgSpeed = _speedTracker.GetAverageSpeed(srcId, dstId);
            score += (int)(Math.Min(avgSpeed / maxSpeedBps, 1.0) * 3000);
        }

        // Site priority (direct enum value, max 2500)
        score += (int)dstPriority;

        // Ownership factor (2000 max)
        if (mode == SpreadMode.Race)
            score += (int)((1.0 - ownedPercent) * 2000);
        else
            score += (int)(ownedPercent * 2000);

        // Penalize pairs that have already failed for this file. cbftp's
        // scoreboard re-evaluation naturally rotates source on retry via
        // backoff timers (3s/10s); we replicate the bias by deducting score
        // proportional to fail count so an alternate src wins the slot. Each
        // failure costs 800 pts — at 3 fails the (file,src,dst) is well below
        // a fresh competing pair's score.
        score -= priorFailures * 800;
        if (score < 0) score = 0;

        return Math.Min(score, 65535);
    }
}

public class SpreadFileInfo
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public long Size { get; set; }
}

public enum SpreadMode { Race, Distribute }
