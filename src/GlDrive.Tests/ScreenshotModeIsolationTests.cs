using System;
using System.IO;
using Xunit;

namespace GlDrive.Tests;

public sealed class ScreenshotModeIsolationTests
{
    private static string ReadSource(string relativePath)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12 && dir != null; i++)
        {
            var candidate = Path.Combine(dir, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException($"Could not locate {relativePath}");
    }

    [Fact]
    public void Screenshot_mode_exits_before_touching_production_lifecycle_state()
    {
        var source = ReadSource("src/GlDrive/App.xaml.cs");
        var screenshot = source.IndexOf("e.Args.Contains(\"--screenshots\"", StringComparison.Ordinal);
        var restartRegistration = source.IndexOf("RegisterApplicationRestart(null, 0)", StringComparison.Ordinal);
        var crashMarker = source.IndexOf("var crashMarker =", StringComparison.Ordinal);
        var updateCleanup = source.IndexOf("UpdateChecker.CleanupOldUpdateFiles()", StringComparison.Ordinal);

        Assert.True(screenshot >= 0);
        Assert.True(screenshot < restartRegistration);
        Assert.True(screenshot < crashMarker);
        Assert.True(screenshot < updateCleanup);
        Assert.Contains("if (_ownsCrashMarker)", source);
        Assert.Contains("_ownsCrashMarker = true", source);
    }

    /// <summary>
    /// The watchdog-spawn guard must exclude every mode that is not a normal app start.
    ///
    /// Window is taken up to the SpawnWatchdog() call rather than a fixed character count: the
    /// original 180-char slice silently excluded the call as soon as a fourth exclusion was added,
    /// failing for width rather than for the property under test.
    /// </summary>
    [Fact]
    public void Watchdog_spawn_is_skipped_for_every_non_normal_mode()
    {
        var source = ReadSource("src/GlDrive/Program.cs");
        var guardStart = source.IndexOf("if (Array.IndexOf(args, \"--apply-update\")", StringComparison.Ordinal);
        Assert.True(guardStart >= 0, "watchdog spawn guard not found");

        var spawnCall = source.IndexOf("SpawnWatchdog()", guardStart, StringComparison.Ordinal);
        Assert.True(spawnCall > guardStart, "SpawnWatchdog() does not follow the guard");

        var guard = source.Substring(guardStart, spawnCall - guardStart);

        Assert.Contains("Array.IndexOf(args, \"--apply-update\") < 0", guard);
        Assert.Contains("Array.IndexOf(args, \"--screenshots\") < 0", guard);
        // --apply-update-task is a DISTINCT argument: Array.IndexOf is an exact match, so the
        // --apply-update entry above does NOT cover it. Without this the SYSTEM task's process
        // spawns a watchdog and races its own file replacement.
        Assert.Contains("Array.IndexOf(args, \"--apply-update-task\") < 0", guard);
    }
}
