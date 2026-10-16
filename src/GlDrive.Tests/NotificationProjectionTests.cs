using System.Collections.Specialized;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using GlDrive.UI;
using Xunit;

namespace GlDrive.Tests;

[Collection("WpfRender")]
public sealed class NotificationProjectionTests
{
    [Fact]
    public void Burst_at_capacity_preserves_bound_grid_selection_current_item_and_emits_only_deltas() => OnSta(() =>
    {
        var projection = new NotificationProjection();
        var seed = Enumerable.Range(0, 5000).Select(i => Item($"seed-{i}")).ToArray();
        projection.Reset(seed);
        var grid = new DataGrid { ItemsSource = projection.Items, AutoGenerateColumns = false };
        var selected = seed[20];
        grid.SelectedItem = selected;
        var view = CollectionViewSource.GetDefaultView(projection.Items);
        Assert.True(view.MoveCurrentTo(selected));
        var changes = new List<NotifyCollectionChangedAction>();
        projection.Items.CollectionChanged += (_, e) => changes.Add(e.Action);
        var optionChanges = 0;
        projection.Categories.CollectionChanged += (_, _) => optionChanges++;
        projection.Servers.CollectionChanged += (_, _) => optionChanges++;
        for (var i = 0; i < 100; i++) projection.Add(Item($"arrival-{i}"));
        Assert.Equal(5000, projection.Items.Count);
        Assert.Equal(200, changes.Count);
        Assert.Equal(100, changes.Count(c => c == NotifyCollectionChangedAction.Add));
        Assert.Equal(100, changes.Count(c => c == NotifyCollectionChangedAction.Remove));
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, changes);
        Assert.Equal(0, optionChanges);
        Assert.Same(selected, grid.SelectedItem);
        Assert.Same(selected, view.CurrentItem);
        Assert.Equal("arrival-99", projection.Items[0].ReleaseName);
        Assert.Same(seed[4899], projection.Items[^1]);
        grid.ItemsSource = null;
    });

    [Fact]
    public void Filtered_arrivals_and_evictions_match_full_projection_without_resets()
    {
        var projection = new NotificationProjection(7);
        projection.SetFilter("KEEP", "Video", "Server");
        var newestFirst = new List<NotificationItemVm>();
        var resets = 0;
        projection.Items.CollectionChanged += (_, e) => { if (e.Action == NotifyCollectionChangedAction.Reset) resets++; };
        for (var i = 0; i < 40; i++)
        {
            var row = Item(i % 2 == 0 ? $"keep-{i}" : $"other-{i}",
                i % 3 == 0 ? "Other" : "Video", i % 5 == 0 ? "other" : "Server");
            newestFirst.Insert(0, row);
            if (newestFirst.Count > 7) newestFirst.RemoveAt(7);
            projection.Add(row);
            Assert.Equal(newestFirst.Where(n => n.ReleaseName.Contains("KEEP", StringComparison.OrdinalIgnoreCase)
                && n.Category == "Video" && n.ServerName == "Server"), projection.Items);
        }
        Assert.Equal(0, resets);
        projection.SetFilter("", "All", "All");
        Assert.Equal(newestFirst, projection.Items);
    }

    [Fact]
    public void Selected_filter_option_survives_eviction_and_reset_until_user_changes_filter() => OnSta(() =>
    {
        var projection = new NotificationProjection(2);
        projection.Add(Item("old", "Gone", "GoneServer"));
        projection.SetFilter("", "Gone", "GoneServer");
        var category = new ComboBox { ItemsSource = projection.Categories, SelectedItem = "Gone" };
        var server = new ComboBox { ItemsSource = projection.Servers, SelectedItem = "GoneServer" };
        projection.Add(Item("new", "Z", "Z"));
        projection.Add(Item("newest", "A", "A"));
        Assert.Empty(projection.Items);
        Assert.Equal("Gone", category.SelectedItem);
        Assert.Equal("GoneServer", server.SelectedItem);
        Assert.Equal(new[] { "All", "A", "Gone", "Z" }, projection.Categories);
        projection.Reset([]);
        Assert.Equal("Gone", category.SelectedItem);
        Assert.Equal("GoneServer", server.SelectedItem);
        projection.SetFilter("", "All", "All");
        Assert.Equal(new[] { "All" }, projection.Categories);
        Assert.Equal(new[] { "All" }, projection.Servers);
        category.ItemsSource = null;
        server.ItemsSource = null;
    });

    [Fact]
    public void Reset_respects_newest_first_order_and_capacity()
    {
        var projection = new NotificationProjection(3);
        var rows = Enumerable.Range(0, 5).Select(i => Item($"row-{i}")).ToArray();
        projection.Reset(rows);
        Assert.Equal(rows.Take(3), projection.Items);
        Assert.Same(rows[2], projection.Add(Item("new")));
        Assert.Equal(new[] { "new", "row-0", "row-1" }, projection.Items.Select(i => i.ReleaseName));
    }

    private static NotificationItemVm Item(string name, string category = "Video", string server = "Server") =>
        new() { ReleaseName = name, Category = category, ServerName = server, MetadataLoaded = true };

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Notification UI fixture did not drain");
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
