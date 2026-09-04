using System;
using System.IO;
using GlDrive.Services;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// A second GlDrive launch while production is running must be inert. Before v3.10.102 the
/// blocked instance wrote and CLAIMED the shared <c>.running</c> crash marker before the
/// single-instance check ran, so its OnExit deleted the primary's marker — silently disabling
/// the primary's watchdog restart for the rest of its life — and it had already spawned a
/// watchdog of its own. Observed 2026-09-03: the primary died at 09:53 and nothing restarted
/// it for seven hours; the watchdog logged nothing because the marker was gone.
/// </summary>
public sealed class SecondInstanceIsolationTests
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
    public void Main_acquires_single_instance_before_spawning_watchdog_or_wpf()
    {
        var program = ReadSource("src/GlDrive/Program.cs");

        var acquire = program.IndexOf(".TryAcquire()", StringComparison.Ordinal);
        var spawn = program.IndexOf("SpawnWatchdog();", StringComparison.Ordinal);
        var wpf = program.IndexOf("var app = new App()", StringComparison.Ordinal);

        Assert.True(acquire >= 0, "Program.Main must acquire the single-instance guard");
        Assert.True(spawn >= 0 && acquire < spawn, "guard must be acquired before the watchdog is spawned");
        Assert.True(wpf >= 0 && acquire < wpf, "guard must be acquired before the WPF App is constructed");

        // The blocked branch must leave a trace in the log file. Serilog is not initialised
        // this early, so it has to be a direct append (same mechanism the watchdog uses).
        Assert.Contains("Second GlDrive instance", program, StringComparison.Ordinal);
        Assert.Contains("FileShare.ReadWrite", program, StringComparison.Ordinal);

        // The live primary holds the rolling file open, so the sink must explicitly permit
        // the rejected process's direct audit append.
        var logging = ReadSource("src/GlDrive/Logging/SerilogSetup.cs");
        Assert.Contains("shared: true", logging, StringComparison.Ordinal);
    }

    [Fact]
    public void App_writes_crash_marker_only_after_single_instance_is_held()
    {
        var source = ReadSource("src/GlDrive/App.xaml.cs");

        var guard = source.IndexOf("_guard = ", StringComparison.Ordinal);
        var markerWrite = source.IndexOf("File.WriteAllText(crashMarker", StringComparison.Ordinal);
        var ownMarker = source.IndexOf("_ownsCrashMarker = true", StringComparison.Ordinal);
        var updatingDelete = source.IndexOf(".updating\")); } catch { }", StringComparison.Ordinal);
        var restartRegistration = source.IndexOf("RegisterApplicationRestart(null, 0)", StringComparison.Ordinal);

        Assert.True(guard >= 0);
        Assert.True(markerWrite > guard, ".running must not be written before the guard is held");
        Assert.True(ownMarker > guard, ".running must not be claimed before the guard is held");
        Assert.True(updatingDelete > guard, ".updating must not be deleted before the guard is held");
        Assert.True(restartRegistration > guard, "restart registration belongs to the primary only");
    }

    [Fact]
    public void App_deletes_owned_crash_marker_before_releasing_single_instance_guard()
    {
        var source = ReadSource("src/GlDrive/App.xaml.cs");
        var onExit = source.IndexOf("protected override void OnExit", StringComparison.Ordinal);
        var markerDelete = source.IndexOf("File.Delete(Path.Combine(ConfigManager.AppDataPath, \".running\"))", onExit,
            StringComparison.Ordinal);
        var guardDispose = source.IndexOf("_guard?.Dispose()", onExit, StringComparison.Ordinal);

        Assert.True(onExit >= 0);
        Assert.True(markerDelete > onExit, "OnExit must remove the marker it owns");
        Assert.True(guardDispose > markerDelete,
            "the mutex must remain held until the old primary's crash marker is removed");
    }

    [Fact]
    public void Guard_rejects_second_holder_until_first_releases()
    {
        var name = $@"Local\GlDrive_test_{Guid.NewGuid():N}";

        using var first = new SingleInstanceGuard(name);
        Assert.True(first.TryAcquire());

        using (var second = new SingleInstanceGuard(name, retryDelay: TimeSpan.Zero))
            Assert.False(second.TryAcquire());

        first.Dispose();

        using var third = new SingleInstanceGuard(name, retryDelay: TimeSpan.Zero);
        Assert.True(third.TryAcquire());
    }
}
