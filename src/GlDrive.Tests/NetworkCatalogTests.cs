using System.Net;
using GlDrive.Config;
using GlDrive.Downloads.Segmented;
using Xunit;
using Adapter = GlDrive.Downloads.Segmented.NetworkCatalog.Adapter;

namespace GlDrive.Tests;

public class NetworkCatalogTests
{
    private static readonly Adapter Ethernet = new("{eth}", "Ethernet", "Intel I225", true, "192.168.1.92", true, false);
    private static readonly Adapter Tether = new("{tether}", "Ethernet 3", "Remote NDIS", true, "172.20.10.2", true, false);
    private static readonly Adapter Vpn = new("{vpn}", "ProtonVPN", "WireGuard Tunnel", true, "10.2.0.2", false, true);
    private static readonly Adapter HyperV = new("{hv}", "vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter", true, "172.31.0.1", false, false);
    private static readonly Adapter Down = new("{down}", "Wi-Fi", "Intel AX211", false, "192.168.0.5", true, false);

    private static readonly Adapter[] All = [Ethernet, Tether, Vpn, HyperV, Down];

    [Fact]
    public void Empty_config_gives_the_default_route()
    {
        var chosen = NetworkCatalog.Choose([], All);
        Assert.Equal([NetworkPath.Default], chosen);
        Assert.True(chosen[0].IsDefault);
    }

    [Fact]
    public void Matches_by_id_first_then_by_name()
    {
        var byId = new NetworkPathConfig { AdapterId = "{tether}", AdapterName = "Ethernet", FriendlyName = "Phone" };
        var byName = new NetworkPathConfig { AdapterId = "{gone}", AdapterName = "protonvpn" };

        var chosen = NetworkCatalog.Choose([byId, byName], All);

        Assert.Equal(2, chosen.Count);
        Assert.Equal("{tether}", chosen[0].Id);
        Assert.Equal("Phone", chosen[0].DisplayName);
        Assert.Equal(IPAddress.Parse("172.20.10.2"), chosen[0].BindAddress);
        Assert.Same(byId, chosen[0].Settings);
        Assert.Equal("{vpn}", chosen[1].Id);
        Assert.Equal("ProtonVPN", chosen[1].DisplayName);
    }

    [Fact]
    public void Disabled_entries_are_ignored()
    {
        var chosen = NetworkCatalog.Choose(
        [
            new NetworkPathConfig { AdapterId = "{eth}", Enabled = false },
            new NetworkPathConfig { AdapterId = "{tether}" },
        ], All);

        Assert.Equal("{tether}", Assert.Single(chosen).Id);
    }

    [Fact]
    public void Only_disabled_entries_gives_the_default_route()
    {
        var chosen = NetworkCatalog.Choose([new NetworkPathConfig { AdapterId = "{eth}", Enabled = false }], All);
        Assert.Equal([NetworkPath.Default], chosen);
    }

    [Fact]
    public void None_up_falls_back_to_the_default_route()
    {
        var chosen = NetworkCatalog.Choose(
        [
            new NetworkPathConfig { AdapterId = "{down}" },
            new NetworkPathConfig { AdapterId = "{missing}", AdapterName = "Nope" },
        ], All);

        Assert.Equal([NetworkPath.Default], chosen);
    }

    [Fact]
    public void Candidates_exclude_hyperv_switches_down_and_addressless_adapters()
    {
        var noIp = new Adapter("{noip}", "Ethernet 5", "USB", true, null, true, false);
        var ids = NetworkCatalog.Candidates([.. All, noIp]).Select(a => a.Id).ToList();

        Assert.Equal(["{eth}", "{tether}", "{vpn}"], ids);
    }

    [Fact]
    public void Hyperv_switch_cannot_be_chosen_even_when_configured()
    {
        var chosen = NetworkCatalog.Choose([new NetworkPathConfig { AdapterId = "{hv}" }], All);
        Assert.Equal([NetworkPath.Default], chosen);
    }
}
