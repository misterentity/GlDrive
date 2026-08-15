using GlDrive.Player;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Cover for the 2026-08-15 request: run torrent traffic over ProtonVPN while GlDrive's FTP and
/// IRC connections keep using the normal connection.
///
/// ProtonVPN's own split tunnelling is per-PROCESS and GlDrive does all three from one
/// executable, so it cannot separate them. Binding individual sockets to the tunnel adapter is
/// the only mechanism with the right granularity. This class picks that adapter's address.
/// </summary>
public sealed class VpnBindingTests
{
    // Modelled on this machine's real adapter list, measured 2026-08-15.
    private static readonly VpnBinding.Adapter[] RealWorld =
    [
        new("Ethernet", "Realtek Gaming 2.5GbE Family Controller", true, "192.168.1.92"),
        new("ProtonVPN", "WireGuard Tunnel", true, "10.2.0.2"),
        new("Wi-Fi", "Intel(R) Wi-Fi 6 AX200 160MHz", false, null),
        new("vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter", true, "172.29.112.1"),
    ];

    [Fact]
    public void Finds_the_vpn_adapter_by_name()
    {
        Assert.Equal("10.2.0.2", VpnBinding.ResolveAddress("ProtonVPN", RealWorld));
    }

    [Fact]
    public void Matching_is_case_insensitive()
    {
        Assert.Equal("10.2.0.2", VpnBinding.ResolveAddress("protonvpn", RealWorld));
    }

    /// <summary>
    /// The adapter's NAME and DESCRIPTION differ in practice — here "ProtonVPN" versus
    /// "WireGuard Tunnel" — and a user could reasonably type either.
    /// </summary>
    [Fact]
    public void Matches_on_description_too()
    {
        Assert.Equal("10.2.0.2", VpnBinding.ResolveAddress("WireGuard", RealWorld));
    }

    [Fact]
    public void Substring_match_survives_proton_renaming_the_adapter()
    {
        var renamed = new[] { new VpnBinding.Adapter("Proton VPN TUN", "WireGuard Tunnel", true, "10.9.9.9") };
        Assert.Equal("10.9.9.9", VpnBinding.ResolveAddress("Proton", renamed));
    }

    /// <summary>
    /// A disconnected VPN must not be bound to. Windows keeps the adapter listed with a stale
    /// address after a drop, and binding to it would fail or silently blackhole.
    /// </summary>
    [Fact]
    public void Ignores_an_adapter_that_is_down()
    {
        var down = new[]
        {
            new VpnBinding.Adapter("ProtonVPN", "WireGuard Tunnel", false, "10.2.0.2"),
            new VpnBinding.Adapter("Ethernet", "Realtek", true, "192.168.1.92"),
        };

        Assert.Null(VpnBinding.ResolveAddress("ProtonVPN", down));
    }

    [Fact]
    public void Ignores_an_adapter_with_no_ipv4()
    {
        var noIp = new[] { new VpnBinding.Adapter("ProtonVPN", "WireGuard Tunnel", true, null) };
        Assert.Null(VpnBinding.ResolveAddress("ProtonVPN", noIp));
    }

    /// <summary>
    /// Missing VPN returns null so the caller can warn. It must never silently fall through to
    /// a different adapter's address — binding torrent traffic to Ethernet while telling the
    /// user it is on the VPN is worse than not binding at all.
    /// </summary>
    [Fact]
    public void Absent_vpn_returns_null_not_another_adapter()
    {
        var noVpn = new[]
        {
            new VpnBinding.Adapter("Ethernet", "Realtek", true, "192.168.1.92"),
            new VpnBinding.Adapter("vEthernet (WSL)", "Hyper-V", true, "172.31.128.1"),
        };

        Assert.Null(VpnBinding.ResolveAddress("ProtonVPN", noVpn));
    }

    [Fact]
    public void Empty_adapter_name_matches_nothing()
    {
        Assert.Null(VpnBinding.ResolveAddress("", RealWorld));
        Assert.Null(VpnBinding.ResolveAddress("   ", RealWorld));
    }

    /// <summary>
    /// The VPN's address changes on reconnect or server switch, so resolution must read the
    /// current list every time rather than trust a remembered value.
    /// </summary>
    [Fact]
    public void Resolves_the_current_address_after_a_reconnect()
    {
        var before = new[] { new VpnBinding.Adapter("ProtonVPN", "WireGuard Tunnel", true, "10.2.0.2") };
        var after = new[] { new VpnBinding.Adapter("ProtonVPN", "WireGuard Tunnel", true, "10.66.4.7") };

        Assert.Equal("10.2.0.2", VpnBinding.ResolveAddress("ProtonVPN", before));
        Assert.Equal("10.66.4.7", VpnBinding.ResolveAddress("ProtonVPN", after));
    }
}
