using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GlDrive.UI;
using Xunit;

namespace GlDrive.Tests;

public sealed class QueuedRaceSelectionTests
{
    [Fact]
    public void Real_listbox_selection_stays_exact_through_content_refresh_reorder_and_clear() => OnSta(() =>
    {
        var vm = Model();
        var rows = new[] { Row("first"), Row("second"), Row("third") };
        vm.RefreshQueuedRows(rows);
        // An unhosted control has no presentation source from which to resolve a theme template.
        // Supply only the visual host; selection and container generation remain real WPF code.
        var list = new ListBox
        {
            ItemsSource = vm.QueuedRaces, SelectionMode = SelectionMode.Extended,
            Template = new ControlTemplate(typeof(ListBox)) { VisualTree = new FrameworkElementFactory(typeof(ItemsPresenter)) },
            ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(StackPanel)))
        };
        VirtualizingPanel.SetIsVirtualizing(list, false);
        list.SelectionChanged += (_, _) => vm.SelectedQueuedRaces = list.SelectedItems.Cast<QueuedRaceVm>().ToArray();
        list.ApplyTemplate();
        list.Measure(new Size(500, 500)); list.Arrange(new Rect(0, 0, 500, 500)); list.UpdateLayout();
        foreach (var row in rows)
        {
            foreach (var selected in list.SelectedItems.Cast<QueuedRaceVm>().ToArray())
                ((ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(selected)).IsSelected = false;
            ((ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(row)).IsSelected = true;
        }
        Assert.Same(rows[2], Assert.Single(list.SelectedItems.Cast<QueuedRaceVm>()));
        Assert.Same(rows[2], Assert.Single(vm.SelectedQueuedRaces));

        var hash = rows[2].GetHashCode();
        vm.RefreshQueuedRows([Row("third", "Wait changed"), Row("first"), Row("second")]);
        Assert.Same(rows[2], vm.QueuedRaces[0]);
        Assert.Equal("Wait changed", rows[2].RetryText);
        Assert.Equal(hash, rows[2].GetHashCode());
        Assert.Same(rows[2], Assert.Single(list.SelectedItems.Cast<QueuedRaceVm>()));
        Assert.Same(rows[2], Assert.Single(vm.SelectedQueuedRaces));
        list.UpdateLayout();
        var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(rows[2]);
        Assert.NotNull(container);
        Assert.True(container.IsSelected);

        list.SelectedItems.Clear();
        Assert.Empty(vm.SelectedQueuedRaces);
        list.SelectedItems.Add(rows[0]); list.SelectedItems.Add(rows[1]);
        list.SelectedItems.Clear(); list.SelectedItems.Add(rows[2]);
        Assert.Same(rows[2], Assert.Single(vm.SelectedQueuedRaces));
        vm.RefreshQueuedRows([Row("replacement")]);
        Assert.Empty(vm.SelectedQueuedRaces);
        Assert.Empty(list.SelectedItems);
    });

    [Fact]
    public void Bulk_removal_captures_only_current_selected_rows_and_rejects_stale_row_instances() => OnSta(() =>
    {
        var vm = Model();
        var rows = new[] { Row("first"), Row("second"), Row("third") };
        vm.RefreshQueuedRows(rows);
        vm.SelectedQueuedRaces = [rows[0], rows[1]];
        vm.SelectedQueuedRaces = [rows[2]];
        string? label = null;
        vm.SetBulkScheduler((description, _) => { label = description; return true; });
        typeof(SpreadViewModel).GetMethod("InitializeBulkQueueActions", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null);
        vm.RemoveSelectedQueuedRacesCommand.Execute(null);
        Assert.Equal("Remove 1 queued race requests", label);
        vm.SelectedQueuedRaces = [Row("third")];
        Assert.Empty(vm.SelectedQueuedRaces);
        vm.RemoveSelectedQueuedRacesCommand.Execute(null);
        Assert.Equal("Select queued requests first.", vm.SpreadStatus);
    });

    private static QueuedRaceVm Row(string id, string retry = "Waiting") => new("video", id, "Queued", retry) { RequestId = id };
    private static SpreadViewModel Model()
    {
        var vm = (SpreadViewModel)RuntimeHelpers.GetUninitializedObject(typeof(SpreadViewModel));
        typeof(SpreadViewModel).GetField("<QueuedRaces>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(vm, new ObservableCollection<QueuedRaceVm>());
        typeof(SpreadViewModel).GetField("_selectedQueuedRequestIds", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(vm, new HashSet<string>(StringComparer.Ordinal));
        return vm;
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Queue selection fixture did not finish");
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
