using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Serilog;

namespace GlDrive.Player;

/// <summary>
/// Finds the local IPv4 address of a requested VPN adapter. TorrentStreamService uses it
/// for the incoming listener and DHT socket; TorrentPeerNetworking resolves it again for
/// each outgoing peer connection and gives it precedence over ordinary download adapters.
/// FTP and IRC retain their own routing policies.
///
/// This is socket binding, not complete VPN isolation. Tracker and DNS traffic are not
/// bound here, existing sockets are not migrated on an adapter change, and listener/DHT
/// bindings require service recreation to change. An unavailable adapter retains the
/// configured warning-and-default-route behavior instead of acting as a kill switch.
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
    /// server switch can change it, so a cached value could bind the wrong interface.
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
