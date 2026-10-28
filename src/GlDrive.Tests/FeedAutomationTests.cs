using System.Text.Json;
using GlDrive.Downloads;
using GlDrive.Services.Automation;
using Xunit;

namespace GlDrive.Tests;

public sealed class FeedAutomationTests
{
    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public async Task Named_filters_are_shared_with_notifications_and_marking_read_is_explicit()
    {
        var notifications = new NotificationStore(persistToDisk: false);
        notifications.Add(new() { Id = "first", ServerId = "one", ServerName = "First", Category = "Video", ReleaseName = "Film.2160p", Timestamp = DateTime.UtcNow });
        notifications.Add(new() { Id = "other", ServerId = "two", ServerName = "Second", Category = "Audio", ReleaseName = "Album" });
        var module = new FeedAutomation(notifications);
        var saved = Assert.IsType<FeedView>(await module.ExecuteAsync("feed.save", Args(new { name = "UHD", text = "2160P", category = "Video", serverName = "First" }), default));
        Assert.Equal(1, notifications.FeedViews.Counts(notifications.Items).Single().Unread);
        await module.ExecuteAsync("feed.views", Args(new { }), default);
        Assert.Equal(1, notifications.FeedViews.Counts(notifications.Items).Single().Unread);
        await module.ExecuteAsync("feed.mark_read", Args(new { id = saved.Id }), default);
        Assert.Equal(0, notifications.FeedViews.Counts(notifications.Items).Single().Unread);
        notifications.Add(new() { Id = "delayed", ServerId = "one", ServerName = "First", Category = "Video", ReleaseName = "Older.2160p", Timestamp = DateTime.UtcNow.AddDays(-1) });
        Assert.Equal(1, notifications.FeedViews.Counts(notifications.Items).Single().Unread);
    }

    [Fact]
    public async Task Muting_changes_only_visible_counts_and_preserves_underlying_feed_and_unread()
    {
        var notifications = new NotificationStore(persistToDisk: false);
        var first = new NotificationItem { Id = "one", ServerId = "site-a", Category = "Video", ReleaseName = "Movie" };
        var second = new NotificationItem { Id = "two", ServerId = "site-b", Category = "Video", ReleaseName = "Movie" };
        notifications.Add(first); notifications.Add(second);
        var module = new FeedAutomation(notifications);
        var saved = Assert.IsType<FeedView>(await module.ExecuteAsync("feed.save", Args(new { name = "All" }), default));
        await module.ExecuteAsync("feed.mute", Args(new { serverId = "site-a", category = "VIDEO", muted = true }), default);
        Assert.Equal(1, notifications.FeedViews.Counts(notifications.Items).Single().Unread);
        Assert.Equal(2, notifications.Items.Count);
        Assert.Contains(first, notifications.Items);
        await module.ExecuteAsync("feed.mark_read", Args(new { id = saved.Id }), default);
        await module.ExecuteAsync("feed.mute", Args(new { serverId = "site-a", category = "video", muted = false }), default);
        Assert.Equal(1, notifications.FeedViews.Counts(notifications.Items).Single().Unread);
        await module.ExecuteAsync("feed.remove", Args(new { id = saved.Id }), default);
        Assert.Empty(notifications.FeedViews.Views);
        Assert.Equal(2, notifications.Items.Count);
    }

    [Fact]
    public async Task Replacing_a_view_preserves_read_markers_and_detaches_returned_lists()
    {
        var notifications = new NotificationStore(persistToDisk: false);
        notifications.Add(new() { Id = "one", ReleaseName = "Film" });
        var module = new FeedAutomation(notifications);
        var saved = Assert.IsType<FeedView>(await module.ExecuteAsync("feed.save", Args(new { name = "Films" }), default));
        await module.ExecuteAsync("feed.mark_read", Args(new { id = saved.Id }), default);
        var updated = Assert.IsType<FeedView>(await module.ExecuteAsync("feed.save", Args(new { id = saved.Id, name = "Cinema", text = "Film" }), default));
        Assert.Equal("Cinema", notifications.FeedViews.Views.Single().Name);
        Assert.Equal(0, notifications.FeedViews.Counts(notifications.Items).Single().Unread);
        Assert.IsType<string[]>(updated.ReadIds)[0] = "changed";
        Assert.Equal("one", notifications.FeedViews.Views.Single().ReadIds.Single());
    }

    [Fact]
    public async Task Cancellation_prevents_a_preference_change()
    {
        var notifications = new NotificationStore(persistToDisk: false);
        var module = new FeedAutomation(notifications);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => module.ExecuteAsync("feed.save", Args(new { name = "New" }), stop.Token));
        Assert.Empty(notifications.FeedViews.Views);
    }

    [Fact]
    public void Displayed_subset_does_not_mark_pending_arrivals_read_or_forget_previous_markers()
    {
        var store = new FeedViewStore(() => null, _ => { });
        var view = store.Save(null, "Films");
        var first = new NotificationItem { Id = "first" };
        var second = new NotificationItem { Id = "second" };
        var pending = new NotificationItem { Id = "pending" };
        store.MarkRead(view.Id, [first]);
        store.MarkRead(view.Id, [first, second, pending], [second.Id]);
        Assert.Equal(1, store.Counts([first, second, pending]).Single().Unread);
        Assert.Equal(new[] { "first", "second" }, store.Views.Single().ReadIds.Order().ToArray());
    }

    [Fact]
    public async Task Catalog_validates_mute_intent_before_mutation()
    {
        var notifications = new NotificationStore(persistToDisk: false);
        var module = new FeedAutomation(notifications);
        using var registry = new AutomationRegistry(module);
        Assert.True(module.Tools.Single(t => t.Name == "feed.views").ReadOnly);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => registry.CallAsync("feed.mute", Args(new { serverId = "one", category = "Video", muted = "yes" }), default));
        Assert.Empty(notifications.FeedViews.MutedCategories);
    }
}
