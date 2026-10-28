using System.IO;
using System.Text;
using System.Text.Json;
using GlDrive.Downloads;
using Xunit;

namespace GlDrive.Tests;

public sealed class FeedViewStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-feed-" + Guid.NewGuid().ToString("N"));
    private string StateFile => Path.Combine(_root, "feed-views.json");
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static NotificationItem Item(string id, string category = "movies", string server = "site", DateTime? at = null) => new()
    {
        Id = id, Category = category, ServerId = server, ServerName = server,
        ReleaseName = "Example.Film.2026.2160p.WEB-GROUP", Timestamp = at ?? Now
    };
    private static FeedViewStore Memory() => new(() => null, _ => { });
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public void FiltersReadIdsAndMutesSurviveRestart()
    {
        var store = FeedViewStore.ForFile(StateFile);
        var view = store.Save(null, "Films", "2160p", "movies", "site");
        store.MarkRead(view.Id, [Item("read")]);
        store.SetMuted("site", "games", true);
        var restored = FeedViewStore.ForFile(StateFile);
        Assert.Equal(view.Id, Assert.Single(restored.Views).Id);
        Assert.True(FeedViewStore.IsMuted(restored.MutedCategories, "site", "GAMES"));
        var count = Assert.Single(restored.Counts([Item("read"), Item("new"), Item("other", "tv"), Item("remote", server: "other")]));
        Assert.Equal(2, count.Visible);
        Assert.Equal(1, count.Unread);
    }

    [Fact]
    public void EqualAndOlderTimestampArrivalsRemainUnreadUntilExplicitlyMarked()
    {
        var store = Memory();
        var view = store.Save(null, "Everything");
        store.MarkRead(view.Id, [Item("existing")]);
        var rows = new[] { Item("existing"), Item("same-time"), Item("older", at: Now.AddDays(-1)) };
        Assert.Equal(2, Assert.Single(store.Counts(rows)).Unread);
        store.MarkRead(view.Id, rows);
        Assert.Equal(0, Assert.Single(store.Counts(rows)).Unread);
        Assert.Equal(1, Assert.Single(store.Counts(rows.Append(Item("after-mark")))).Unread);
    }

    [Fact]
    public void MutesAreServerScopedAndDoNotDeleteFeedOrMarkHiddenRowsRead()
    {
        var notifications = new NotificationStore(persistToDisk: false);
        notifications.Add(Item("first")); notifications.Add(Item("second", server: "other"));
        var store = notifications.FeedViews;
        var view = store.Save(null, "Everything");
        store.SetMuted("site", "MOVIES", true);
        Assert.Equal(2, notifications.Items.Count);
        Assert.Equal(1, Assert.Single(store.Counts(notifications.Items)).Visible);
        store.MarkRead(view.Id, notifications.Items);
        store.ClearMutes();
        Assert.Equal(2, Assert.Single(store.Counts(notifications.Items)).Visible);
        Assert.Equal(1, Assert.Single(store.Counts(notifications.Items)).Unread);
        Assert.DoesNotContain("first", Assert.Single(store.Views).ReadIds);
    }

    [Fact]
    public void ReadMarksAreDetachedAndPrunedToRetainedHistory()
    {
        var store = Memory();
        var view = store.Save(null, "Everything");
        store.MarkRead(view.Id, [Item("old"), Item("kept")]);
        var detached = Assert.Single(store.Views);
        ((string[])detached.ReadIds)[0] = "mutated";
        Assert.DoesNotContain("mutated", Assert.Single(store.Views).ReadIds);
        store.MarkRead(view.Id, [Item("kept"), Item("new")]);
        Assert.Equal(new[] { "kept", "new" }, Assert.Single(store.Views).ReadIds.Order());
    }

    [Fact]
    public void BrokenStateIsCopiedAndStartsEmpty()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(StateFile, "broken preferences");
        var store = FeedViewStore.ForFile(StateFile);
        Assert.Empty(store.Views); Assert.Empty(store.MutedCategories);
        Assert.Equal("broken preferences", File.ReadAllText(StateFile + ".corrupt"));
        store.Save(null, "Recovered");
        Assert.Equal("Recovered", Assert.Single(FeedViewStore.ForFile(StateFile).Views).Name);
    }

    [Fact]
    public void WriteAndObserverFailuresPreserveMemoryAndDoNotStopLaterObservers()
    {
        var writes = 0;
        var changes = 0;
        var store = new FeedViewStore(() => null, _ => { writes++; throw new IOException(); });
        store.Changed += () => throw new IOException();
        store.Changed += () => changes++;
        var view = store.Save(null, "Local");
        store.SetMuted("site", "movies", true);
        Assert.Equal(view.Id, Assert.Single(store.Views).Id);
        Assert.Single(store.MutedCategories);
        Assert.Equal(2, writes); Assert.Equal(2, changes);
    }

    [Fact]
    public void CountCapacityKeepsNewWorkEvenWhenRecordedDatesAreInTheFuture()
    {
        var future = DateTime.UtcNow.AddYears(1);
        var views = Enumerable.Range(0, FeedViewStore.MaximumViews)
            .Select(i => new FeedView("old" + i, "Old " + i, "", "All", "All", future.AddMinutes(i), [])).ToArray();
        var mutes = Enumerable.Range(0, FeedViewStore.MaximumMutes)
            .Select(i => new FeedCategoryMute("site", "old" + i, future.AddMinutes(i))).ToArray();
        var json = JsonSerializer.Serialize(new { views, mutedCategories = mutes }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var store = new FeedViewStore(() => json, _ => { });
        var added = store.Save(null, "New");
        store.SetMuted("site", "new-category", true);
        Assert.Equal(FeedViewStore.MaximumViews, store.Views.Count);
        Assert.Contains(store.Views, v => v.Id == added.Id);
        Assert.DoesNotContain(store.Views, v => v.Id == "old0");
        Assert.Equal(FeedViewStore.MaximumMutes, store.MutedCategories.Count);
        Assert.Contains(store.MutedCategories, m => m.Category == "new-category");
        Assert.DoesNotContain(store.MutedCategories, m => m.Category == "old0");
    }

    [Fact]
    public void LargestReadMarkersProduceReloadableFilesByEvictingOldPreferences()
    {
        var store = FeedViewStore.ForFile(StateFile);
        var rows = Enumerable.Range(0, FeedViewStore.MaximumReadIds)
            .Select(i => Item(new string('\u4e00', 124) + i.ToString("D4"))).ToArray();
        FeedView? latest = null;
        for (var i = 0; i < 3; i++)
        {
            latest = store.Save(null, "View " + i);
            store.MarkRead(latest.Id, rows);
            Assert.True(new FileInfo(StateFile).Length <= FeedViewStore.MaximumBytes);
        }
        var restored = FeedViewStore.ForFile(StateFile);
        Assert.Contains(restored.Views, v => v.Id == latest!.Id);
        Assert.Equal(0, restored.Counts(rows).Single(c => c.View.Id == latest!.Id).Unread);
        Assert.False(File.Exists(StateFile + ".corrupt"));
        Assert.True(Encoding.UTF8.GetByteCount(File.ReadAllText(StateFile)) <= FeedViewStore.MaximumBytes);
    }
}
