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

    [Fact]
    public void Screenshot_mode_does_not_spawn_a_watchdog()
    {
        var source = ReadSource("src/GlDrive/Program.cs");
        var spawnGuard = source.Substring(
            source.IndexOf("if (Array.IndexOf(args, \"--apply-update\")", StringComparison.Ordinal), 180);

        Assert.Contains("Array.IndexOf(args, \"--screenshots\") < 0", spawnGuard);
        Assert.Contains("SpawnWatchdog()", spawnGuard);
    }
}
