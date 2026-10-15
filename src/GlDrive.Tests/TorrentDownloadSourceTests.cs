using System.IO;
using GlDrive.Downloads;
using GlDrive.Player;
using MonoTorrent;
using MonoTorrent.BEncoding;
using MonoTorrent.Client;
using System.Security.Cryptography;
using System.Reflection;
using Xunit;

namespace GlDrive.Tests;

public sealed class TorrentDownloadSourceTests
{
    [Theory]
    [InlineData(7)]
    [InlineData(100)]
    public async Task Queued_torrent_can_use_the_engine_connection_budget_without_a_hidden_sixty_peer_cap(int budget)
    {
        var root = Path.Combine(Path.GetTempPath(), "gldrive-peer-budget-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new EngineSettingsBuilder
            {
                CacheDirectory = root, DhtEndPoint = null, ListenEndPoints = [],
                AllowLocalPeerDiscovery = false, AllowPortForwarding = false,
                MaximumConnections = budget
            }.ToSettings();
            using var engine = new ClientEngine(settings);
            using var service = new TorrentStreamService(engine, new TorrentContentPolicy(true));
            var torrent = TorrentOf(("video.mkv", 100));
            var decisions = new TorrentContentPolicy(true).EvaluateAll(torrent.Files.Select(f => f.Path), root).ToArray();
            await using var transfer = await service.AddQueueDownloadAsync(torrent, root, decisions, CancellationToken.None);
            Assert.Equal(budget, Assert.Single(engine.Torrents).Settings.MaximumConnections);
            Assert.Equal(budget, engine.Settings.MaximumConnections);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Owner_and_session_disposal_do_not_hold_one_lock_while_waiting_for_the_other(bool disposeOwner)
    {
        var backend = new FakeBackend(TorrentOf(("video.mkv", 100)));
        await using var source = Source(backend);
        await using var prepared = await source.PrepareAsync(Item(backend.Torrent), null, CancellationToken.None);
        const BindingFlags hidden = BindingFlags.NonPublic | BindingFlags.Instance;
        var ownerLock = typeof(TorrentDownloadSource).GetField("_sync", hidden)!.GetValue(source)!;
        var sessionLock = prepared.GetType().GetField("_sync", hidden)!.GetValue(prepared)!;
        var held = disposeOwner ? sessionLock : ownerLock;
        var probe = disposeOwner ? ownerLock : sessionLock;
        Task? disposal = null;
        var acquired = await Task.Run(() =>
        {
            lock (held)
            {
                disposal = Task.Run(async () => { if (disposeOwner) await source.DisposeAsync(); else await prepared.DisposeAsync(); });
                Assert.True(SpinWait.SpinUntil(() => disposeOwner
                    ? (bool)typeof(TorrentDownloadSource).GetField("_disposed", hidden)!.GetValue(source)!
                    : prepared.GetType().GetField("_disposal", hidden)!.GetValue(prepared) != null, TimeSpan.FromSeconds(2)));
                var entered = Monitor.TryEnter(probe, TimeSpan.FromMilliseconds(500));
                if (entered) Monitor.Exit(probe);
                return entered;
            }
        });
        await disposal!.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(acquired, "Disposal held its lock while entering the other object's cleanup.");
    }

    [Fact]
    public async Task Actual_engine_cached_metadata_verifies_local_bytes_closes_writers_and_preserves_restart_resume()
    {
        var root = Path.Combine(Path.GetTempPath(), "gldrive-torrent-engine-" + Guid.NewGuid().ToString("N"));
        var cache = Path.Combine(root, "cache");
        var payload = Path.Combine(root, "payload");
        Directory.CreateDirectory(payload);
        var bytes = Enumerable.Range(0, 1024).Select(i => (byte)i).ToArray();
        var metadata = TorrentData(SHA1.HashData(bytes), ("video.mkv", bytes.Length));
        Assert.True(Torrent.TryLoad(metadata, out var torrent));
        var item = Item(torrent);
        item.LocalPath = payload;
        var file = Path.Combine(payload, "video.mkv");
        await File.WriteAllBytesAsync(file, bytes);
        var metadataPath = Path.Combine(cache, "metadata", torrent.InfoHashes.V1OrV2.ToHex() + ".torrent");
        Directory.CreateDirectory(Path.GetDirectoryName(metadataPath)!);
        await File.WriteAllBytesAsync(metadataPath, metadata);
        try
        {
            byte[]? checkpoint = null;
            for (var run = 0; run < 2; run++)
            {
                var settings = new EngineSettingsBuilder
                {
                    CacheDirectory = cache, DhtEndPoint = null, ListenEndPoints = [],
                    AllowLocalPeerDiscovery = false, AllowPortForwarding = false,
                    AutoSaveLoadFastResume = true, AutoSaveLoadMagnetLinkMetadata = true
                }.ToSettings();
                var engine = new ClientEngine(settings);
                var service = new TorrentStreamService(engine, new TorrentContentPolicy(true));
                await using var source = new TorrentDownloadSource(() => new TorrentDownloadBackend(service), TimeSpan.FromMilliseconds(10));
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await using var prepared = await source.PrepareAsync(item, null, deadline.Token);
                Assert.Empty(engine.Torrents); // Screening never registers or starts payload managers.
                var resumePath = settings.GetFastResumePath(torrent.InfoHashes);
                if (checkpoint != null) Assert.Equal(checkpoint, await File.ReadAllBytesAsync(resumePath));
                await prepared.ExecuteAsync(null, deadline.Token);
                Assert.Empty(engine.Torrents);
                Assert.Equal(bytes, await File.ReadAllBytesAsync(file));
                using (File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                Assert.True(File.Exists(resumePath));
                Assert.True(File.Exists(metadataPath));
                checkpoint = await File.ReadAllBytesAsync(resumePath);
                Assert.NotEmpty(checkpoint);
                Assert.Empty(engine.Settings.ListenEndPoints);
                Assert.Null(engine.Settings.DhtEndPoint);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Metadata_probe_cannot_delete_fast_resume_even_when_cancelled(bool cancel)
    {
        var directory = Path.Combine(Path.GetTempPath(), "gldrive-resume-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "fixture.fresume");
        var checkpoint = new byte[] { 1, 5, 7, 13 };
        await File.WriteAllBytesAsync(path, checkpoint);
        try
        {
            var fetch = TorrentStreamService.PreserveResumeDuringMetadataAsync<int>(() =>
            {
                File.Delete(path); // MonoTorrent's internal RemoveAsync(... CacheDataOnly).
                return cancel ? Task.FromCanceled<int>(new CancellationToken(true)) : Task.FromResult(42);
            }, path);
            if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch);
            else Assert.Equal(42, await fetch);
            Assert.Equal(checkpoint, await File.ReadAllBytesAsync(path));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Preparation_screens_without_adding_or_creating_payload_and_selects_only_allowed_bytes()
    {
        var backend = new FakeBackend(TorrentOf(("video.mkv", 100), ("setup.exe", 25)));
        await using var source = Source(backend);
        var item = Item(backend.Torrent, skip: true);
        await using var prepared = await source.PrepareAsync(item, null, CancellationToken.None);
        Assert.Equal(1, backend.ScreenCalls);
        Assert.Equal(0, backend.AddCalls);
        Assert.Equal(100, prepared.TotalBytes);
        Assert.Contains(item.LocalPath, prepared.Directories);
        Assert.False(Directory.Exists(item.LocalPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Blocked_files_require_explicit_skip_consent_and_escapes_never_allow_it(bool escape)
    {
        var backend = new FakeBackend(TorrentOf(("video.mkv", 100), ("setup.exe", 25)));
        if (escape) backend.Decisions = [new("video.mkv", TorrentFileVerdict.Allow, ""),
            new("setup.exe", TorrentFileVerdict.EscapesSaveDirectory, "fixture escape")];
        await using var source = Source(backend);
        var failure = await Assert.ThrowsAsync<TorrentContentBlockedException>(() =>
            source.PrepareAsync(Item(backend.Torrent, skip: escape), null, CancellationToken.None));
        Assert.Equal(!escape, failure.CanSkipFiles);
        Assert.Single(failure.Decisions);
        Assert.Equal(0, backend.AddCalls);
    }

    [Fact]
    public async Task All_blocked_torrent_is_refused_even_with_consent()
    {
        var backend = new FakeBackend(TorrentOf(("setup.exe", 25)));
        await using var source = Source(backend);
        var failure = await Assert.ThrowsAsync<TorrentContentBlockedException>(() =>
            source.PrepareAsync(Item(backend.Torrent, skip: true), null, CancellationToken.None));
        Assert.False(failure.CanSkipFiles);
        Assert.Equal(0, backend.AddCalls);
    }

    [Fact]
    public async Task Cancel_during_metadata_never_adds_or_starts_a_payload()
    {
        var backend = new FakeBackend(TorrentOf(("video.mkv", 100))) { HoldMetadata = true };
        await using var source = Source(backend);
        using var caller = new CancellationTokenSource();
        var preparation = source.PrepareAsync(Item(backend.Torrent), null, caller.Token);
        await backend.ScreenEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparation.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(0, backend.AddCalls);
    }

    [Fact]
    public async Task Source_disposal_cancels_and_drains_metadata_before_disposing_engine()
    {
        var backend = new FakeBackend(TorrentOf(("video.mkv", 100))) { HoldMetadata = true };
        var source = Source(backend);
        var preparation = source.PrepareAsync(Item(backend.Torrent), null, CancellationToken.None);
        await backend.ScreenEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await source.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparation);
        Assert.True(backend.ScreenExited);
        Assert.Equal(1, backend.DisposeCalls);
        Assert.Equal(0, backend.AddCalls);
    }

    [Fact]
    public async Task One_hundred_percent_is_not_completion_and_verified_completion_waits_for_writers_to_close()
    {
        var transfer = new FakeTransfer { State = new(100, 50, 1, 2, "Downloading", false), HoldStop = true };
        var backend = new FakeBackend(TorrentOf(("video.mkv", 100)), transfer);
        await using var source = Source(backend);
        await using var prepared = await source.PrepareAsync(Item(backend.Torrent), null, CancellationToken.None);
        var reports = new List<DownloadProgress>();
        var run = prepared.ExecuteAsync(new InlineProgress(reports.Add), CancellationToken.None);
        await transfer.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Until(() => reports.Count > 0);
        Assert.False(run.IsCompleted);
        transfer.State = transfer.State with { VerifiedComplete = true };
        await transfer.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(run.IsCompleted);
        Assert.DoesNotContain(reports, p => p.StatusDetail == "Verified and saved");
        transfer.AllowStop.TrySetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("Verified and saved", reports[^1].StatusDetail);
        Assert.Equal(100, reports[^1].DownloadedBytes);
        Assert.Equal(1, transfer.DisposeCalls);
        await prepared.DisposeAsync();
        Assert.Equal(1, transfer.DisposeCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_and_disposal_drain_the_exact_operation_before_retry(bool disposePrepared)
    {
        var transfer = new FakeTransfer { HoldStop = true };
        var backend = new FakeBackend(TorrentOf(("video.mkv", 100)), transfer);
        await using var source = Source(backend);
        var prepared = await source.PrepareAsync(Item(backend.Torrent), null, CancellationToken.None);
        using var caller = new CancellationTokenSource();
        var run = prepared.ExecuteAsync(null, caller.Token);
        await transfer.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task? disposal = null;
        if (disposePrepared) disposal = prepared.DisposeAsync().AsTask(); else caller.Cancel();
        await transfer.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(run.IsCompleted);
        transfer.AllowStop.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(2)));
        if (disposal != null) await disposal.WaitAsync(TimeSpan.FromSeconds(2));
        await prepared.DisposeAsync();
        Assert.Equal(1, transfer.DisposeCalls);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => prepared.ExecuteAsync(null, CancellationToken.None));
        Assert.Equal(1, backend.AddCalls);
    }

    [Fact]
    public async Task Engine_error_fails_and_closes_transfer_instead_of_polling_forever()
    {
        var transfer = new FakeTransfer { State = new(20, 0, 0, 0, "Error", false, "fixture disk failure") };
        var backend = new FakeBackend(TorrentOf(("video.mkv", 100)), transfer);
        await using var source = Source(backend);
        await using var prepared = await source.PrepareAsync(Item(backend.Torrent), null, CancellationToken.None);
        var failure = await Assert.ThrowsAsync<IOException>(() => prepared.ExecuteAsync(null, CancellationToken.None));
        Assert.Contains("fixture disk failure", failure.Message);
        Assert.Equal(1, transfer.DisposeCalls);
    }

    [Fact]
    public async Task Progress_clamps_nonfinite_and_negative_values_without_inventing_ftp_lanes()
    {
        var transfer = new FakeTransfer { State = new(double.NaN, double.NegativeInfinity, 0, 0, "Downloading", false) };
        var backend = new FakeBackend(TorrentOf(("video.mkv", 100)), transfer);
        await using var source = Source(backend);
        await using var prepared = await source.PrepareAsync(Item(backend.Torrent), null, CancellationToken.None);
        using var caller = new CancellationTokenSource();
        var reports = new List<DownloadProgress>();
        var run = prepared.ExecuteAsync(new InlineProgress(reports.Add), caller.Token);
        await Until(() => reports.Count > 0);
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.All(reports, p => { Assert.Equal(0, p.DownloadedBytes); Assert.Equal(0, p.BytesPerSecond); Assert.Null(p.Lanes); Assert.Null(p.Map); });
        Assert.All(reports, p => Assert.StartsWith("Waiting for peers", p.StatusDetail));
    }

    [Fact]
    public async Task Identity_mismatch_is_refused_without_fetching_or_exposing_magnet()
    {
        var backend = new FakeBackend(TorrentOf(("video.mkv", 100)));
        await using var source = Source(backend);
        var item = Item(backend.Torrent);
        item.RemotePath = new string('0', 40);
        item.TorrentMagnet += "&tr=https%3A%2F%2Ffixture.invalid%2Fprivate-token";
        var failure = await Assert.ThrowsAsync<DownloadBlockedException>(() => source.PrepareAsync(item, null, CancellationToken.None));
        Assert.DoesNotContain("private-token", failure.ToString());
        Assert.Equal(0, backend.ScreenCalls);
        Assert.Equal(0, backend.AddCalls);
    }

    private static TorrentDownloadSource Source(FakeBackend backend) => new(() => backend, TimeSpan.FromMilliseconds(5));
    private static DownloadItem Item(Torrent torrent, bool skip = false) => new()
    {
        SourceKind = DownloadSourceKind.Torrent, RemotePath = torrent.InfoHashes.V1OrV2.ToHex(),
        TorrentMagnet = "magnet:?xt=urn:btih:" + torrent.InfoHashes.V1OrV2.ToHex(),
        LocalPath = Path.Combine(Path.GetTempPath(), "gldrive-torrent-fixture-" + Guid.NewGuid().ToString("N")),
        SkipBlockedTorrentFiles = skip
    };

    private static Torrent TorrentOf(params (string Path, long Length)[] files)
    {
        Assert.True(Torrent.TryLoad(TorrentData(new byte[20], files), out var torrent));
        return torrent;
    }

    private static byte[] TorrentData(byte[] pieceHashes, params (string Path, long Length)[] files)
    {
        var entries = new BEncodedList();
        foreach (var file in files)
        {
            var path = new BEncodedList();
            foreach (var segment in file.Path.Split('/')) path.Add(new BEncodedString(segment));
            entries.Add(new BEncodedDictionary { ["length"] = new BEncodedNumber(file.Length), ["path"] = path });
        }
        var dictionary = new BEncodedDictionary
        {
            ["info"] = new BEncodedDictionary
            {
                ["name"] = new BEncodedString("fixture"), ["piece length"] = new BEncodedNumber(16384),
                ["pieces"] = new BEncodedString(pieceHashes), ["files"] = entries
            }
        };
        return dictionary.Encode();
    }

    private static async Task Until(Func<bool> condition)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition()) await Task.Delay(5, limit.Token);
    }

    private sealed class InlineProgress(Action<DownloadProgress> action) : IProgress<DownloadProgress>
    { public void Report(DownloadProgress value) => action(value); }

    private sealed class FakeBackend(Torrent torrent, FakeTransfer? transfer = null) : ITorrentDownloadBackend
    {
        public Torrent Torrent { get; } = torrent;
        public IReadOnlyList<TorrentFileDecision>? Decisions { get; set; }
        public bool HoldMetadata { get; init; }
        public int ScreenCalls, AddCalls, DisposeCalls;
        public bool ScreenExited;
        public TaskCompletionSource ScreenEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<TorrentStreamService.ScreeningResult> ScreenAsync(MagnetLink magnet, string directory, Action<string>? status, CancellationToken ct)
        {
            ScreenCalls++;
            ScreenEntered.TrySetResult();
            try
            {
                if (HoldMetadata) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return new(Torrent, Decisions ?? new TorrentContentPolicy(true).EvaluateAll(Torrent.Files.Select(f => f.Path), directory).ToArray(), null);
            }
            finally { ScreenExited = true; }
        }
        public Task<ITorrentDownloadTransfer> AddAsync(Torrent value, string directory, IReadOnlyList<TorrentFileDecision> decisions, CancellationToken ct)
        {
            Assert.Same(Torrent, value);
            Assert.True(ScreenExited);
            AddCalls++;
            return Task.FromResult<ITorrentDownloadTransfer>(transfer ?? new FakeTransfer());
        }
        public Task SetRateAsync(long bytesPerSecond) => Task.CompletedTask;
        public ValueTask DisposeAsync() { DisposeCalls++; Assert.True(ScreenExited); return ValueTask.CompletedTask; }
    }

    private sealed class FakeTransfer : ITorrentDownloadTransfer
    {
        public TorrentDownloadSnapshot State { get; set; } = new(0, 0, 0, 0, "Downloading", false);
        public TorrentDownloadSnapshot Snapshot => State;
        public bool HoldStop { get; init; }
        public int DisposeCalls;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StopEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowStop { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task StartAsync() { Started.TrySetResult(); return Task.CompletedTask; }
        public async ValueTask DisposeAsync()
        {
            DisposeCalls++;
            StopEntered.TrySetResult();
            if (HoldStop) await AllowStop.Task;
        }
    }
}
