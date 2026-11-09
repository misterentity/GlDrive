using System.Reflection;
using GlDrive.Config;
using GlDrive.Ftp;
using GlDrive.Spread;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace GlDrive.Tests;

[CollectionDefinition("Recovered participant logger", DisableParallelization = true)]
public sealed class RecoveredParticipantLoggerCollection { }

[Collection("Recovered participant logger")]
public sealed class SpreadRecoveredParticipantTests
{
    [Fact]
    public async Task Unexpected_job_failure_retains_exception_on_its_single_warning()
    {
        var sink = new CaptureSink();
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        var previous = Log.Logger;
        Log.Logger = logger;
        try
        {
            var expected = new InvalidOperationException("Synthetic participant lookup failure");
            using var job = new SpreadJob("TEST", "Fixture.Release", SpreadMode.Race,
                new() { MergeRaceAnnounces = true }, new(), new(), new(), new(), new())
            {
                ServerConfigResolver = _ => throw expected
            };
            job.QueueSourceHints([new("source", "TEST", "/test/Fixture.Release")]);
            var errors = new List<string>();
            job.Error += (_, message) => errors.Add(message);

            await job.RunAsync();

            Assert.Equal(SpreadJobState.Failed, job.State);
            Assert.Equal(expected.Message, job.LastError);
            Assert.Equal(expected.Message, Assert.Single(errors));
            var warning = Assert.Single(sink.Events, e => e.Level >= LogEventLevel.Warning);
            Assert.Same(expected, warning.Exception);
            Assert.Contains("ApplyPendingSourceHintsAsync", warning.Exception!.StackTrace);
            Assert.Contains(expected.Message, warning.RenderMessage());
        }
        finally { Log.Logger = previous; }
    }

    [Theory]
    [InlineData("source", false)]
    [InlineData("destination", false)]
    [InlineData("source", true)]
    [InlineData("destination", true)]
    public async Task Recovered_participant_can_be_scheduled_and_claim_slots(string recoveredId, bool liveRefresh)
    {
        await using var fixture = new Fixture(recoveredId);
        var job = fixture.Job;
        Assert.False(job.Sites.ContainsKey(recoveredId));

        // Admission retained both configs, but the unavailable participant had no
        // pool. Reinit or a later live-registry refresh now makes it usable.
        if (liveRefresh)
            job.LivePoolResolver = id => fixture.Pools.GetValueOrDefault(id);
        else
            job.UpdatePools(new(fixture.Pools));

        Invoke(job, "ProcessFiles", "source", new List<SpreadFileInfo>
        {
            new() { Name = "payload.rar", FullPath = "/source/payload.rar", Size = 128 }
        }, 0L);
        var selected = Assert.IsType<ValueTuple<SpreadFileInfo, string, string>>(
            Invoke(job, "FindBestTransfer", fixture.Paths, new SpreadScorer(new SpeedTracker())));
        Assert.Equal("source", selected.Item2);
        Assert.Equal("destination", selected.Item3);

        Assert.True(Assert.IsType<bool>(Invoke(job, "TryClaimSlots", selected.Item2, selected.Item3)));
        Assert.Equal(1, job.Sites["source"].ActiveTransfers);
        Assert.Equal(1, job.Sites["destination"].ActiveTransfers);
        Assert.False(Assert.IsType<bool>(Invoke(job, "TryClaimSlots", selected.Item2, selected.Item3)));

        // Early execution rejection still follows the production finally path and
        // releases both claimed slots, without opening any network connection.
        job.IsReleaseNuked = (_, _) => true;
        var execution = Assert.IsAssignableFrom<Task>(Invoke(job, "ExecuteTransfer", selected.Item1,
            selected.Item2, selected.Item3, fixture.Paths["destination"], CancellationToken.None,
            CancellationToken.None, (Action)(() => { })));
        await execution;
        Assert.Equal(0, job.Sites["source"].ActiveTransfers);
        Assert.Equal(0, job.Sites["destination"].ActiveTransfers);
        Assert.Equal(SpreadJobState.Running, job.State);
    }

