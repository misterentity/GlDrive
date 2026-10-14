using System.IO;
using GlDrive.Config;

namespace GlDrive.Downloads.Segmented;

/// <summary>
/// App-wide state every download shares: one pacer (so the total and per-network limits hold
/// across all servers and HTTP downloads at once) and one data-usage ledger. Limits are read
/// from the live <see cref="DownloadConfig"/> on every call, so Settings and the tray's slow
/// mode take effect mid-transfer.
/// </summary>
public static class DownloadNetworkServices
{
    private static readonly object Lock = new();
    private static DownloadConfig? _config;
    private static TransferPacer? _pacer;
    private static NetworkUsageStore? _usage;

    /// <summary>Bind to the live download config. Later calls rebind (config objects are mutated in place, but be safe).</summary>
    public static void Initialize(DownloadConfig config)
    {
        lock (Lock) _config = config;
    }

    public static DownloadConfig Config => Volatile.Read(ref _config) ?? new DownloadConfig();

    public static TransferPacer Pacer
    {
        get
        {
            lock (Lock)
                return _pacer ??= new TransferPacer(TotalBytesPerSecond, NetworkBytesPerSecond);
        }
    }

    public static NetworkUsageStore Usage
    {
        get
        {
            lock (Lock)
                return _usage ??= new NetworkUsageStore(Path.Combine(ConfigManager.AppDataPath, "network-usage.json"));
        }
    }

    /// <summary>Total cap in bytes/s: slow mode wins, else the global download limit; 0 = unlimited.</summary>
    public static long TotalBytesPerSecond()
    {
        var c = Config;
        if (c.MultiStream.SlowMode && c.MultiStream.SlowModeKbps > 0) return c.MultiStream.SlowModeKbps * 1024L;
        return c.SpeedLimitKbps > 0 ? c.SpeedLimitKbps * 1024L : 0;
    }

    private static long NetworkBytesPerSecond(string? networkId)
    {
        if (string.IsNullOrEmpty(networkId)) return 0;
        var net = Config.MultiStream.Networks.FirstOrDefault(n => n.AdapterId == networkId);
        return net is { SpeedLimitKbps: > 0 } ? net.SpeedLimitKbps * 1024L : 0;
    }

    public static IReadOnlyList<NetworkPath> ResolveNetworks() => NetworkCatalog.Resolve(Config.MultiStream);

    public static bool IsOverAllowance(NetworkPath path) => !path.IsDefault && Usage.IsOverAllowance(path);

    public static void RecordBytes(string? networkId, long bytes) =>
        Usage.Add(networkId ?? NetworkPath.DefaultId, bytes);

    public static void Flush()
    {
        lock (Lock) _usage?.Flush();
    }
}
