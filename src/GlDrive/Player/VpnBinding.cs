using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Serilog;

namespace GlDrive.Player;

/// <summary>
/// Finds the local IPv4 address of a VPN tunnel adapter so torrent sockets can be bound to it,
/// keeping torrent traffic on the VPN while GlDrive's FTP and IRC connections keep using the
/// default route.
///
/// Why binding rather than ProtonVPN's own split tunnelling (asked 2026-08-15): Proton's split
/// tunnelling is PER-PROCESS. GlDrive does FTP, IRC and BitTorrent from one GlDrive.exe, so
/// Proton can only put the whole process in or out of the tunnel — it cannot tell one socket
/// from another. Binding individual sockets is the only mechanism with the right granularity,
/// and it is what qBittorrent's "Network Interface" setting does.
///
/// This works on this machine because the VPN is NOT the default route: measured 2026-08-15,
/// ProtonVPN's route metric was 32000 against Ethernet's 25, and Find-NetRoute resolved a
/// normal connection to 192.168.1.92. So ordinary traffic already bypasses the tunnel and only
/// the explicitly-bound sockets ride it.
///
/// KNOWN LIMIT — read before trusting this. It binds what MonoTorrent 3.0.2 exposes: the
/// incoming listener and the DHT socket. It CANNOT bind outgoing peer connections, because
/// injecting a custom ISocketConnector needs the `Factories` API that does not exist in 3.0.2,
/// and 3.0.2 is the newest stable release (3.0.3 and 3.9.0 are alpha). Outgoing connections
/// therefore still follow the default route. This is a partial measure, not a leak-proof one.
/// </summary>
public static class VpnBinding
{
    /// <summary>What we need to know about one network adapter. Keeps the choice testable.</summary>
    public readonly record struct Adapter(string Name, string Description, bool IsUp, string? IPv4);

    /// <summary>
    /// Pick the adapter whose name or description matches <paramref name="wanted"/> and return
    /// its IPv4 address, or null when there is no usable match.
    ///
    /// Matching is a case-insensitive substring on BOTH name and description because the two
    /// differ in practice — on this box the adapter is named "ProtonVPN" while its description
    /// reads "WireGuard Tunnel", and either could be what a user types.
    ///
    /// The address is deliberately resolved fresh rather than remembered: a VPN reconnect or
    /// server switch changes it (10.2.0.2 today), so a cached value is a silent misbind waiting
    /// to happen.
    /// </summary>
    public static string? ResolveAddress(string wanted, IEnumerable<Adapter> adapters)
    {
        if (string.IsNullOrWhiteSpace(wanted)) return null;

        foreach (var a in adapters)
        {
            if (!a.IsUp) continue;
            if (string.IsNullOrWhiteSpace(a.IPv4)) continue;

            var nameHit = a.Name?.Contains(wanted, StringComparison.OrdinalIgnoreCase) == true;
            var descHit = a.Description?.Contains(wanted, StringComparison.OrdinalIgnoreCase) == true;
            if (nameHit || descHit) return a.IPv4;
        }

        return null;
    }

    /// <summary>Live adapter list from the OS.</summary>
    public static IEnumerable<Adapter> EnumerateAdapters()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            string? ipv4 = null;
            try
            {
                ipv4 = nic.GetIPProperties().UnicastAddresses
                    .FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork)
                    ?.Address.ToString();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "VpnBinding: could not read addresses for {Adapter}", nic.Name);
            }

            yield return new Adapter(
                nic.Name,
                nic.Description,
                nic.OperationalStatus == OperationalStatus.Up,
                ipv4);
        }
    }

    /// <summary>
    /// Resolve the bind address for <paramref name="adapterName"/> against the live adapter
    /// list, returning <see cref="IPAddress.Any"/> when it cannot be found.
    ///
    /// Falling back to Any means torrent traffic uses the ordinary connection. That is a leak
    /// relative to the feature's intent, so it is logged at Warning — the user chose
    /// warn-and-continue over a hard kill switch, and the one thing that must not happen is
    /// for it to be silent.
    /// </summary>
    public static IPAddress ResolveBindAddress(string? adapterName)
    {
        if (string.IsNullOrWhiteSpace(adapterName)) return IPAddress.Any;

        var address = ResolveAddress(adapterName, EnumerateAdapters());
        if (address != null && IPAddress.TryParse(address, out var parsed))
        {
            Log.Information("VpnBinding: torrent sockets bound to {Adapter} ({Address})",
                adapterName, address);
            return parsed;
        }

        Log.Warning(
            "VpnBinding: adapter \"{Adapter}\" not found or has no IPv4 — torrent traffic will use " +
            "your NORMAL connection, not the VPN. Connect the VPN and restart the player to bind it.",
            adapterName);

        return IPAddress.Any;
    }
}
