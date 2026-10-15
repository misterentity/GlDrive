using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using GlDrive.Downloads;
using GlDrive.Player;
using MonoTorrent;
using MonoTorrent.BEncoding;
using MonoTorrent.Client;
using Xunit;

namespace GlDrive.Tests;

public sealed class TorrentPeerTransferTests
{
    [Fact]
    public async Task Loopback_peer_payload_survives_interruption_and_fresh_engine_resume_with_exact_checksum()
    {
        var root = Path.Combine(Path.GetTempPath(), "gldrive-torrent-peer-" + Guid.NewGuid().ToString("N"));
        var seedDirectory = Path.Combine(root, "seed");
        var payloadDirectory = Path.Combine(root, "download");
        var cache = Path.Combine(root, "cache");
        Directory.CreateDirectory(seedDirectory);
        var payload = Enumerable.Range(0, 512 * 1024).Select(i => (byte)(i * 17 + i / 251)).ToArray();
        var hashes = new List<byte>();
        for (var offset = 0; offset < payload.Length; offset += 16384)
            hashes.AddRange(SHA1.HashData(payload.AsSpan(offset, Math.Min(16384, payload.Length - offset))));
        var metadata = new BEncodedDictionary
        {
            ["info"] = new BEncodedDictionary
            {
                ["name"] = new BEncodedString("payload.bin"), ["length"] = new BEncodedNumber(payload.Length),
                ["piece length"] = new BEncodedNumber(16384), ["pieces"] = new BEncodedString(hashes.ToArray())
            }
        }.Encode();
        Assert.True(Torrent.TryLoad(metadata, out var torrent));
        // Give the only reachable peer exactly one valid piece. Its advertised
        // availability is the barrier: even an arbitrarily fast downloader cannot
        // finish before the progress observer gets to cancel the first operation.
        var partialSeed = (byte[])payload.Clone();
        for (var offset = 16384; offset < partialSeed.Length; offset += 16384)
            partialSeed[offset] ^= 0xff;
        await File.WriteAllBytesAsync(Path.Combine(seedDirectory, "payload.bin"), partialSeed);
        var metadataPath = Path.Combine(cache, "metadata", torrent.InfoHashes.V1OrV2.ToHex() + ".torrent");
        Directory.CreateDirectory(Path.GetDirectoryName(metadataPath)!);
        await File.WriteAllBytesAsync(metadataPath, metadata);
        var port = FreeLoopbackPort();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var seeder = new ClientEngine(Settings(Path.Combine(root, "seed-cache"), port));
        var seed = await seeder.AddAsync(torrent, seedDirectory,
            new TorrentSettingsBuilder { CreateContainingDirectory = false, AllowDht = false, AllowPeerExchange = false }.ToSettings());
        try
        {
            await seed.StartAsync();
            await Until(() => seed.State == TorrentState.Downloading, deadline.Token);
            Assert.Equal(1, seed.Bitfield.TrueCount);
            var item = new DownloadItem
            {
                SourceKind = DownloadSourceKind.Torrent, RemotePath = torrent.InfoHashes.V1OrV2.ToHex(),
                TorrentMagnet = "magnet:?xt=urn:btih:" + torrent.InfoHashes.V1OrV2.ToHex(), LocalPath = payloadDirectory
            };
            long interruptedAt = 0;
            byte[]? checkpoint = null;
            for (var phase = 0; phase < 2; phase++)
            {
                if (phase == 1)
                {
                    // Open the availability barrier only after cancellation has
                    // drained the first downloader and saved its partial checkpoint.
                    await seed.StopAsync(TimeSpan.FromSeconds(2));
                    Assert.Equal(TorrentState.Stopped, seed.State);
                    await File.WriteAllBytesAsync(Path.Combine(seedDirectory, "payload.bin"), payload);
                    await seed.HashCheckAsync(autoStart: false);
                    Assert.Equal(TorrentState.Stopped, seed.State);
                    Assert.True(seed.Bitfield.AllTrue);
                    await seed.StartAsync();
                    await Until(() => seed.State == TorrentState.Seeding, deadline.Token);
                }
                var engine = new ClientEngine(Settings(cache));
                var backend = new LocalPeerBackend(engine, new Uri($"ipv4://127.0.0.1:{port}"));
                await using var source = new TorrentDownloadSource(() => backend, TimeSpan.FromMilliseconds(10));
                await using var prepared = await source.PrepareAsync(item, null, deadline.Token);
                Assert.Empty(engine.Torrents);
                var resumePath = engine.Settings.GetFastResumePath(torrent.InfoHashes);
                if (checkpoint != null) Assert.Equal(checkpoint, await File.ReadAllBytesAsync(resumePath));
                using var operation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                var firstPhase = phase == 0;
                var progress = new InlineProgress(value =>
                {
                    if (firstPhase && value.DownloadedBytes > 0 && value.DownloadedBytes < payload.Length)
                    {
                        Interlocked.Exchange(ref interruptedAt, value.DownloadedBytes);
                        operation.Cancel();
                    }
                });
                var transfer = prepared.ExecuteAsync(progress, operation.Token);
                if (firstPhase)
                {
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transfer);
                    Assert.Equal(16384, interruptedAt);
                    Assert.Equal(1, seed.Bitfield.TrueCount);
                }
                else await transfer;
                Assert.True(backend.PeerAdded);
                Assert.True(backend.ReceivedBytes > 0);
                Assert.Empty(engine.Torrents);
                Assert.True(File.Exists(resumePath));
                checkpoint = await File.ReadAllBytesAsync(resumePath);
                Assert.NotEmpty(checkpoint);
                using (File.Open(Path.Combine(payloadDirectory, "payload.bin"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            }
            Assert.True(seed.Monitor.DataBytesSent > 0);
            Assert.Equal(SHA256.HashData(payload), SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(payloadDirectory, "payload.bin"))));
        }
        finally
        {
            await seed.StopAsync(TimeSpan.FromSeconds(2));
            await seeder.RemoveAsync(seed, RemoveMode.KeepAllData);
            seeder.Dispose();
            Directory.Delete(root, true);
        }
    }

