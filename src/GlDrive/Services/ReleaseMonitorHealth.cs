namespace GlDrive.Services;

/// <summary>Mount-scoped health, retained when only the notification monitor restarts.</summary>
public sealed class ReleaseMonitorHealth
{
    private readonly Lock _gate = new();
    private bool _warned;
    public bool HasNoCategories { get; private set; }
    public string WatchPath { get; private set; } = "";
    public DateTime? LastCheckedUtc { get; private set; }

    internal bool Observe(string watchPath, int categoryCount, DateTime utcNow)
    {
        lock (_gate)
        {
            WatchPath = watchPath;
            LastCheckedUtc = utcNow;
            HasNoCategories = categoryCount == 0;
            if (!HasNoCategories || _warned) return false;
            _warned = true;
            return true;
        }
    }
}
