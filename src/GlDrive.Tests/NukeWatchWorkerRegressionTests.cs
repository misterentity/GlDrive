using System.IO;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Library;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

public sealed class NukeWatchWorkerRegressionTests : IDisposable
{
    private const string Original = "Example.Movie.2026.1080p.WEB.H264-GROUP";
    private const string Proper = "Example.Movie.2026.PROPER.1080p.WEB.H264-OTHER";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-nuke-worker-" + Guid.NewGuid().ToString("N"));
    private readonly AppConfig _config = new();
    private readonly List<DownloadItem> _downloads = [];
    private readonly DateTime _now = DateTime.UtcNow;
    private readonly List<SitebotReleaseFacts> _facts = [];
    public NukeWatchWorkerRegressionTests()
    {
        Directory.CreateDirectory(_root);
        _config.NukeWatch.AutoQueueFixes = true;
        _facts.Add(new() { ServerId = "site", Release = Proper, Complete = new()
            { ServerId = "site", Section = "movies", Release = Proper, Kind = SitebotEventKind.Complete, ObservedAtUtc = _now } });
    }
    public void Dispose() => Directory.Delete(_root, true);
    private NukeWatchStore Store(string? path = null) => new(path ?? Path.Combine(_root, "state.json"), () => _config.NukeWatch, () => DateTime.UtcNow);
    private NukeWatch Watch(NukeWatchStore store, Func<OwnedReleaseObservation, SitebotEvent, DownloadItem?> enqueue) =>
        new(_config, store, () => _downloads.ToArray(), () => _facts.ToArray(), _ => null, enqueue);
    private void Flag(NukeWatch watch)
    {
        watch.ObserveOwned(new(Original, Path.Combine(_root, "owned"), _now));
        watch.ObservePreDb([new() { Release = Original, Status = 3, Reason = "bad.ivtc" }]);
    }
    private DownloadItem Queue(OwnedReleaseObservation owned, SitebotEvent completion)
    {
        var download = new DownloadItem { ReleaseName = completion.Release, NukeCorrectionForRelease = owned.ReleaseName,
            Status = DownloadStatus.Queued, LocalPath = Path.Combine(_root, completion.Release) };
        _downloads.Add(download);
        return download;
    }

    [Fact]
    public async Task Failed_ledger_write_does_not_block_an_opted_in_correction()
    {
        var unavailable = Path.Combine(_root, "state-as-directory"); Directory.CreateDirectory(unavailable);
        var store = Store(unavailable);
        using var watch = Watch(store, Queue);
        Flag(watch);
        await watch.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(store.PersistenceHealthy);
        Assert.Equal(Proper, Assert.Single(_downloads).ReleaseName);
    }

    [Theory]
    [InlineData(DownloadStatus.Cancelled)]
    [InlineData(DownloadStatus.Failed)]
    public async Task Actual_download_provenance_prevents_exact_requeue_after_ledger_loss(DownloadStatus previousStatus)
    {
        _downloads.Add(new() { ReleaseName = Proper, NukeCorrectionForRelease = Original, Status = previousStatus });
        using var watch = Watch(Store(), Queue);
        Flag(watch);
        await watch.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(_downloads);
        Assert.Equal(previousStatus, _downloads[0].Status);
    }

    [Fact]
    public async Task Disposal_does_not_wait_for_an_in_flight_enqueue_callback()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var watch = Watch(Store(), (owned, completion) =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(8));
            return null;
        });
        try
        {
            Flag(watch);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            await Task.Run(watch.Dispose).WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            release.Set();
            watch.Dispose();
            await watch.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Background_worker_does_not_inherit_caller_attribution()
    {
        var attribution = new AsyncLocal<string?> { Value = "synthetic-caller" };
        string? observed = "not-run";
        using var watch = Watch(Store(), (owned, completion) => { observed = attribution.Value; return Queue(owned, completion); });
        Flag(watch);
        await watch.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(_downloads);
        Assert.Null(observed);
    }

    [Fact]
    public async Task Existing_predb_nuke_for_candidate_blocks_only_that_correction()
    {
        using var watch = Watch(Store(), Queue);
        watch.ObservePreDb([new() { Release = Proper, Status = 3, Reason = "corrupt" }]);
        Flag(watch);
        await watch.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(_downloads);
        Assert.True(Assert.Single(watch.Store.Snapshot()).Flagged);
    }
}
