using System.Text.Json;
using GlDrive.Downloads;
using GlDrive.Services.Automation;
using GlDrive.UI;
using GlDrive.Library;
using Xunit;

namespace GlDrive.Tests;

public sealed class CollectionAutomationTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"movieTmdbId\":0}")]
    [InlineData("{\"movieTmdbId\":-1}")]
    [InlineData("{\"movieTmdbId\":2147483648}")]
    [InlineData("{\"movieTmdbId\":1.5}")]
    [InlineData("{\"movieTmdbId\":\"1\"}")]
    [InlineData("{\"movieTmdbId\":null}")]
    public async Task Invalid_ids_are_rejected_before_any_service_or_provider_access(string json)
    {
        var calls = 0;
        var automation = new CollectionAutomation(() => { calls++; throw new InvalidOperationException(); });
        using var args = JsonDocument.Parse(json);
        await Assert.ThrowsAsync<ArgumentException>(() => automation.ExecuteAsync("collections.refresh", args.RootElement, default));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Add_requires_both_ids_before_accessing_service()
    {
        var calls = 0;
        var automation = new CollectionAutomation(() => { calls++; throw new InvalidOperationException(); });
        using var args = JsonDocument.Parse("{\"movieTmdbId\":1}");
        await Assert.ThrowsAsync<ArgumentException>(() => automation.ExecuteAsync("collections.add", args.RootElement, default));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Cancelled_or_unknown_operation_does_not_initialize_collection_work()
    {
        var calls = 0;
        var automation = new CollectionAutomation(() => { calls++; throw new InvalidOperationException(); });
        using var args = JsonDocument.Parse("{}");
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => automation.ExecuteAsync("collections.status", args.RootElement, stop.Token));
        await Assert.ThrowsAsync<ArgumentException>(() => automation.ExecuteAsync("collections.delete", args.RootElement, default));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("owned", false, "Owned")]
    [InlineData("wanted", false, "Wanted")]
    [InlineData("downloading", false, "Downloading")]
    [InlineData("missing", true, "Missing")]
    [InlineData("unknown", false, "Unknown")]
    [InlineData("unexpected", false, "Unknown")]
    public void Local_ui_add_only_offers_confirmed_missing_members(string state, bool canAdd, string label)
    {
        var row = new CollectionFilmRow(new CollectionGapFilm(1, "Synthetic Film", 2026, state));
        Assert.Equal(canAdd, row.CanAdd); Assert.Equal(label, row.DisplayStatus);
        Assert.Equal(1, row.TmdbId); Assert.Equal("Synthetic Film", row.Title); Assert.Equal(2026, row.Year);
    }

    [Fact]
    public async Task Automation_and_ui_reject_undated_collection_members()
    {
        var store = new WishlistStore(() => null, _ => { });
        var service = new CollectionGapService(new(), store, () => new(DateTime.UtcNow, [], 1, 0, 0, true), () => [],
            (_, _) => Task.FromResult(new TmdbCollectionResult("available", new(9, "Synthetic Collection", [new(2, "Synthetic Reboot", null)]))));
        var automation = new CollectionAutomation(() => service);
        using var args = JsonDocument.Parse("{\"movieTmdbId\":1,\"filmTmdbId\":2}");
        await automation.ExecuteAsync("collections.refresh", args.RootElement, default);
        var result = Assert.IsType<CollectionGapAddResult>(await automation.ExecuteAsync("collections.add", args.RootElement, default));
        Assert.Equal("year-required", result.Status);
        Assert.Empty(store.Items);
        Assert.False(new CollectionFilmRow(new(2, "Synthetic Reboot", null, "missing")).CanAdd);
        Assert.False(new CollectionFilmRow(new(2, "Synthetic Reboot", null, "unknown")).CanAdd);
    }

    [Fact]
    public async Task Automation_cannot_bypass_unknown_ownership_guard()
    {
        var store = new WishlistStore(() => null, _ => { });
        var service = new CollectionGapService(new(), store, () => LibraryIndexSnapshot.Empty, () => [],
            (_, _) => Task.FromResult(new TmdbCollectionResult("available", new(9, "Synthetic Collection", [new(2, "Synthetic Film", 2026)]))));
        await service.RefreshAsync(1);
        var automation = new CollectionAutomation(() => service);
        using var args = JsonDocument.Parse("{\"movieTmdbId\":1,\"filmTmdbId\":2}");
        var result = Assert.IsType<CollectionGapAddResult>(await automation.ExecuteAsync("collections.add", args.RootElement, default));
        Assert.Equal("ownership-unknown", result.Status);
        Assert.Empty(store.Items);
    }
}
