using System.Text.Json;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Services.Operations;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

public sealed class ReleasePredictionTests
{
    private const string Release = "Example.Movie.2026.1080p.WEB.H264-TEST";
    private static WishlistItem Movie() => new() { Title = "Example Movie", Year = 2026, Type = MediaType.Movie };

    [Fact]
    public void Repeated_prediction_does_not_mutate_configuration_or_create_acquisition_claims()
    {
        var config = new AppConfig { Servers = [new() { Id = "source", SpreadSite = new() { Sections = new() { ["movies"] = "/movies" } } }] };
        config.Downloads.AutoDownloadWishlist = true; config.Downloads.ScoredQualityEnabled = true;
        var item = Movie(); var beforeConfig = JsonSerializer.Serialize(config); var beforeItem = JsonSerializer.Serialize(item);
        for (var i = 0; i < 2; i++)
        {
            var snapshot = ReleasePrediction.Capture(config);
            var result = ReleasePrediction.Evaluate(snapshot, [item], false, Release, "movies", _ => { }, default);
            Assert.Contains(result, row => row.Stage == "wishlist" && row.Verdict == "conditional");
        }
        Assert.Equal(beforeConfig, JsonSerializer.Serialize(config)); Assert.Equal(beforeItem, JsonSerializer.Serialize(item));
        Assert.Empty(item.PendingGrabs); Assert.Empty(item.GrabbedReleases);
    }

    [Fact]
    public void Missing_claims_are_unknown_and_never_predicted_as_allowed()
    {
        var config = new AppConfig(); config.Downloads.AutoDownloadWishlist = true;
        var result = ReleasePrediction.Evaluate(config, [Movie()], true, Release, "movies", _ => { }, default);
        Assert.Equal("unknown", Assert.Single(result, row => row.Stage == "wishlist").Verdict);
    }

    [Fact]
    public void Sibling_claims_consume_the_scoring_budget_even_without_custom_formats()
    {
        var config = new AppConfig(); config.Downloads.AutoDownloadWishlist = true; config.Downloads.ScoredQualityEnabled = true;
        var sibling = Movie(); sibling.GrabbedReleases = Enumerable.Range(0, 2100).Select(i => "Example.Movie.2026.1080p.WEB.H264-" + i).ToList();
        var reasons = new List<string>();
        var result = ReleasePrediction.Evaluate(config, [Movie(), sibling], false, Release, "movies", reasons.Add, default);
        Assert.All(result.Where(row => row.Stage == "wishlist"), row => Assert.Equal("unknown", row.Verdict));
        Assert.Contains("prediction-work-limit", reasons);
    }

    [Fact]
    public void Oversized_rule_scope_is_unknown_instead_of_evaluating_a_truncated_gate()
    {
        var config = new AppConfig { Servers = [new() { Id = "source", SpreadSite = new() { Skiplist = Enumerable.Range(0, 129).Select(_ => new SkiplistRule { Pattern = "*", Action = SkiplistAction.Allow }).ToList() } }] };
        var result = ReleasePrediction.Evaluate(ReleasePrediction.Capture(config), [], false, Release, "movies", _ => { }, default);
        Assert.Equal("unknown", Assert.Single(result).Verdict);
    }

    [Fact]
    public void Realistic_multisite_policy_produces_verdicts_and_routes_without_mutating_inputs()
    {
        var config = new AppConfig { Servers = Enumerable.Range(0, 4).Select(index => new ServerConfig
        {
            Id = "site-" + index, Enabled = index != 3,
            SpreadSite = new()
            {
                DownloadOnly = index == 1,
                Sections = new() { ["movies"] = "/movies" },
                Skiplist = Enumerable.Range(0, 100).Select(rule => new SkiplistRule
                    { Pattern = rule == 99 && index == 2 ? "*" : "Unrelated-" + rule, Action = SkiplistAction.Deny }).ToList()
            }
        }).ToList() };
        var before = JsonSerializer.Serialize(config);
        var reasons = new List<string>();
        var result = ReleasePrediction.Evaluate(ReleasePrediction.Capture(config), [], false, Release, "movies", reasons.Add, default);
        Assert.Equal("conditional", Assert.Single(result, row => row.ServerId == "site-0").Verdict);
        Assert.Contains("excluded by download-only", Assert.Single(result, row => row.ServerId == "site-1").Detail);
        Assert.Equal("denied-by-config", Assert.Single(result, row => row.ServerId == "site-2").Verdict);
        Assert.Equal("disabled", Assert.Single(result, row => row.ServerId == "site-3").Verdict);
        Assert.DoesNotContain("prediction-work-limit", reasons);
        Assert.Equal(before, JsonSerializer.Serialize(config));
    }

    [Fact]
    public void A_single_site_cannot_run_past_the_cooperative_preview_budget()
    {
        var config = new AppConfig { Servers = [new() { Id = "source", SpreadSite = new()
            { Skiplist = Enumerable.Range(0, 100).Select(index => new SkiplistRule { Pattern = "Unrelated-" + index }).ToList() } }] };
        var checks = 0; var reasons = new List<string>();
        var result = ReleasePrediction.Evaluate(ReleasePrediction.Capture(config), [], false, Release, "movies", reasons.Add, default,
            budgetExceeded: () => ++checks > 5);
        Assert.Equal("unknown", Assert.Single(result, row => row.Stage == "site-rules").Verdict);
        Assert.Contains("prediction-work-limit", reasons);
        Assert.InRange(checks, 6, 7);
    }

    [Fact]
    public void Unloaded_live_snapshot_never_invokes_the_read_or_save_delegate()
    {
        var reads = 0; var writes = 0;
        var store = new WishlistStore(() => { reads++; return "[]"; }, _ => writes++);
        var result = store.ReadDiagnosticSnapshot();
        Assert.True(result.Partial); Assert.Empty(result.Items); Assert.Equal(0, reads); Assert.Equal(0, writes);
    }

    [Fact]
    public void Wishlist_copy_bounds_nested_claims_and_omits_irrelevant_private_fields()
    {
        var item = Movie(); item.Plot = new string('p', 200_000); item.PosterUrl = "https://invalid.example/fixture";
        item.BestOwned = [new() { Key = "movie", ReleaseName = Release, LocalPath = "private-fixture-path" }];
        var budget = 1024; var copy = DossierWishlistSnapshot.Copy(item, ref budget);
        Assert.NotNull(copy); Assert.Null(copy.Plot); Assert.Null(copy.PosterUrl); Assert.Null(Assert.Single(copy.BestOwned).LocalPath);
        item.PendingGrabs = Enumerable.Range(0, 129).Select(_ => new WishlistCopy()).ToList();
        Assert.Null(DossierWishlistSnapshot.Copy(item, ref budget));
        item.PendingGrabs.Clear(); item.GrabbedReleases = [null!]; Assert.Null(DossierWishlistSnapshot.Copy(item, ref budget));
    }
}
