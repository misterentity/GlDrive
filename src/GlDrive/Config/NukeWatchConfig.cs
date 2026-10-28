namespace GlDrive.Config;

public sealed class NukeWatchConfig
{
    public bool Enabled { get; set; } = true;
    public bool AutoQueueFixes { get; set; }
    public int WatchDays { get; set; } = 7;
    // Bounds only passive observations/history, never transfers or connections.
    public int HistoryCapacity { get; set; } = 2048;
    public int QueueCapacity { get; set; } = 256;
}