    [Fact]
    public async Task Pool_replacement_preserves_progress_and_active_slot_accounting()
    {
        await using var fixture = new Fixture("destination");
        var job = fixture.Job;
        job.UpdatePools(new(fixture.Pools));
        var progress = job.Sites["destination"];
        progress.BytesTransferred = 512;
        progress.FilesOwned = 3;
        progress.IsSource = true;
        Assert.True(Assert.IsType<bool>(Invoke(job, "TryClaimSlots", "source", "destination")));

        job.UpdatePools(new() { ["source"] = fixture.Pools["source"] });
        await using var replacement = new FtpConnectionPool(null!, 1);
        job.UpdatePools(new() { ["source"] = fixture.Pools["source"], ["destination"] = replacement });

        Assert.Same(progress, job.Sites["destination"]);
        Assert.Equal(512, progress.BytesTransferred);
        Assert.Equal(3, progress.FilesOwned);
        Assert.True(progress.IsSource);
        Assert.Equal(1, progress.ActiveTransfers);
        Assert.False(Assert.IsType<bool>(Invoke(job, "TryClaimSlots", "source", "destination")));
        Assert.Same(replacement, job.ResolveSpreadPool("destination"));
    }

    [Fact]
    public async Task Display_snapshot_can_finish_enumerating_while_a_participant_recovers()
    {
        await using var fixture = new Fixture("destination");
        var job = fixture.Job;
        var visible = job.Sites;
        using var rows = visible.GetEnumerator();
        Assert.True(rows.MoveNext());
        Assert.Equal("source", rows.Current.Key);
        var originalSource = rows.Current.Value;

        // The display captured its rows before recovery publishes a new site.
        // Finishing that render must not enumerate a structurally modified map.
        job.UpdatePools(new(fixture.Pools));

        Assert.False(rows.MoveNext());
        Assert.Single(visible);
        Assert.False(visible.ContainsKey("destination"));
        var refreshed = job.Sites;
        Assert.Equal(2, refreshed.Count);
        Assert.Same(originalSource, refreshed["source"]);

        // Only the participant inventory is snapshotted: existing progress
        // objects remain shared so previously captured counters keep updating.
        originalSource.IsSource = true;
        originalSource.FilesTotal = 4;
        var destination = refreshed["destination"];
        destination.FilesTotal = 4;
        destination.FilesOwned = 2;
        Assert.Equal(32767, job.Score);
        job.UpdatePools(new() { ["source"] = fixture.Pools["source"] });
        Assert.Same(destination, job.Sites["destination"]);
        Assert.Equal(32767, job.Score);
    }

    [Fact]
    public async Task Unknown_pool_is_rejected_without_publishing_partial_participant_state()
    {
        await using var fixture = new Fixture("destination");
        await using var unknown = new FtpConnectionPool(null!, 1);
        var job = fixture.Job;
        var invalid = new Dictionary<string, FtpConnectionPool>(fixture.Pools) { ["unknown"] = unknown };

        Assert.Throws<ArgumentException>(() => job.UpdatePools(invalid));

        Assert.Single(job.Sites);
        Assert.Null(job.ResolveSpreadPool("destination"));
        Assert.Null(job.ResolveSpreadPool("unknown"));
        Assert.Same(fixture.Pools["source"], job.ResolveSpreadPool("source"));
    }

    private static object? Invoke(SpreadJob job, string method, params object[] args) =>
        typeof(SpreadJob).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(job, args);

    private sealed class CaptureSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];
        public void Emit(LogEvent logEvent) { lock (Events) Events.Add(logEvent); }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public SpreadJob Job { get; }
        public Dictionary<string, FtpConnectionPool> Pools { get; } = new()
        {
            ["source"] = new(null!, 1), ["destination"] = new(null!, 1)
        };
        public Dictionary<string, string> Paths { get; } = new()
        {
            ["source"] = "/source", ["destination"] = "/destination"
        };

        public Fixture(string unavailableId)
        {
            var configs = Pools.Keys.ToDictionary(id => id, id => new ServerConfig
            {
                Id = id, Name = id,
                SpreadSite = new() { MaxDownloadSlots = 1, MaxUploadSlots = 1 }
            });
            Job = new SpreadJob("TEST", "Fixture.Release", SpreadMode.Race, new(),
                Pools.Where(p => p.Key != unavailableId).ToDictionary(), new(), configs, new(), new());
        }

        public async ValueTask DisposeAsync()
        {
            Job.Dispose();
            foreach (var pool in Pools.Values) await pool.DisposeAsync();
        }
    }
}
