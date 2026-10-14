using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using GlDrive.Config;
using Serilog;

namespace GlDrive.Downloads.Segmented;

/// <summary>
/// A network a download lane can be pinned to. <see cref="BindAddress"/> null means "default
/// route, unbound" — the behavior every download had before multi-network existed.
/// </summary>
public sealed record NetworkPath(string Id, string DisplayName, IPAddress? BindAddress, NetworkPathConfig? Settings)
{
    public const string DefaultId = "default";
    public static NetworkPath Default { get; } = new(DefaultId, "Default route", null, null);
    public bool IsDefault => BindAddress == null;
}

/// <summary>
/// Enumerates adapters that could carry download traffic and resolves which ones the user
/// enabled. Addresses are resolved fresh on every call — a tether or VPN reconnect changes
/// them, and a remembered address is a silent misbind (same rule as <see cref="Player.VpnBinding"/>).
/// </summary>
public static class NetworkCatalog
{
    /// <summary>What we need to know about one adapter. Keeps <see cref="Choose"/> testable.</summary>
    public readonly record struct Adapter(
        string Id, string Name, string Description, bool IsUp, string? IPv4, bool HasGateway, bool IsTunnel);

    /// <summary>Live adapter list from the OS: up, non-loopback, with an IPv4 address.</summary>
    public static IReadOnlyList<Adapter> EnumerateAdapters()
    {
        var list = new List<Adapter>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            try
            {
                var props = nic.GetIPProperties();
                var ipv4 = props.UnicastAddresses
                    .FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork)
                    ?.Address.ToString();
                var hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork);
                var isTunnel = nic.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp
                    || nic.Description.Contains("WireGuard", StringComparison.OrdinalIgnoreCase)
                    || nic.Description.Contains("TAP-", StringComparison.OrdinalIgnoreCase)
                    || nic.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase);
                list.Add(new Adapter(nic.Id, nic.Name, nic.Description,
                    nic.OperationalStatus == OperationalStatus.Up, ipv4, hasGateway, isTunnel));
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "NetworkCatalog: could not read {Adapter}", nic.Name);
            }
        }
        return list;
    }

    /// <summary>
    /// Adapters worth offering in Settings: up, IPv4, and able to reach the internet (a gateway
    /// or a tunnel). Hyper-V/WSL virtual switches have neither and are left out.
    /// </summary>
    public static IReadOnlyList<Adapter> Candidates(IEnumerable<Adapter> adapters) =>
        adapters.Where(a => a.IsUp && !string.IsNullOrWhiteSpace(a.IPv4) && (a.HasGateway || a.IsTunnel)).ToList();

    /// <summary>
    /// Resolve the enabled networks. No configured networks (the default) → just the unbound
    /// default route. Configured but none currently up → also the default route, logged once by
    /// the caller, so a missing tether never stops downloads.
    /// </summary>
    public static IReadOnlyList<NetworkPath> Choose(IEnumerable<NetworkPathConfig> configured, IEnumerable<Adapter> adapters)
    {
        var enabled = configured.Where(c => c.Enabled).ToList();
        if (enabled.Count == 0) return [NetworkPath.Default];

        var live = Candidates(adapters);
        var result = new List<NetworkPath>();
        foreach (var cfg in enabled)
        {
            var match = live.FirstOrDefault(a => !string.IsNullOrEmpty(cfg.AdapterId) && a.Id == cfg.AdapterId);
            if (match.Id == null)
                match = live.FirstOrDefault(a => !string.IsNullOrEmpty(cfg.AdapterName) &&
                    string.Equals(a.Name, cfg.AdapterName, StringComparison.OrdinalIgnoreCase));
            if (match.Id == null || !IPAddress.TryParse(match.IPv4, out var ip)) continue;

            var name = string.IsNullOrWhiteSpace(cfg.FriendlyName) ? match.Name : cfg.FriendlyName;
            result.Add(new NetworkPath(match.Id, name, ip, cfg));
        }
        return result.Count > 0 ? result : [NetworkPath.Default];
    }

    /// <summary>Live resolution against the OS adapter list.</summary>
    public static IReadOnlyList<NetworkPath> Resolve(MultiStreamConfig config)
    {
        var chosen = Choose(config.Networks, EnumerateAdapters());
        if (chosen.Count == 1 && chosen[0].IsDefault && config.Networks.Any(n => n.Enabled))
            Log.Warning("Downloads: none of the selected networks is up — using the default route");
        return chosen;
    }
}
