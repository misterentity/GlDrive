using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using GlDrive.AiAgent;
using Xunit;

namespace GlDrive.Tests;

public class TelemetryRecorderReliabilityTests
{
    [Fact]
    public void Dispose_drains_every_stream_to_valid_json_lines()
    {
        var root = Path.Combine(Path.GetTempPath(), "gldrive-telemetry-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var recorder = new TelemetryRecorder(root, 10);
            foreach (var stream in Enum.GetValues<TelemetryStream>())
                for (var i = 0; i < 100; i++)
                    recorder.Record(stream, new RaceOutcomeEvent { RaceId = i.ToString() });
            recorder.Dispose();
            recorder.Dispose(); // repeated shutdown must be harmless

            var paths = Directory.GetFiles(Path.Combine(root, "ai-data"), "*.jsonl");
            Assert.Equal(Enum.GetValues<TelemetryStream>().Length, paths.Length);
            foreach (var path in paths)
            {
                Assert.Equal((byte)'{', File.ReadAllBytes(path)[0]);
                var rows = File.ReadAllLines(path);
                Assert.Equal(100, rows.Length);
                for (var i = 0; i < rows.Length; i++)
                {
                    using var doc = JsonDocument.Parse(rows[i]);
                    Assert.Equal(i.ToString(), doc.RootElement.GetProperty("raceId").GetString());
                }
            }
            Assert.All(recorder.GetDropCounts().Values, count => Assert.Equal(0, count));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Saturated_queue_counts_rejected_rows_and_batches_accepted_rows()
    {
        var root = Path.Combine(Path.GetTempPath(), "gldrive-telemetry-" + Guid.NewGuid().ToString("N"));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = new ConcurrentQueue<string>();
        using var recorder = new TelemetryRecorder(root, 10, async (_, text, ct) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
            writes.Enqueue(text);
        }, queueCapacity: 8);
        try
        {
            recorder.Record(TelemetryStream.Races, new RaceOutcomeEvent { RaceId = "first" });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var i = 0; i < 20; i++)
                recorder.Record(TelemetryStream.Races, new RaceOutcomeEvent { RaceId = i.ToString() });

            Assert.Equal(12, recorder.GetDropCounts()[TelemetryStream.Races]);
            release.TrySetResult();
            recorder.Dispose();
            Assert.Equal(2, writes.Count);
            Assert.Equal(9, writes.Sum(text => text.Count(c => c == '\n')));
        }
        finally
        {
            release.TrySetResult();
            recorder.Dispose();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Failed_writes_are_counted()
    {
        var root = Path.Combine(Path.GetTempPath(), "gldrive-telemetry-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var recorder = new TelemetryRecorder(root, 10,
                (_, _, _) => Task.FromException(new IOException("Simulated unavailable disk")));
            for (var i = 0; i < 100; i++)
                recorder.Record(TelemetryStream.Races, new RaceOutcomeEvent());
            recorder.Dispose();
            Assert.Equal(100, recorder.GetDropCounts()[TelemetryStream.Races]);
        }
        finally { Directory.Delete(root, true); }
    }
}
