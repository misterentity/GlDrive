using System.IO;
using System.Text.Json;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Ftp;
using Xunit;

namespace GlDrive.Tests;

public sealed class BackgroundWantedPullTests
{
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void OnlyManagedPullsDependOnRacingState(bool managed, bool idle, bool expected) =>
        Assert.Equal(expected, BackgroundPullPolicy.MayRun(managed, () => idle));

    [Fact]
    public void MissingOrFailedRacingStateFailsClosedOnlyForManagedPulls()
    {
        Assert.False(BackgroundPullPolicy.MayRun(true, null));
        Assert.False(BackgroundPullPolicy.MayRun(true, () => throw new InvalidOperationException()));
        Assert.True(BackgroundPullPolicy.MayRun(false, () => throw new InvalidOperationException()));
    }

    [Fact]
    public void ManagedPullFlagIsCompatibleAndSurvivesQueueSerialization()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        Assert.False(JsonSerializer.Deserialize<DownloadItem>("{}", options)!.BackgroundWantedPull);
        var json = JsonSerializer.Serialize(new DownloadItem { BackgroundWantedPull = true }, options);
        Assert.Contains("\"backgroundWantedPull\":true", json);
        Assert.True(JsonSerializer.Deserialize<DownloadItem>(json, options)!.BackgroundWantedPull);
    }

    [Fact]
    public async Task BusyRaceParksWithoutPlanningAndDoesNotBlockManualDownloads()
    {
        await using var fixture = new Fixture();
        fixture.Manager.BackgroundPullMayRun = () => false;
        var managed = fixture.Item(true);
        var manual = fixture.Item(false);
        var parked = fixture.When(item => item == managed && item.StatusDetail?.Contains("racing") == true);
        var completed = fixture.When(item => item == manual && item.Status == DownloadStatus.Completed);
        fixture.Manager.Enqueue(managed);
        fixture.Manager.Enqueue(manual);
        fixture.Manager.Start();
        await Task.WhenAll(parked, completed).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DownloadStatus.Queued, managed.Status);
        Assert.Equal(0, managed.RetryCount);
        Assert.Equal(1, fixture.Source.Plans);
        Assert.Equal(1, fixture.Source.Downloads);
    }

    [Fact]
    public async Task RaceStartingDuringPlanningParksAfterTheListingDrains()
    {
        await using var fixture = new Fixture();
        var idle = true;
        fixture.Manager.BackgroundPullMayRun = () => idle;
        fixture.Source.AfterPlan = () => idle = false;
        var item = fixture.Item(true);
        var parked = fixture.When(value => value == item && value.StatusDetail?.Contains("racing") == true);
        fixture.Manager.Enqueue(item);
        fixture.Manager.Start();
        await parked.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, fixture.Source.Plans);
        Assert.Equal(0, fixture.Source.Downloads);
        Assert.Equal(0, item.RetryCount);
        Assert.False(fixture.Source.LastToken.IsCancellationRequested);
    }

    [Fact]
    public async Task CooperativeYieldParksWithoutRetryAndResumesWhenRacingIsIdle()
    {
        await using var fixture = new Fixture();
        var idle = true;
        fixture.Manager.BackgroundPullMayRun = () => idle;
        fixture.Source.BeforePayload = () => idle = false;
        var item = fixture.Item(true);
        var parked = fixture.When(value => value == item && value.StatusDetail?.Contains("racing") == true);
        var completed = fixture.When(value => value == item && value.Status == DownloadStatus.Completed);
        fixture.Manager.Enqueue(item);
        fixture.Manager.Start();
        await parked.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, item.RetryCount);
        Assert.False(fixture.Source.LastToken.IsCancellationRequested);
        fixture.Source.BeforePayload = null;
        idle = true;
        await completed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, fixture.Source.Downloads);
        Assert.Equal(0, item.RetryCount);
    }

    [Fact]
    public async Task CancellingAParkedPullPreventsDelayedResurrection()
    {
        await using var fixture = new Fixture();
        var idle = false;
        fixture.Manager.BackgroundPullMayRun = () => idle;
        var item = fixture.Item(true);
        var parked = fixture.When(value => value == item && value.StatusDetail?.Contains("racing") == true);
        fixture.Manager.Enqueue(item);
        fixture.Manager.Start();
        await parked.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Manager.Cancel(item.Id);
        idle = true;
        await Task.Delay(1200);
        Assert.Equal(DownloadStatus.Cancelled, item.Status);
        Assert.Equal(0, fixture.Source.Plans);
    }

    [Theory]
    [InlineData(16L, 1)]
    [InlineData(0L, 0)]
    public async Task MeasurementUsesActualBytesAndIsolatesObserverFailures(long actualBytes, int expectedSamples)
    {
        await using var fixture = new Fixture();
        fixture.Source.ActualBytes = actualBytes;
        var item = fixture.Item(false);
        item.TotalBytes = 100_000;
        var samples = new List<(long Bytes, TimeSpan Elapsed)>();
        fixture.Manager.TransferMeasured += (_, _) => throw new InvalidOperationException("isolated observer");
        fixture.Manager.TransferMeasured += (bytes, elapsed) => samples.Add((bytes, elapsed));
        var completed = fixture.When(value => value == item && value.Status == DownloadStatus.Completed);
        fixture.Manager.Enqueue(item);
        fixture.Manager.Start();
        await completed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(expectedSamples, samples.Count);
        if (expectedSamples > 0)
        {
            Assert.Equal(actualBytes, samples[0].Bytes);
            Assert.True(samples[0].Elapsed > TimeSpan.Zero);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-background-" + Guid.NewGuid().ToString("N"));
        public DownloadManager Manager { get; }
        public FakeSource Source { get; } = new();
        public Fixture()
        {
            Directory.CreateDirectory(_root);
            Manager = new(DownloadStore.ForFile(Path.Combine(_root, "queue.json")), Source,
                new DownloadConfig { AutoExtract = false, VerifySfv = false, SkipIncompleteReleases = false,
                    MaxConcurrentDownloads = 1, MaxRetries = 0 });
            Manager.BackgroundPullRecheckSeconds = 1;
        }
        public DownloadItem Item(bool background) => new()
        {
            BackgroundWantedPull = background, RemotePath = "/movie/" + Guid.NewGuid().ToString("N") + ".mkv",
            LocalPath = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".mkv"), ReleaseName = "Example.Movie.2026-GROUP"
        };
        public Task When(Func<DownloadItem, bool> predicate)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Manager.DownloadStatusChanged += item => { if (predicate(item)) completion.TrySetResult(); };
            return completion.Task;
        }
        public async ValueTask DisposeAsync()
        {
            await Manager.StopAsync();
            Manager.Dispose();
            Manager.Store.Flush();
            Directory.Delete(_root, true);
        }
    }

    private sealed class FakeSource : IDownloadSource
    {
        public bool IsInCooldown => false;
        public int Plans;
        public int Downloads;
        public long ActualBytes = 16;
        public Action? AfterPlan;
        public Action? BeforePayload;
        public CancellationToken LastToken;
        public Task<DownloadTreePlan> PlanAsync(string remotePath, string localPath, CancellationToken ct)
        {
            LastToken = ct;
            Plans++;
            AfterPlan?.Invoke();
            return Task.FromResult(new DownloadTreePlan(false, [new(remotePath, localPath, 16)], []));
        }
        public Task DownloadAsync(IReadOnlyList<FtpDownloadFile> files, IProgress<DownloadProgress>? progress,
            CancellationToken ct) => DownloadAsync(files, progress, ct, new());
        public async Task DownloadAsync(IReadOnlyList<FtpDownloadFile> files, IProgress<DownloadProgress>? progress,
            CancellationToken ct, DownloadExecutionPolicy policy)
        {
            LastToken = ct;
            Downloads++;
            BeforePayload?.Invoke();
            if (policy.YieldWhen?.Invoke() == true) throw new BackgroundPullYieldedException();
            await Task.Delay(1, ct);
            policy.OnBytes?.Invoke(ActualBytes);
            foreach (var file in files) await File.WriteAllBytesAsync(file.LocalPath, new byte[16], ct);
        }
        public Task RefreshManifestAsync(FtpDownloadFile file, CancellationToken ct) => Task.CompletedTask;
    }
}
