using System.IO;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Pins the production wiring around <c>SpreadPoolRecoveryLoop</c>. The loop's
/// behavior is unit-tested separately; these checks ensure a failed first init is
/// actually enrolled and an unmount actually cancels it.
/// </summary>
public sealed class SpreadPoolRecoveryWiringTests
{
    private static readonly string Source = ReadSpreadManager();

    [Fact]
    public void DesiredFactoryIsRecordedBeforeTheFirstAttempt()
    {
        var record = Source.IndexOf("_factories[serverId] = factory", StringComparison.Ordinal);
        var attempt = Source.IndexOf("TryInitializePool(serverId, factory, ct", StringComparison.Ordinal);

        Assert.True(record >= 0, "SpreadManager no longer records the desired factory.");
        Assert.True(attempt > record, "The factory must be recorded before initial pool creation can fail.");
    }

    [Fact]
    public void FailedInitialAttemptSchedulesBackgroundRecovery()
        => Assert.Contains("SchedulePoolRecovery(serverId, factory)", Source);

    [Fact]
    public void UnmountCancelsBackgroundRecovery()
        => Assert.Contains("await _poolRecovery.CancelAsync(serverId)", Source);

    private static string ReadSpreadManager()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (; dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "src", "GlDrive", "Spread", "SpreadManager.cs");
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        throw new FileNotFoundException("SpreadManager.cs not found from " + Directory.GetCurrentDirectory());
    }
}
