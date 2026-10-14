using System.Collections.Concurrent;
using System.IO;
using FluentFTP;
using GlDrive.AiAgent;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace GlDrive.Tests;

[CollectionDefinition("NukePoller logger", DisableParallelization = true)]
public sealed class NukePollerLoggerCollection { }

// 2026-10-13 20:37/20:48: the poll deadline fired while Borrow waited for a busy pool
// (FtpConnectionPool.WaitForReturnedConnectionAsync) and was logged as
// "[WRN] NukePoller failed" with a full OperationCanceledException stack. A busy pool
// is a skipped poll, not a fault: it still counts toward the circuit breaker, quietly.
[Collection("NukePoller logger")]
public sealed class NukePollerDeadlineLoggingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-nuke-deadline-" + Guid.NewGuid().ToString("N"));
    private readonly ILogger _previousLogger = Log.Logger;
    private readonly CaptureSink _sink = new();
    private readonly Logger _logger;

    public NukePollerDeadlineLoggingTests()
    {
        _logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(_sink).CreateLogger();
        Log.Logger = _logger;
    }

    public void Dispose()
    {
        Log.Logger = _previousLogger;
        _logger.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static async Task<FtpReply> WaitForever(CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        return new FtpReply();
    }

    [Fact]
    public async Task Poll_deadline_is_an_information_skip_not_a_stack_trace_warning()
    {
        var recorder = new TelemetryRecorder(_root, 64);
        using var poller = new NukePoller(recorder, () => [new("busy", () => true, WaitForever)],
            new NukeCursorStore(_root), _root, 1, pollTimeout: TimeSpan.FromMilliseconds(50), startTimer: false);

        await poller.PollAllAsync();

        var mine = _sink.Events.Where(e => e.MessageTemplate.Text.StartsWith("NukePoller")).ToList();
        Assert.DoesNotContain(mine, e => e.Level >= LogEventLevel.Warning);
        var skip = Assert.Single(mine, e => e.Level == LogEventLevel.Information);
        Assert.Null(skip.Exception);
    }

    [Fact]
    public async Task Poll_deadlines_still_open_the_circuit_after_three_misses()
    {
        var calls = 0;
        var recorder = new TelemetryRecorder(_root, 64);
        using var poller = new NukePoller(recorder, () => [new("busy", () => true, ct =>
            {
                Interlocked.Increment(ref calls);
                return WaitForever(ct);
            })],
            new NukeCursorStore(_root), _root, 1, pollTimeout: TimeSpan.FromMilliseconds(20), startTimer: false);

        for (var i = 0; i < 4; i++) await poller.PollAllAsync();
        Assert.Equal(3, calls);
    }

    private sealed class CaptureSink : ILogEventSink
    {
        private readonly ConcurrentQueue<LogEvent> _events = new();
        public IEnumerable<LogEvent> Events => _events;
        public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);
    }
}
