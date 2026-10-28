using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using GlDrive.Spread;
using GlDrive.UI;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// v3.17.0 routed CLEAR DONE through the dashboard's shared 10-second Undo slot. Finished cards
/// are only a view of completed races (race history is unchanged), so the cards stayed on screen
/// for the whole countdown, were silently kept when the dashboard closed inside it, and could not
/// be cleared at all while another removal was pending. CLEAR DONE reads as broken; like DISMISS
/// it must apply immediately.
/// </summary>
public sealed class SpreadClearFinishedTests
{
    [Fact]
    public void Clear_done_removes_finished_cards_immediately_without_the_undo_slot()
    {
        var vm = Model();
        var scheduled = 0;
        vm.SetBulkScheduler((_, _) => { scheduled++; return false; }); // another removal pending
        var finished = new SpreadJobVm { Id = "finished", IsFinished = true };
        var running = new SpreadJobVm { Id = "running" };
        vm.SpreadJobs.Add(finished);
        vm.SpreadJobs.Add(running);

        Clear(vm);

        Assert.Same(running, Assert.Single(vm.SpreadJobs));
        Assert.Equal(0, scheduled);
        Assert.Contains("Cleared 1 finished race card", vm.SpreadStatus);
    }

    [Fact]
    public void Clear_done_with_no_finished_cards_changes_nothing()
    {
        var vm = Model();
        vm.SpreadJobs.Add(new SpreadJobVm { Id = "running" });

        Clear(vm);

        Assert.Single(vm.SpreadJobs);
        Assert.True(string.IsNullOrEmpty(vm.SpreadStatus));
    }

    private static void Clear(SpreadViewModel vm) =>
        typeof(SpreadViewModel).GetMethod("ClearFinished", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null);

    private static SpreadViewModel Model()
    {
        var vm = (SpreadViewModel)RuntimeHelpers.GetUninitializedObject(typeof(SpreadViewModel));
        typeof(SpreadViewModel).GetField("<SpreadJobs>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(vm, new ObservableCollection<SpreadJobVm>());
        typeof(SpreadViewModel).GetField("_jobMaxSpeed", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(vm, new Dictionary<string, double>());
        return vm;
    }
}
