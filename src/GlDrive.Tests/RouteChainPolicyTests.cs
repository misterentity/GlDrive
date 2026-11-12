using GlDrive.Config;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

public class RouteChainPolicyTests
{
    private static readonly ServerConfig[] Servers =
    [
        new() { Id = "a1", Name = "sitea" },
        new() { Id = "b2", Name = "siteb" },
        new() { Id = "c3", Name = "sitec" },
        new() { Id = "d4", Name = "sited" }
    ];

    private static RouteChainPolicy? Chain(params string[] chain) =>
        RouteChainPolicy.Create(new SpreadConfig { UseRouteChain = true, RouteChain = [.. chain] }, Servers);

    [Fact]
    public void Next_hop_outranks_skipping_ahead_and_backwards_earns_nothing()
    {
        var chain = Chain("sitea", "siteb", "sitec")!;

        Assert.Equal(RouteChainPolicy.MaxScore, chain.Score("a1", "b2"));
        Assert.Equal(RouteChainPolicy.MaxScore, chain.Score("b2", "c3"));
        Assert.Equal(RouteChainPolicy.MaxScore / 2, chain.Score("a1", "c3"));
        Assert.Equal(0, chain.Score("b2", "a1"));
        Assert.Equal(0, chain.Score("a1", "d4"));
        Assert.Equal(0, chain.Score("d4", "a1"));
    }

    [Fact]
    public void Sites_resolve_by_name_or_id_ignoring_case_and_unknown_entries()
    {
        var chain = Chain("SITEA", "nosuchsite", "b2", " sitec ")!;

        Assert.Equal(RouteChainPolicy.MaxScore, chain.Score("a1", "b2"));
        Assert.Equal(RouteChainPolicy.MaxScore, chain.Score("b2", "c3"));
    }

    [Theory]
    [InlineData(false, "sitea", "siteb")]
    [InlineData(true, "sitea")]
    [InlineData(true, "sitea", "nosuchsite")]
    [InlineData(true)]
    public void No_policy_when_off_or_fewer_than_two_known_sites(bool enabled, params string[] chain) =>
        Assert.Null(RouteChainPolicy.Create(new SpreadConfig { UseRouteChain = enabled, RouteChain = [.. chain] }, Servers));

    [Theory]
    [InlineData("sitea, siteb,sitec", "sitea|siteb|sitec")]
    [InlineData("sitea > siteb > sitec", "sitea|siteb|sitec")]
    [InlineData("sitea -> siteb; siteb", "sitea|siteb")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Chain_text_is_parsed_leniently(string? text, string expected) =>
        Assert.Equal(expected, string.Join("|", RouteChainPolicy.Parse(text)));

    [Fact]
    public void Chain_replaces_measured_speed_in_the_transfer_score()
    {
        var speeds = new SpeedTracker();
        speeds.RecordTransfer("a1", "c3", 100_000_000, TimeSpan.FromSeconds(1));   // fast direct route
        speeds.RecordTransfer("a1", "b2", 1_000_000, TimeSpan.FromSeconds(1));     // slow first hop
        var file = new SpreadFileInfo { Name = "release.r00", Size = 50 };

        int Score(SpreadScorer scorer, string dst) => scorer.Score(file, "a1", dst, SitePriority.Normal, 0.5,
            100, 100_000_000, TimeSpan.FromSeconds(30), SpreadMode.Race);

        var bySpeed = new SpreadScorer(speeds);
        Assert.True(Score(bySpeed, "c3") > Score(bySpeed, "b2"));

        var byChain = new SpreadScorer(speeds, Chain("sitea", "siteb", "sitec"));
        Assert.True(Score(byChain, "b2") > Score(byChain, "c3"));
    }

    [Fact]
    public void Sfv_still_goes_first_regardless_of_the_chain()
    {
        var scorer = new SpreadScorer(new SpeedTracker(), Chain("sitea", "siteb", "sitec"));
        var sfv = new SpreadFileInfo { Name = "release.sfv", Size = 1 };

        Assert.Equal(65535, scorer.Score(sfv, "c3", "a1", SitePriority.Normal, 0, 100, 1, TimeSpan.Zero, SpreadMode.Race));
    }
}
