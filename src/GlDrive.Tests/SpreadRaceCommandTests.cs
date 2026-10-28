using GlDrive.UI;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

public sealed class SpreadRaceCommandTests
{
    [Fact]
    public void CardTargetWinsOverAnotherSelectedRace()
    {
        var selected = new SpreadJobVm { Id = "selected" };
        var clicked = new SpreadJobVm { Id = "clicked" };
        SpreadJobVm? received = null;
        var command = new SpreadRaceCommand(() => selected, target => received = target);
        command.Execute(clicked);
        Assert.Same(clicked, received);
    }

    [Fact]
    public void ToolbarAndEscapeUseCurrentSelectionAtExecution()
    {
        SpreadJobVm? selected = null;
        var calls = new List<string>();
        var command = new SpreadRaceCommand(() => selected, target => calls.Add(target.Id));
        Assert.False(command.CanExecute(null));
        command.Execute(null);
        selected = new() { Id = "first" };
        Assert.True(command.CanExecute(null));
        command.Execute(null);
        selected = new() { Id = "second" };
        command.Execute(null);
        Assert.Equal(new[] { "first", "second" }, calls);
    }

    [Fact]
    public void FinishedOrInvalidExplicitTargetNeverStopsAnotherSelectedRace()
    {
        var calls = 0;
        var command = new SpreadRaceCommand(() => new() { Id = "active" }, _ => calls++);
        var finished = new SpreadJobVm { Id = "done", IsFinished = true };
        Assert.False(command.CanExecute(finished));
        command.Execute(finished);
        command.Execute("invalid");
        Assert.Equal(0, calls);
    }
}
