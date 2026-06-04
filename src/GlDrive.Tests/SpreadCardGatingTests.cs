using System.Collections.Generic;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

// Guards the retained-finished-card behavior: a live race keeps PAUSE/RESUME/STOP,
// a finished (DONE/FAILED/STOPPED) race is retained in the list as dismiss-only and
// keeps its final score (points). See SpreadViewModel.FreezeFinishedJob.
public class SpreadCardGatingTests
{
    [Fact]
    public void Active_running_card_can_pause_and_stop_not_dismiss()
    {
        var vm = new SpreadJobVm { IsPaused = false };
        Assert.True(vm.IsActive);
        Assert.True(vm.CanPause);
        Assert.False(vm.CanResume);
        Assert.True(vm.CanStop);
        Assert.False(vm.CanDismiss);
    }

    [Fact]
    public void Active_paused_card_shows_resume_not_pause()
    {
        var vm = new SpreadJobVm { IsPaused = true };
        Assert.False(vm.CanPause);
        Assert.True(vm.CanResume);
        Assert.True(vm.CanStop);
        Assert.False(vm.CanDismiss);
    }

    [Fact]
    public void Finished_card_is_dismiss_only_and_keeps_its_points()
    {
        var vm = new SpreadJobVm { Score = 65535, IsFinished = true };
        Assert.False(vm.IsActive);
        Assert.False(vm.CanPause);
        Assert.False(vm.CanResume);
        Assert.False(vm.CanStop);
        Assert.True(vm.CanDismiss);
        Assert.Equal(65535, vm.Score); // final points retained on the card
    }

    [Fact]
    public void Finishing_a_paused_card_disables_resume()
    {
        var vm = new SpreadJobVm { IsPaused = true };
        Assert.True(vm.CanResume);
        vm.IsFinished = true;
        Assert.False(vm.CanResume); // finished overrides paused
        Assert.True(vm.CanDismiss);
    }

    [Fact]
    public void IsFinished_raises_gating_property_changes()
    {
        var vm = new SpreadJobVm();
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);
        vm.IsFinished = true;
        Assert.Contains(nameof(SpreadJobVm.CanStop), changed);
        Assert.Contains(nameof(SpreadJobVm.CanDismiss), changed);
        Assert.Contains(nameof(SpreadJobVm.IsActive), changed);
    }
}
