using GlDrive.Services.Automation;
using Xunit;

namespace GlDrive.Tests;

public sealed class BulkUiAutomationTests
{
    [Fact]
    public void Invoke_advertises_destructive_hint_for_persistent_bulk_commands()
    {
        var tool = new UiAutomation(() => throw new InvalidOperationException("No desktop needed"))
            .Tools.Single(tool => tool.Name == "ui.invoke");
        Assert.True(tool.Destructive);
        var protocol = System.Text.Json.JsonSerializer.SerializeToElement(tool.ToProtocol());
        Assert.True(protocol.GetProperty("annotations").GetProperty("destructiveHint").GetBoolean());
    }

    private sealed record Row(string Id);
    private sealed class Model
    {
        public List<Row> WishlistItems { get; } = [new("first"), new("second")];
        public IEnumerable<Row>? SelectedWishlistItems { get; set; }
    }

    [Fact]
    public void ExactIdsSurviveReorderingAndDoNotMutateModel()
    {
        var model = new Model();
        model.WishlistItems.Reverse();
        var rows = UiAutomation.ResolveMany(model, "dashboard", "SelectedWishlistItems", "WishlistItems", ["first", "second"]);
        Assert.Equal(["first", "second"], rows.Cast<Row>().Select(row => row.Id));
        Assert.Null(model.SelectedWishlistItems);
        Assert.Empty(UiAutomation.ResolveMany(model, "dashboard", "SelectedWishlistItems", "WishlistItems", []));
    }

    [Theory]
    [InlineData("missing", "second")]
    [InlineData("first", "first")]
    [InlineData("FIRST", "second")]
    [InlineData("", "second")]
    public void InvalidIdsRejectWholeSelection(string first, string second) => Assert.Throws<ArgumentException>(() =>
        UiAutomation.ResolveMany(new Model(), "dashboard", "SelectedWishlistItems", "WishlistItems", [first, second]));

    [Fact]
    public void UnsupportedMappingsAndExcessiveSelectionsAreRejected()
    {
        var model = new Model();
        Assert.Throws<ArgumentException>(() => UiAutomation.ResolveMany(model, "spread", "SelectedWishlistItems", "WishlistItems", ["first"]));
        Assert.Throws<ArgumentException>(() => UiAutomation.ResolveMany(model, "dashboard", "SelectedWishlistItems", "WishlistItems", Enumerable.Range(0, 201).Select(i => i.ToString()).ToArray()));
    }

    [Fact]
    public void DuplicateSourceIdentityIsRejected()
    {
        var model = new Model();
        model.WishlistItems.Add(new("first"));
        Assert.Throws<ArgumentException>(() => UiAutomation.ResolveMany(model, "dashboard", "SelectedWishlistItems", "WishlistItems", ["first"]));
    }
}
