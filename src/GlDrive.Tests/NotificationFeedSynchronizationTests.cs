using System.Collections.Specialized;
using GlDrive.Downloads;
using GlDrive.UI;
using Xunit;

namespace GlDrive.Tests;

public sealed class NotificationFeedSynchronizationTests
{
    [Fact]
    public void Authoritative_empty_snapshot_removes_a_stale_add_after_clear()
    {
        var projection = new NotificationProjection();
        var stale = Item("old");
        projection.Reset([stale]);
        projection.Synchronize([]);
        // A delayed event only asks for the latest snapshot; its old item is not replayed.
        projection.Synchronize([]);
        Assert.Empty(projection.Items);
        Assert.Equal(new[] { "All" }, projection.Categories);
        Assert.Equal(new[] { "All" }, projection.Servers);
        projection.Synchronize([Item("new")]);
        Assert.Equal("new", Assert.Single(projection.Items).Id);
    }

    [Fact]
    public void Snapshot_restores_store_order_after_out_of_order_add_notifications()
    {
        var projection = new NotificationProjection();
        var first = Item("first");
        var second = Item("second");
        var third = Item("third");
        projection.Add(third);
        projection.Add(first);
        projection.Add(second);
        var actions = new List<NotifyCollectionChangedAction>();
        projection.Items.CollectionChanged += (_, e) => actions.Add(e.Action);
        projection.Synchronize([Item("third"), Item("second"), Item("first")]);
        Assert.Equal(new[] { "third", "second", "first" }, projection.Items.Select(i => i.Id));
        Assert.Same(third, projection.Items[0]);
        Assert.Same(second, projection.Items[1]);
        Assert.Same(first, projection.Items[2]);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, actions);
    }

    [Fact]
    public void Refresh_preserves_selected_row_reference_and_loaded_metadata()
    {
        var projection = new NotificationProjection();
        var selected = Item("selected");
        selected.MetadataLoaded = true;
        selected.PosterUrl = "https://images.example.invalid/poster.jpg";
        selected.Plot = "Fixture plot";
        projection.Reset([selected, Item("removed")]);
        projection.Synchronize([Item("arrival"), Item("selected")]);
        Assert.Same(selected, projection.Items[1]);
        Assert.True(projection.Items[1].MetadataLoaded);
        Assert.Equal("Fixture plot", projection.Items[1].Plot);
        Assert.Equal(selected.PosterUrl, projection.Items[1].PosterUrl);
        Assert.DoesNotContain(projection.Items, row => row.Id == "removed");
    }

    [Fact]
    public void Feed_mute_changes_only_projection_and_is_specific_to_server_and_category()
    {
        var projection = new NotificationProjection();
        var muted = Item("muted", "Video", "first");
        var sameCategoryElsewhere = Item("elsewhere", "Video", "second");
        var differentCategory = Item("other", "Audio", "first");
        var source = new[] { muted, sameCategoryElsewhere, differentCategory };
        projection.Reset(source);
        projection.SetMutedCategories([new("first", "VIDEO", DateTime.UtcNow)]);
        projection.Synchronize(source);
        Assert.Equal(new[] { "elsewhere", "other" }, projection.Items.Select(row => row.Id));
        Assert.Equal(new[] { "muted", "elsewhere", "other" }, source.Select(row => row.Id));
        Assert.Equal("Video", muted.Category);
        Assert.Equal("first", muted.ServerId);
        projection.SetMutedCategories([]);
        Assert.Equal(source, projection.Items);
        Assert.Same(muted, projection.Items[0]);
    }

    [Fact]
    public void Selected_filter_options_survive_authoritative_eviction_until_filter_changes()
    {
        var projection = new NotificationProjection(2);
        projection.Reset([Item("old", "OldCategory", "OldServer")]);
        projection.SetFilter("", "OldCategory", "OldServer");
        projection.Synchronize([Item("new", "Current", "CurrentServer")]);
        Assert.Empty(projection.Items);
        Assert.Equal(new[] { "All", "Current", "OldCategory" }, projection.Categories);
        Assert.Equal(new[] { "All", "CurrentServer", "OldServer" }, projection.Servers);
        projection.Synchronize([]);
        Assert.Equal(new[] { "All", "OldCategory" }, projection.Categories);
        Assert.Equal(new[] { "All", "OldServer" }, projection.Servers);
        projection.SetFilter("", "All", "All");
        Assert.Equal(new[] { "All" }, projection.Categories);
        Assert.Equal(new[] { "All" }, projection.Servers);
    }

    [Fact]
    public void Repeated_synchronization_respects_capacity_and_filter_without_duplicate_rows_or_options()
    {
        var projection = new NotificationProjection(3);
        projection.SetFilter("KEEP", "Video", "Server");
        var snapshot = new[] { Item("keep-new"), Item("other"), Item("keep-middle"), Item("keep-too-old") };
        projection.Synchronize(snapshot);
        var selected = projection.Items[1];
        var mutations = 0;
        projection.Items.CollectionChanged += (_, _) => mutations++;
        projection.Synchronize(snapshot.Select(row => Item(row.Id)));
        Assert.Equal(new[] { "keep-new", "keep-middle" }, projection.Items.Select(row => row.Id));
        Assert.Same(selected, projection.Items[1]);
        Assert.Equal(0, mutations);
        Assert.Equal(new[] { "All", "Video" }, projection.Categories);
        Assert.Equal(new[] { "All", "Server" }, projection.Servers);
        projection.SetFilter("", "All", "All");
        Assert.Equal(new[] { "keep-new", "other", "keep-middle" }, projection.Items.Select(row => row.Id));
    }

    private static NotificationItemVm Item(string id, string category = "Video", string server = "Server") => new()
    {
        Id = id, ReleaseName = id, Category = category, ServerId = server, ServerName = server,
        RemotePath = "/fixture/" + id
    };
}
