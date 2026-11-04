using GlDrive.Services;
using Xunit;

namespace GlDrive.Tests;

public class UpdateWatchdogWaitTests
{
    [Fact]
    public void CompletionSignalEndsTheWaitImmediately() =>
        Assert.False(UpdateWatchdogWait.ShouldKeepWaiting(TimeSpan.FromSeconds(5), true, () => true));

    [Fact]
    public void WaitsTheBaseWindowEvenIfNoUpdaterIsVisible() =>
        Assert.True(UpdateWatchdogWait.ShouldKeepWaiting(TimeSpan.FromMinutes(2), false, () => false));

    [Fact]
    public void KeepsWaitingPastThreeMinutesWhileTheUpdaterRuns()
    {
        Assert.True(UpdateWatchdogWait.ShouldKeepWaiting(TimeSpan.FromMinutes(6), false, () => true));
        Assert.False(UpdateWatchdogWait.ShouldKeepWaiting(TimeSpan.FromMinutes(6), false, () => false));
    }

    [Fact]
    public void HardCapStopsAWedgedUpdater() =>
        Assert.False(UpdateWatchdogWait.ShouldKeepWaiting(TimeSpan.FromMinutes(21), false, () => true));
}
