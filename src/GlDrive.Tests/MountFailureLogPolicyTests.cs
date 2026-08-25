using GlDrive.Services;
using System;
using System.IO;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// v3.10.50: a forever-retry that logs two stacks per attempt buried the day's log
/// (7,244 of 16,084 lines on 2026-08-07). These pin the throttle that replaced it.
/// </summary>
public class MountFailureLogPolicyTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void OpeningAttemptsAlwaysLogInFull(int attempt)
        => Assert.True(MountFailureLogPolicy.ShouldLogFullDetail(attempt));

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(11)]
    [InlineData(13)]
    [InlineData(226)]
    public void SteadyStateRetriesAreCompact(int attempt)
        => Assert.False(MountFailureLogPolicy.ShouldLogFullDetail(attempt));

    [Theory]
    [InlineData(12)]
    [InlineData(24)]
    [InlineData(228)]
    public void EveryTwelfthAttemptLogsInFull(int attempt)
        => Assert.True(MountFailureLogPolicy.ShouldLogFullDetail(attempt));

    [Fact]
    public void NonPositiveAttemptIsTreatedAsTheFirst()
    {
        Assert.True(MountFailureLogPolicy.ShouldLogFullDetail(0));
        Assert.True(MountFailureLogPolicy.ShouldLogFullDetail(-5));
    }

    [Fact]
    public void ChangedErrorLogsInFullEvenDeepIntoAStreak()
    {
        // The moment the failure mode changes is the diagnostic moment — it must not
        // be swallowed just because the streak is long.
        Assert.True(MountFailureLogPolicy.ShouldLogFullDetail(
            attempt: 200, previousError: "Timed out trying to connect!", currentError: "530 Too many logins"));
    }

    [Fact]
    public void UnchangedErrorDeepIntoAStreakStaysCompact()
    {
        Assert.False(MountFailureLogPolicy.ShouldLogFullDetail(
            attempt: 200, previousError: "Timed out trying to connect!", currentError: "Timed out trying to connect!"));
    }

    [Fact]
    public void FirstAttemptHasNoPreviousErrorAndLogsInFull()
    {
        Assert.True(MountFailureLogPolicy.ShouldLogFullDetail(
            attempt: 1, previousError: null, currentError: "Timed out trying to connect!"));
    }

    [Fact]
    public void A226AttemptStreakLogsFarFewerStacksThanAttempts()
    {
        // The regression this guards: 226 attempts previously produced 226 stacks
        // from this call site alone (plus another 226 from MountService).
        var full = 0;
        for (var i = 1; i <= 226; i++)
            if (MountFailureLogPolicy.ShouldLogFullDetail(i, "same", "same")) full++;

        Assert.Equal(3 + 226 / MountFailureLogPolicy.PeriodicInterval, full);
        Assert.True(full < 25, $"expected a large reduction, got {full} full logs in 226 attempts");
    }

    [Fact]
    public void Recovered_startup_mount_failure_is_not_logged_as_an_application_error()
    {
        var source = ReadServerManagerSource();

        Assert.Contains("Log.Warning(ex, \"Failed to mount server", source);
        Assert.DoesNotContain("Log.Error(ex, \"Failed to mount server", source);
    }

    [Fact]
    public void Repetitive_compact_remount_summary_is_information_level()
    {
        var source = ReadServerManagerSource();

        Assert.Contains("Log.Information(\"Remount attempt {Attempt} for {Name} failed ({Error})", source);
        Assert.DoesNotContain("Log.Warning(\"Remount attempt {Attempt} for {Name} failed ({Error})", source);
    }

    private static string ReadServerManagerSource()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12 && dir != null; i++)
        {
            var candidate = Path.Combine(dir, "src", "GlDrive", "Services", "ServerManager.cs");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new InvalidOperationException("Could not locate ServerManager.cs from " + AppContext.BaseDirectory);
    }
}
