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
        TimeSpan elapsed, SpreadMode mode)
    {
        // SFV always first
        if (file.Name.EndsWith(".sfv", StringComparison.OrdinalIgnoreCase))
            return 65535;

        // NFO after 15s
        if (file.Name.EndsWith(".nfo", StringComparison.OrdinalIgnoreCase) && elapsed.TotalSeconds >= 15)
            return 65535;

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
