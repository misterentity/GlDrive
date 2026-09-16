using System.IO;
using GlDrive.Services;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace GlDrive.Tests;

public sealed class PreviousSessionDiagnosticsTests
{
    private sealed class Sink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(89)]
    [InlineData(91)]
    [InlineData(11699)] // Observed after the September 16 planned Windows update restart.
    public void MissingMarkerDoesNotReportUnexpectedExitRegardlessOfHeartbeatAge(int seconds)
    {
        var sink = new Sink();
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        var prior = PreviousSessionDiagnostics.Capture(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".running"));

        Assert.False(prior.Report(logger, new(true, TimeSpan.FromSeconds(seconds), "snapshot")));
        Assert.DoesNotContain(sink.Events, e => e.Level >= LogEventLevel.Warning);
        Assert.Contains(sink.Events, e => e.Properties.ContainsKey("Snapshot"));
    }

    [Theory]
    [InlineData("2026-09-16T10:30:00Z", false)]
    [InlineData("CRASH:2026-09-16T10:30:00Z", true)]
    public void CapturedMarkerSurvivesReplacementAndIsReportedToInitializedLogger(string marker, bool watchdog)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".running");
        try
        {
            File.WriteAllText(path, marker);
            var prior = PreviousSessionDiagnostics.Capture(path);
            File.WriteAllText(path, "new session");
            var sink = new Sink();
            using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();

            // Even missing/corrupt heartbeat data must not hide retained marker evidence.
            Assert.True(prior.Report(logger, new(false, null, null)));
            var warning = Assert.Single(sink.Events, e => e.Level == LogEventLevel.Warning);
            Assert.Equal(watchdog, warning.Properties.ContainsKey("ExitTime"));
            Assert.DoesNotContain("after crash", warning.RenderMessage());
            if (watchdog) Assert.Contains("2026-09-16T10:30:00Z", warning.RenderMessage());
            Assert.Equal("new session", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void UnreadableMarkerIsUnknownRatherThanCleanOrCrash()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".running");
        try
        {
            using var locked = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            var prior = PreviousSessionDiagnostics.Capture(path);
            var sink = new Sink();
            using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();

            Assert.Null(prior.HadRunningMarker);
            Assert.False(prior.Report(logger, new(true, TimeSpan.FromHours(3), "snapshot")));
            Assert.Contains("exit state unknown", Assert.Single(sink.Events, e => e.Level == LogEventLevel.Warning).RenderMessage());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void FirstRunWithoutMarkerOrHeartbeatIsInformational()
    {
        var sink = new Sink();
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        Assert.False(new PreviousSessionDiagnostics(false, null).Report(logger, new(false, null, null)));
        Assert.Equal(LogEventLevel.Information, Assert.Single(sink.Events).Level);
    }
}
