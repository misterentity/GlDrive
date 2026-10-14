using GlDrive.Config;
using GlDrive.Downloads.Segmented;
using GlDrive.UI;
using Xunit;

namespace GlDrive.Tests;

public class LaneSummaryTests
{
    private static LaneStats Lane(string label, LaneState state, string? networkId = "eth") =>
        new(label, networkId, 0, 0, state);

    [Fact]
    public void NullOrEmpty_IsBlank()
    {
        Assert.Equal("", LaneSummary.Format(null));
        Assert.Equal("", LaneSummary.Format([]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(NetworkPath.DefaultId)]
    public void SingleActiveLaneOnDefaultRoute_IsBlank(string? networkId)
    {
        Assert.Equal("", LaneSummary.Format([Lane("Default route", LaneState.Active, networkId)]));
    }

    [Fact]
    public void SingleLaneOnNamedNetwork_IsShown()
    {
        Assert.Equal("1 stream · Phone ×1", LaneSummary.Format([Lane("Phone", LaneState.Active, "tether")]));
    }

    [Fact]
    public void GroupsByLabelInFirstSeenOrder_CountingActiveAndConnecting()
    {
        var lanes = new[]
        {
            Lane("Ethernet", LaneState.Active),
            Lane("race login", LaneState.Connecting, null),
            Lane("Ethernet", LaneState.Connecting),
            Lane("Ethernet", LaneState.Done),
            Lane("Ethernet", LaneState.Idle),
        };
        Assert.Equal("3 streams · Ethernet ×2, race login ×1", LaneSummary.Format(lanes));
    }

    [Fact]
    public void YieldedAndFailedAreAppended()
    {
        var lanes = new[]
        {
            Lane("Ethernet", LaneState.Active),
            Lane("race login", LaneState.Yielded, null),
            Lane("Phone", LaneState.Failed, "tether"),
        };
        Assert.Equal("1 stream · Ethernet ×1 · 1 yielded · 1 failed", LaneSummary.Format(lanes));
    }

    [Fact]
    public void SingleDefaultLaneWithYielded_IsShown()
    {
        var lanes = new[]
        {
            Lane("Default route", LaneState.Active, null),
            Lane("race login", LaneState.Yielded, null),
        };
        Assert.Equal("1 stream · Default route ×1 · 1 yielded", LaneSummary.Format(lanes));
    }

    [Fact]
    public void NothingRunning_OnlyReportsFailures()
    {
        Assert.Equal("2 failed", LaneSummary.Format([Lane("A", LaneState.Failed), Lane("B", LaneState.Failed)]));
        Assert.Equal("", LaneSummary.Format([Lane("A", LaneState.Done)]));
    }
}

public class NetworkRowMappingTests
{
    private static NetworkCatalog.Adapter Adapter(string id, string name, string ip) =>
        new(id, name, name, true, ip, true, false);

    [Fact]
    public void Build_MergesLiveAndConfigured_MarksMissingAsNotConnected()
    {
        var configured = new List<NetworkPathConfig>
        {
            new() { AdapterId = "eth-id", AdapterName = "Ethernet", FriendlyName = "Home", Enabled = true, SpeedLimitKbps = 500 },
            new() { AdapterId = "old-id", AdapterName = "Tether", Enabled = true },
            new() { AdapterId = "", AdapterName = "wi-fi", Enabled = false, AllowanceGb = 10, Period = AllowancePeriod.Monthly },
        };
        var live = new[] { Adapter("eth-id", "Ethernet", "192.168.1.5"), Adapter("wifi-id", "Wi-Fi", "10.0.0.2"), Adapter("x", "Other", "1.2.3.4") };

        var rows = NetworkRowViewModel.Build(configured, live);

        Assert.Equal(4, rows.Count);
        Assert.True(rows[0].Use);
        Assert.Equal("Home", rows[0].FriendlyName);
        Assert.Equal("500", rows[0].LimitKbps);
        Assert.Equal("Ethernet (192.168.1.5)", rows[0].AdapterDisplay);
        Assert.Equal(AllowancePeriod.Monthly, rows[1].Period); // matched by name, case-insensitive
        Assert.False(rows[1].Use);
        Assert.False(rows[2].Use);
        Assert.Equal("Tether (not connected)", rows[3].AdapterDisplay);
        Assert.True(rows[3].Use);
    }

    [Fact]
    public void ToConfigs_KeepsTickedOrCustomizedRows_AndOnlyTickedAreEnabled()
    {
        var rows = new List<NetworkRowViewModel>
        {
            new() { AdapterId = "a", AdapterName = "A", Use = true },
            new() { AdapterId = "b", AdapterName = "B" },
            new() { AdapterId = "c", AdapterName = "C", LimitKbps = "-5", AllowanceGb = "abc", FriendlyName = "  " },
            new() { AdapterId = "d", AdapterName = "D", AllowanceGb = 2.5.ToString(System.Globalization.CultureInfo.CurrentCulture), Period = AllowancePeriod.Daily },
            new() { AdapterId = "e", AdapterName = "E", FriendlyName = " Phone " },
        };

        var cfgs = NetworkRowViewModel.ToConfigs(rows);

        Assert.Equal(["a", "d", "e"], cfgs.Select(c => c.AdapterId));
        Assert.True(cfgs[0].Enabled);
        Assert.False(cfgs[1].Enabled);
        Assert.Equal(2.5, cfgs[1].AllowanceGb);
        Assert.Equal(AllowancePeriod.Daily, cfgs[1].Period);
        Assert.Equal("Phone", cfgs[2].FriendlyName);
        Assert.Equal("E", cfgs[2].AdapterName);
    }
}
