using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using GlDrive.Downloads.Segmented;
using GlDrive.Player;
using MonoTorrent;
using MonoTorrent.BEncoding;
using MonoTorrent.Client;
using Xunit;

namespace GlDrive.Tests;

public sealed class TorrentDiscoveryIntegrationTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Tracker_discovery_falls_back_in_metadata_and_payload_and_uses_every_required_peer(int peerCount)
    {
        const int pieceSize = 16384;
        const string fileName = "synthetic-discovery.bin";
        var root = Path.Combine(Path.GetTempPath(), "gldrive-discovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var trackers = new ScriptedTrackers();
        var seedEngines = new List<ClientEngine>();
        var seeds = new List<TorrentManager>();
        TorrentStreamService? service = null;
        var payload = new byte[peerCount * pieceSize];
        new Random(7331).NextBytes(payload);
        var pieces = new List<byte>();
        for (var offset = 0; offset < payload.Length; offset += pieceSize)
            pieces.AddRange(SHA1.HashData(payload.AsSpan(offset, pieceSize)));
        var trackerTier = new BEncodedList();
        foreach (var uri in trackers.Uris) trackerTier.Add(new BEncodedString(uri.AbsoluteUri));
        var metadata = new BEncodedDictionary
        {
            ["announce"] = new BEncodedString(trackers.Uris[0].AbsoluteUri),
            ["announce-list"] = new BEncodedList { trackerTier },
            ["info"] = new BEncodedDictionary
            {
                ["name"] = new BEncodedString(fileName), ["length"] = new BEncodedNumber(payload.Length),
                ["piece length"] = new BEncodedNumber(pieceSize), ["pieces"] = new BEncodedString(pieces.ToArray())
            }
        }.Encode();
        Assert.True(Torrent.TryLoad(metadata, out var torrent));
        try
        {
            for (var index = 0; index < peerCount; index++)
            {
                // Disjoint verified availability is a deterministic barrier: all
                // peers must contribute, even if the whole transfer is very fast.
                var seedBytes = (byte[])payload.Clone();
                for (var piece = 0; piece < peerCount; piece++)
                    if (piece != index) seedBytes[piece * pieceSize] ^= 0xff;
                var seedDirectory = Path.Combine(root, "seed-" + index);
                Directory.CreateDirectory(seedDirectory);
                await File.WriteAllBytesAsync(Path.Combine(seedDirectory, fileName), seedBytes, deadline.Token);
                var port = FreeLoopbackPort();
                trackers.AddSeed(port);
                var seedEngine = new ClientEngine(Settings(Path.Combine(root, "seed-cache-" + index), port));
                seedEngines.Add(seedEngine);
                var seed = await seedEngine.AddAsync(torrent, seedDirectory, new TorrentSettingsBuilder
                {
                    CreateContainingDirectory = false, AllowDht = false, AllowPeerExchange = false
                }.ToSettings());
                seeds.Add(seed);
                await seed.HashCheckAsync(autoStart: false);
                Assert.Equal(TorrentState.Stopped, seed.State);
                Assert.Equal(1, seed.Bitfield.TrueCount);
                await seed.SetFilePriorityAsync(Assert.Single(seed.Files), Priority.DoNotDownload);
                await seed.StartAsync();
                await Until(() => seed.State is TorrentState.Downloading or TorrentState.Seeding, deadline.Token);
            }

            var receivedByRoute = new ConcurrentDictionary<string, long>();
            NetworkPath[] routes = peerCount == 1
                ? [new("route-one", "First loopback", IPAddress.Loopback, null)]
                : [new("route-one", "First loopback", IPAddress.Loopback, null),
                   new("route-two", "Second loopback", IPAddress.Parse("127.0.0.2"), null)];
            var peerNetworking = new TorrentPeerNetworking(() => routes,
                recordBytes: (id, bytes) => receivedByRoute.AddOrUpdate(id, bytes, (_, count) => count + bytes));
            var downloadEngine = new ClientEngine(Settings(Path.Combine(root, "download-cache"), FreeLoopbackPort()),
                peerNetworking.ApplyTo(TorrentTrackerFactories.Create()));
            service = new TorrentStreamService(downloadEngine, new TorrentContentPolicy(true));
            var downloadDirectory = Path.Combine(root, "download");
            Directory.CreateDirectory(downloadDirectory);
            var magnet = MagnetLink.Parse("magnet:?xt=urn:btih:" + torrent.InfoHashes.V1OrV2.ToHex() +
                string.Concat(trackers.Uris.Select(uri => "&tr=" + Uri.EscapeDataString(uri.AbsoluteUri))));

            // No metadata is pre-cached and no peer is injected into either
            // manager. The two actual UDP trackers are the sole discovery path.
            var screened = await service.ScreenQueueDownloadAsync(magnet, downloadDirectory, null, deadline.Token);
            Assert.True(screened.Ok, screened.Error);
            Assert.NotNull(screened.Torrent);
            Assert.Empty(downloadEngine.Torrents);
            Assert.Equal(trackers.Uris.Select(uri => uri.AbsoluteUri).Order(),
                screened.Torrent.AnnounceUrls.SelectMany(tier => tier).Order());
            await Until(() => trackers.PhaseStopped(0), deadline.Token);
            trackers.AssertFallback(phase: 0, stopped: true);
            Assert.All(seeds, seed => Assert.Equal(0, seed.Monitor.DataBytesSent));
            var metadataBytesByRoute = receivedByRoute.ToDictionary(pair => pair.Key, pair => pair.Value);

            TorrentManager payloadManager;
            await using (var transfer = await service.AddQueueDownloadAsync(screened.Torrent, downloadDirectory,
                             screened.Decisions, deadline.Token))
            {
                payloadManager = Assert.Single(downloadEngine.Torrents);
                await transfer.StartAsync();
                await Until(() => transfer.Snapshot.VerifiedComplete, deadline.Token);
                Assert.True(payloadManager.Bitfield.AllTrue);
                Assert.Equal(payload.Length, payloadManager.Monitor.DataBytesReceived);
                trackers.AssertFallback(phase: 1, stopped: false);
            }

            Assert.Equal(TorrentState.Stopped, payloadManager.State);
            Assert.Empty(downloadEngine.Torrents);
            await Until(() => trackers.PhaseStopped(1), deadline.Token);
            trackers.AssertFallback(phase: 1, stopped: true);
            Assert.All(seeds, seed =>
            {
                Assert.True(seed.Monitor.DataBytesSent >= pieceSize, "Every disjoint peer must supply its required piece.");
                Assert.Equal(0, seed.Monitor.DataBytesReceived);
            });
            Assert.All(routes, route => Assert.True(receivedByRoute.GetValueOrDefault(route.Id) -
                metadataBytesByRoute.GetValueOrDefault(route.Id) >= pieceSize,
                "Each selected source route must carry at least one required payload piece after metadata discovery."));
            var destination = Path.Combine(downloadDirectory, fileName);
            Assert.Equal(SHA256.HashData(payload), SHA256.HashData(await File.ReadAllBytesAsync(destination, deadline.Token)));
            // Stop/remove must have drained payload writers before ownership ends.
            using (File.Open(destination, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        }
        finally
        {
            if (service != null) await service.DisposeQueueEngineAsync();
            foreach (var engine in seedEngines)
            {
                await engine.StopAllAsync(TimeSpan.FromSeconds(2));
                Assert.All(engine.Torrents, manager => Assert.Equal(TorrentState.Stopped, manager.State));
                engine.Dispose();
            }
            Directory.Delete(root, recursive: true);
        }
    }

    private static EngineSettings Settings(string cache, int port) => new EngineSettingsBuilder
    {
        CacheDirectory = cache, DhtEndPoint = null,
        ListenEndPoints = new() { ["ipv4"] = new IPEndPoint(IPAddress.Loopback, port) },
        AllowLocalPeerDiscovery = false, AllowPortForwarding = false, AutoSaveLoadDhtCache = false,
        AutoSaveLoadFastResume = true, AutoSaveLoadMagnetLinkMetadata = true,
        MaximumConnections = 100, MaximumHalfOpenConnections = 16,
        MaximumDownloadRate = 0, MaximumUploadRate = 0
    }.ToSettings();

    private static int FreeLoopbackPort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private static async Task Until(Func<bool> predicate, CancellationToken token)
    {
        while (!predicate()) await Task.Delay(10, token);
    }

    private sealed class ScriptedTrackers : IAsyncDisposable
    {
        private readonly object _sync = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly UdpClient[] _sockets;
        private readonly Task[] _servers;
        private readonly HashSet<int> _seedPorts = [];
        private readonly List<Phase> _phases = [new()];
        private int _phase;
        public Uri[] Uris { get; }

        public ScriptedTrackers()
        {
            _sockets = [new(new IPEndPoint(IPAddress.Loopback, 0)), new(new IPEndPoint(IPAddress.Loopback, 0))];
            Uris = _sockets.Select(socket => new Uri($"udp://127.0.0.1:{((IPEndPoint)socket.Client.LocalEndPoint!).Port}/announce")).ToArray();
            _servers = _sockets.Select((socket, index) => Serve(socket, index)).ToArray();
        }

        public void AddSeed(int port) { lock (_sync) _seedPorts.Add(port); }

        public bool PhaseStopped(int phase)
        {
            lock (_sync) return _phases.Count > phase && _phases[phase].Stopped;
        }

        public void AssertFallback(int phase, bool stopped)
        {
            lock (_sync)
            {
                Assert.True(_phases.Count > phase, "Expected a new tracker discovery phase.");
                var observed = _phases[phase];
                Assert.NotNull(observed.FailedTracker);
                Assert.True(observed.FailedStarted > 0, "A real UDP failure response must precede the healthy fallback.");
                Assert.True(observed.HealthyStarted > 0, "The other UDP tracker must receive Started and return peers.");
                Assert.NotEqual(observed.FailedTracker, observed.HealthyTracker);
                if (stopped) Assert.True(observed.Stopped, "The manager must announce Stopped before relinquishing ownership.");
            }
        }

        private async Task Serve(UdpClient socket, int tracker)
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var incoming = await socket.ReceiveAsync(_stop.Token);
                    var request = incoming.Buffer;
                    if (request.Length < 16 || !IPAddress.IsLoopback(incoming.RemoteEndPoint.Address)) continue;
                    var action = BinaryPrimitives.ReadInt32BigEndian(request.AsSpan(8));
                    byte[] response;
                    if (action == 0)
                    {
                        response = new byte[16];
                        BinaryPrimitives.WriteInt64BigEndian(response.AsSpan(8), 0x1234567890);
                    }
                    else if (action == 1 && request.Length >= 98)
                    {
                        var evt = BinaryPrimitives.ReadInt32BigEndian(request.AsSpan(80));
                        var port = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(96));
                        bool fail;
                        int[] peers;
                        lock (_sync)
                        {
                            fail = false;
                            peers = [];
                            if (!_seedPorts.Contains(port))
                            {
                                var phase = _phases[_phase];
                                if (evt == 3)
                                {
                                    if (phase.HealthyStarted > 0)
                                    {
                                        phase.Stopped = true;
                                        _phases.Add(new Phase());
                                        _phase++;
                                    }
                                }
                                else
                                {
                                    // TrackerTier randomizes candidates: whichever is
                                    // contacted first must fail, not a guessed URI order.
                                    if (evt == 2) phase.FailedTracker ??= tracker;
                                    fail = phase.FailedTracker == tracker;
                                    if (evt == 2 && fail) phase.FailedStarted++;
                                    if (!fail)
                                    {
                                        if (evt == 2) phase.HealthyStarted++;
                                        phase.HealthyTracker = tracker;
                                        peers = _seedPorts.ToArray();
                                    }
                                }
                            }
                        }
                        if (fail)
                        {
                            action = 3;
                            var message = Encoding.ASCII.GetBytes("Synthetic tracker rejection; try another tracker");
                            response = new byte[8 + message.Length];
                            message.CopyTo(response, 8);
                        }
                        else
                        {
                            response = new byte[20 + peers.Length * 6];
                            BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(8), 30);
                            BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(16), peers.Length);
                            for (var index = 0; index < peers.Length; index++)
                            {
                                IPAddress.Loopback.GetAddressBytes().CopyTo(response, 20 + index * 6);
                                BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(24 + index * 6), (ushort)peers[index]);
                            }
                        }
                    }
                    else continue;
                    BinaryPrimitives.WriteInt32BigEndian(response, action);
                    request.AsSpan(12, 4).CopyTo(response.AsSpan(4));
                    await socket.SendAsync(response, incoming.RemoteEndPoint, _stop.Token);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await Task.WhenAll(_servers);
            foreach (var socket in _sockets) socket.Dispose();
            _stop.Dispose();
        }

        private sealed class Phase
        {
            public int? FailedTracker { get; set; }
            public int? HealthyTracker { get; set; }
            public int FailedStarted { get; set; }
            public int HealthyStarted { get; set; }
            public bool Stopped { get; set; }
        }
    }
}