    private static EngineSettings Settings(string cache, int? listenPort = null) => new EngineSettingsBuilder
    {
        CacheDirectory = cache, DhtEndPoint = null,
        ListenEndPoints = listenPort == null ? [] : new() { ["ipv4"] = new IPEndPoint(IPAddress.Loopback, listenPort.Value) },
        AllowLocalPeerDiscovery = false, AllowPortForwarding = false,
        AutoSaveLoadFastResume = true, AutoSaveLoadMagnetLinkMetadata = true,
        MaximumDownloadRate = 0, MaximumUploadRate = 0
    }.ToSettings();

    private static int FreeLoopbackPort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private static async Task Until(Func<bool> condition, CancellationToken ct)
    {
        while (!condition()) await Task.Delay(10, ct);
    }

    private sealed class InlineProgress(Action<DownloadProgress> action) : IProgress<DownloadProgress>
    { public void Report(DownloadProgress value) => action(value); }

    private sealed class LocalPeerBackend(ClientEngine engine, Uri peer) : ITorrentDownloadBackend
    {
        private readonly TorrentDownloadBackend _inner = new(new TorrentStreamService(engine, new TorrentContentPolicy(true)));
        public bool PeerAdded { get; private set; }
        public long ReceivedBytes { get; private set; }
        public Task<TorrentStreamService.ScreeningResult> ScreenAsync(MagnetLink magnet, string directory, Action<string>? status, CancellationToken ct) =>
            _inner.ScreenAsync(magnet, directory, status, ct);
        public async Task<ITorrentDownloadTransfer> AddAsync(Torrent torrent, string directory, IReadOnlyList<TorrentFileDecision> decisions, CancellationToken ct)
        {
            var transfer = await _inner.AddAsync(torrent, directory, decisions, ct);
            var manager = Assert.Single(engine.Torrents);
            PeerAdded = await manager.AddPeerAsync(new PeerInfo(peer));
            return new ObservedTransfer(transfer, manager, bytes => ReceivedBytes += bytes);
        }
        public Task SetRateAsync(long bytesPerSecond) => _inner.SetRateAsync(bytesPerSecond);
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private sealed class ObservedTransfer(ITorrentDownloadTransfer inner, TorrentManager manager, Action<long> record) : ITorrentDownloadTransfer
    {
        public TorrentDownloadSnapshot Snapshot => inner.Snapshot;
        public Task StartAsync() => inner.StartAsync();
        public async ValueTask DisposeAsync()
        {
            record(manager.Monitor.DataBytesReceived);
            await inner.DisposeAsync();
        }
    }
}
