using System;
using System.IO;
using GlDrive.Services;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// 2026-10-01 17:07:46 Dave restarted Windows from the Start menu. GlDrive logged "shutting
/// down" at 17:07:51, the OS killed it mid-teardown before OnExit deleted <c>.running</c>, the
/// watchdog logged an unclean exit and relaunched GlDrive into the shutting-down OS, and the
/// next boot raised a WRN + a "previous shutdown did not complete" tray balloon. An exit the app
/// itself began must be distinguishable from a crash.
/// </summary>
public sealed class IntendedExitMarkerTests
{
    private sealed class Sink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

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
    public void ExitingMarkerRoundTripsSinceAndReason()
    {
        var content = ExitMarker.Exiting(new DateTime(2026, 10, 2, 0, 7, 51, DateTimeKind.Utc), "Windows session ending (Shutdown)");
        Assert.True(ExitMarker.TryParseExiting(content, out var since, out var reason));
        Assert.StartsWith("2026-10-02T00:07:51", since);
        Assert.Equal("Windows session ending (Shutdown)", reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2026-10-01T23:00:00.0000000Z")]
    [InlineData("CRASH:2026-10-02T00:07:54.9855317Z")]
    [InlineData("garbage")]
    public void NonExitingContentIsNotAnIntendedExit(string? content)
        => Assert.False(ExitMarker.TryParseExiting(content, out _, out _));

    [Theory]
    [InlineData(false, null, (int)WatchdogExitVerdict.Clean)]
    [InlineData(true, "2026-10-01T23:00:00.0000000Z", (int)WatchdogExitVerdict.Unclean)]
    [InlineData(true, "CRASH:2026-10-01T23:00:00Z", (int)WatchdogExitVerdict.Unclean)]
    [InlineData(true, null, (int)WatchdogExitVerdict.Unclean)] // unreadable: keep restarting
    [InlineData(true, "EXITING:2026-10-02T00:07:51Z|tray Exit", (int)WatchdogExitVerdict.IntendedExit)]
    public void WatchdogRestartsOnlyWhenTheAppBelievedItWasRunning(bool exists, string? content, int expected)
        => Assert.Equal((WatchdogExitVerdict)expected, ExitMarker.ClassifyForWatchdog(exists, content));

    [Fact]
    public void StartupReportsInterruptedIntendedExitAsInformationNotUncleanExit()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".running");
        try
        {
            File.WriteAllText(path, ExitMarker.Exiting(DateTime.UtcNow, "Windows session ending (Shutdown)"));
            var prior = PreviousSessionDiagnostics.Capture(path);
            var sink = new Sink();
            using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();

            Assert.False(prior.Report(logger, new(true, TimeSpan.FromMinutes(5), "snapshot")));
            Assert.DoesNotContain(sink.Events, e => e.Level >= LogEventLevel.Warning);
            Assert.Contains(sink.Events, e => e.RenderMessage().Contains("Windows session ending (Shutdown)"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void OnExitMarksExitingBeforeAnyTeardown()
    {
        var src = ReadSource("src/GlDrive/App.xaml.cs");
        var onExit = src.IndexOf("protected override void OnExit(", StringComparison.Ordinal);
        Assert.True(onExit >= 0);
        var mark = src.IndexOf("MarkExiting(", onExit, StringComparison.Ordinal);
        var firstTeardown = src.IndexOf("_heartbeat?.Dispose()", onExit, StringComparison.Ordinal);
        Assert.True(mark > onExit && firstTeardown > 0 && mark < firstTeardown,
            "OnExit must record the intended exit before teardown can be cut short");
    }

    [Fact]
    public void SessionEndingIsMarkedAsIntendedExit()
    {
        var src = ReadSource("src/GlDrive/App.xaml.cs");
        var handler = src.IndexOf("protected override void OnSessionEnding(", StringComparison.Ordinal);
        Assert.True(handler >= 0, "App must observe Windows logoff/restart");
        var mark = src.IndexOf("MarkExiting(", handler, StringComparison.Ordinal);
        var end = src.IndexOf("base.OnSessionEnding(", handler, StringComparison.Ordinal);
        Assert.True(mark > handler && mark < end);
    }

    [Fact]
    public void TrayExitMarksBeforeItsForcedEnvironmentExit()
    {
        var src = ReadSource("src/GlDrive/UI/TrayViewModel.cs");
        var cmd = src.IndexOf("ExitCommand = new RelayCommand", StringComparison.Ordinal);
        var mark = src.IndexOf("App.MarkExiting(", cmd, StringComparison.Ordinal);
        var unmount = src.IndexOf("UnmountAllAsync", cmd, StringComparison.Ordinal);
        var forced = src.IndexOf("Environment.Exit(0)", cmd, StringComparison.Ordinal);
        Assert.True(cmd >= 0 && mark > cmd && mark < unmount && mark < forced,
            "the 3s Environment.Exit fallback can kill OnExit before it deletes the marker");
    }

    [Fact]
    public void WatchdogConsultsVerdictBeforeRestarting()
    {
        var src = ReadSource("src/GlDrive/Program.cs");
        var watchdog = src.IndexOf("private static int RunWatchdog(", StringComparison.Ordinal);
        var classify = src.IndexOf("ExitMarker.ClassifyForWatchdog(", watchdog, StringComparison.Ordinal);
        var intended = src.IndexOf("WatchdogExitVerdict.IntendedExit", watchdog, StringComparison.Ordinal);
        var restart = src.IndexOf("Process.Start(new ProcessStartInfo", src.IndexOf("WatchdogExitVerdict.IntendedExit", watchdog, StringComparison.Ordinal), StringComparison.Ordinal);
        var crashRewrite = src.IndexOf("File.WriteAllText(crashMarker, $\"CRASH:", watchdog, StringComparison.Ordinal);
        Assert.True(classify > watchdog && intended > classify && crashRewrite > intended && restart > intended);
        var noRestart = src.IndexOf("return 0;", intended, StringComparison.Ordinal);
        Assert.True(noRestart > intended && noRestart < crashRewrite,
            "an intended exit must return before the crash rewrite and restart");
    }
}
