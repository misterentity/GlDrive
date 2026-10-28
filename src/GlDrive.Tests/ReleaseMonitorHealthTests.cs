using GlDrive.Services;
using GlDrive.Config;
using FluentFTP;
using Xunit;

namespace GlDrive.Tests;

public class ReleaseMonitorHealthTests
{
    [Fact]
    public void EmptyWatchPathWarnsOnlyOncePerMountAndRecordsRecovery()
    {
        var health = new ReleaseMonitorHealth();
        var now = DateTime.UtcNow;
        Assert.True(health.Observe("/recent", 0, now));
        Assert.True(health.HasNoCategories);
        Assert.Equal("/recent", health.WatchPath);
        Assert.Equal(now, health.LastCheckedUtc);
        Assert.False(health.Observe("/recent", 0, now));
        Assert.False(health.Observe("/recent", 3, now));
        Assert.False(health.HasNoCategories);
        Assert.False(health.Observe("/another", 0, now));
        Assert.True(health.HasNoCategories);
        Assert.True(new ReleaseMonitorHealth().Observe("/recent", 0, now));
    }

    [Fact]
    public void NonemptyListingDoesNotConsumeWarning()
    {
        var health = new ReleaseMonitorHealth();
        Assert.False(health.Observe("/recent", 2, DateTime.UtcNow));
        Assert.True(health.Observe("/recent", 0, DateTime.UtcNow));
    }

    [Fact]
    public async Task ExcludingEveryCategoryStillRecordsNonemptyWatchPathWithoutListingCategories()
    {
        var paths = new List<string>();
        var monitor = new NewReleaseMonitor(new NotificationConfig { ExcludedCategories = ["TV"] },
            () => MountState.Connected, (path, _) =>
            {
                paths.Add(path);
                return Task.FromResult(new[] { new FtpListItem { Name = "TV", Type = FtpObjectType.Directory } });
            }, () => 0, "fixture", (_, _) => Task.CompletedTask);
        await monitor.PollCycle(CancellationToken.None);
        Assert.False(monitor.Health.HasNoCategories);
        Assert.Equal(new[] { "/recent" }, paths);
    }
}
