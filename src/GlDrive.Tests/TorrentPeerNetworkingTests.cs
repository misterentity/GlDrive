using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using GlDrive.Config;
using GlDrive.Downloads.Segmented;
using GlDrive.Player;
using MonoTorrent;
using MonoTorrent.Connections.Peer;
using Xunit;

namespace GlDrive.Tests;

public sealed class TorrentPeerNetworkingTests
{
    private static readonly NetworkPath First = new("first", "First loopback route", IPAddress.Parse("127.0.0.1"), null);
    private static readonly NetworkPath Second = new("second", "Second loopback route", IPAddress.Parse("127.0.0.2"), null);

    [Fact]
    public async Task Six_live_connections_bind_both_source_routes_and_account_exact_received_bytes()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var listener = Listen(IPAddress.Loopback);
        var ledger = new ConcurrentDictionary<string, long>();
        var pacer = new RecordingPacer();
        var factories = new TorrentPeerNetworking(() => [First, Second], pacer,
            recordBytes: (id, bytes) => ledger.AddOrUpdate(id, bytes, (_, count) => count + bytes)).ApplyTo(Factories.Default);
        var peers = new List<IPeerConnection>();
        var servers = new List<Socket>();
        try
        {
            for (var index = 0; index < 6; index++)
            {
                var peer = Create(factories, listener);
                peers.Add(peer);
                await Connect(peer).WaitAsync(deadline.Token);
                var server = await listener.AcceptSocketAsync(deadline.Token);
                servers.Add(server);
                Assert.Equal(index % 2 == 0 ? First.BindAddress : Second.BindAddress,
                    ((IPEndPoint)server.RemoteEndPoint!).Address);
                Assert.False(peer.IsIncoming);
                Assert.False(peer.Disposed);
                Assert.True(peer.CanReconnect);
            }

            var payload = Enumerable.Range(0, 257).Select(i => (byte)i).ToArray();
            await Task.WhenAll(peers.Select(async (peer, index) =>
            {
                var upload = new byte[] { (byte)index, 42, 99 };
                await SendPeer(peer, upload).WaitAsync(deadline.Token);
                Assert.Equal(upload, await ReceiveSocket(servers[index], upload.Length, deadline.Token));
                await SendSocket(servers[index], payload, deadline.Token);
                Assert.Equal(payload, await ReceivePeer(peer, payload.Length).WaitAsync(deadline.Token));
            }));

            Assert.Equal(3 * payload.Length, ledger[First.Id]);
            Assert.Equal(3 * payload.Length, ledger[Second.Id]);
            Assert.Equal(3 * payload.Length, pacer.Calls.Where(call => call.Network == First.Id).Sum(call => call.Bytes));
            Assert.Equal(3 * payload.Length, pacer.Calls.Where(call => call.Network == Second.Id).Sum(call => call.Bytes));
            Assert.All(pacer.Calls, call => Assert.True(call.Token.CanBeCanceled));
        }
        finally
        {
            foreach (var peer in peers) peer.Dispose();
            foreach (var server in servers) server.Dispose();
        }
        Assert.All(peers, peer => Assert.True(peer.Disposed));
    }

    [Fact]
    public async Task Next_connect_resolves_current_addresses_instead_of_retaining_a_stale_route()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = Listen(IPAddress.Loopback);
        IReadOnlyList<NetworkPath> selected = [First];
        var factories = new TorrentPeerNetworking(() => selected).ApplyTo(Factories.Default);
        using var first = Create(factories, listener);
        await Connect(first).WaitAsync(deadline.Token);
        using var firstServer = await listener.AcceptSocketAsync(deadline.Token);
        Assert.Equal(First.BindAddress, ((IPEndPoint)firstServer.RemoteEndPoint!).Address);

        using var second = Create(factories, listener);
        selected = [Second];
        await Connect(second).WaitAsync(deadline.Token);
        using var secondServer = await listener.AcceptSocketAsync(deadline.Token);
        Assert.Equal(Second.BindAddress, ((IPEndPoint)secondServer.RemoteEndPoint!).Address);
    }

    [Fact]
    public async Task An_exhausted_route_is_excluded_without_preventing_the_other_route_from_connecting()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = Listen(IPAddress.Loopback);
        var factories = new TorrentPeerNetworking(() => [First, Second], overAllowance: route => route.Id == First.Id)
            .ApplyTo(Factories.Default);
        for (var index = 0; index < 4; index++)
        {
            using var peer = Create(factories, listener);
            await Connect(peer).WaitAsync(deadline.Token);
            using var server = await listener.AcceptSocketAsync(deadline.Token);
            Assert.Equal(Second.BindAddress, ((IPEndPoint)server.RemoteEndPoint!).Address);
        }
    }

    [Fact]
    public async Task Failed_source_bind_tries_the_other_selected_route_and_accounts_only_that_route()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = Listen(IPAddress.Loopback);
        // TEST-NET is not a local interface; bind fails before any remote
        // connection can be attempted. The only destination remains loopback.
        var unavailable = new NetworkPath("unavailable", "Unassigned fixture route", IPAddress.Parse("192.0.2.255"), null);
        var ledger = new ConcurrentDictionary<string, long>();
        var factories = new TorrentPeerNetworking(() => [unavailable, Second],
            recordBytes: (id, bytes) => ledger.AddOrUpdate(id, bytes, (_, count) => count + bytes)).ApplyTo(Factories.Default);
        using var peer = Create(factories, listener);
        await Connect(peer).WaitAsync(deadline.Token);
        using var server = await listener.AcceptSocketAsync(deadline.Token);
        Assert.Equal(Second.BindAddress, ((IPEndPoint)server.RemoteEndPoint!).Address);
        await SendSocket(server, new byte[] { 1, 2, 3 }, deadline.Token);
        Assert.Equal(new byte[] { 1, 2, 3 }, await ReceivePeer(peer, 3).WaitAsync(deadline.Token));
        Assert.Equal(3, ledger[Second.Id]);
        Assert.False(ledger.ContainsKey(unavailable.Id));
    }

    [Fact]
    public async Task Exhausting_an_already_connected_route_prevents_further_receives()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = Listen(IPAddress.Loopback);
        var exhausted = false;
        var ledger = new ConcurrentQueue<long>();
        var factories = new TorrentPeerNetworking(() => [First], overAllowance: _ => exhausted,
            recordBytes: (_, bytes) => ledger.Enqueue(bytes)).ApplyTo(Factories.Default);
        using var peer = Create(factories, listener);
        await Connect(peer).WaitAsync(deadline.Token);
        using var server = await listener.AcceptSocketAsync(deadline.Token);
        await SendSocket(server, new byte[] { 7 }, deadline.Token);
        exhausted = true;
        await Assert.ThrowsAsync<NetworkAllowanceExhaustedException>(() => ReceiveOne(peer, new byte[8]).WaitAsync(deadline.Token));
        Assert.Empty(ledger);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Replacing_saved_network_settings_enforces_the_new_allowance_on_an_existing_connection(bool matchByLegacyName)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = Listen(IPAddress.Loopback);
        var directory = Path.Combine(Path.GetTempPath(), "gldrive-peer-allowance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var usage = new NetworkUsageStore(Path.Combine(directory, "usage.json"), () => new DateTime(2026, 10, 15, 12, 0, 0));
        var originalSettings = new NetworkPathConfig
        {
            AdapterId = matchByLegacyName ? "" : First.Id, AdapterName = "Fixture adapter",
            AllowanceGb = 0, Period = AllowancePeriod.Daily
        };
        var connectedRoute = First with { Settings = originalSettings };
        IReadOnlyList<NetworkPathConfig> savedSettings = [originalSettings];
        var factories = new TorrentPeerNetworking(() => [connectedRoute],
            overAllowance: path => TorrentPeerNetworking.IsOverCurrentAllowance(path, savedSettings, usage),
            recordBytes: usage.Add).ApplyTo(Factories.Default);
        try
        {
            using var peer = Create(factories, listener);
            await Connect(peer).WaitAsync(deadline.Token);
            using var server = await listener.AcceptSocketAsync(deadline.Token);
            await SendSocket(server, new byte[] { 1, 2, 3 }, deadline.Token);
            Assert.Equal(new byte[] { 1, 2, 3 }, await ReceivePeer(peer, 3).WaitAsync(deadline.Token));
            Assert.Equal(3, usage.UsedThisPeriod(First.Id, AllowancePeriod.Daily));

            // Settings save creates a new object/list; it does not mutate the
            // route's connection-time Settings instance.
            var replacement = new NetworkPathConfig
            {
                AdapterId = matchByLegacyName ? "" : First.Id, AdapterName = "FIXTURE ADAPTER",
                AllowanceGb = 3d / (1024 * 1024 * 1024), Period = AllowancePeriod.Daily
            };
            savedSettings = [replacement];
            Assert.NotSame(replacement, connectedRoute.Settings);
            Assert.False(usage.IsOverAllowance(connectedRoute)); // The stale object is still unlimited.
            await SendSocket(server, new byte[] { 7 }, deadline.Token);
            await Assert.ThrowsAsync<NetworkAllowanceExhaustedException>(() =>
                ReceiveOne(peer, new byte[8]).WaitAsync(deadline.Token));
            Assert.Equal(3, usage.UsedThisPeriod(First.Id, AllowancePeriod.Daily));

            // Raising the allowance must also be observed on this same socket,
            // whose pending byte was not consumed by the rejected receive.
            savedSettings = [new NetworkPathConfig
            {
                AdapterId = matchByLegacyName ? "" : First.Id, AdapterName = "Fixture adapter",
                AllowanceGb = 0, Period = AllowancePeriod.Daily
            }];
            Assert.Equal(new byte[] { 7 }, await ReceivePeer(peer, 1).WaitAsync(deadline.Token));
            Assert.Equal(4, usage.UsedThisPeriod(First.Id, AllowancePeriod.Daily));
            Assert.False(peer.Disposed);
            Assert.False(listener.Pending());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Empty_or_exhausted_selection_fails_without_an_implicit_default_route(bool exhausted)
    {
        using var listener = Listen(IPAddress.Loopback);
        var factories = new TorrentPeerNetworking(() => exhausted ? [First, Second] : [], overAllowance: _ => exhausted)
            .ApplyTo(Factories.Default);
        using var peer = factories.CreatePeerConnection(PeerUri(listener));
        if (peer != null)
        {
            var error = await Record.ExceptionAsync(() => Connect(peer).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.NotNull(error);
            Assert.IsNotType<TimeoutException>(error);
        }
        Assert.False(listener.Pending());
    }

    [Fact]
    public async Task Explicit_ipv4_routes_reject_ipv6_without_escaping_the_selection()
    {
        var factories = new TorrentPeerNetworking(() => [First, Second]).ApplyTo(Factories.Default);
        using var peer = factories.CreatePeerConnection(new Uri("ipv6://[::1]:1"));
        if (peer != null)
        {
            var error = await Record.ExceptionAsync(() => Connect(peer).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.NotNull(error);
            Assert.IsNotType<TimeoutException>(error);
            // Rejection must be the selected-address family guard, not an
            // attempted default-route connection failing at the remote port.
            if (error is SocketException socket) Assert.NotEqual(SocketError.ConnectionRefused, socket.SocketErrorCode);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_default_route_keeps_normal_ipv4_and_ipv6_connections(bool ipv6)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = Listen(ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback);
        var ledger = new ConcurrentDictionary<string, long>();
        var factories = new TorrentPeerNetworking(() => [NetworkPath.Default],
            recordBytes: (id, bytes) => ledger.AddOrUpdate(id, bytes, (_, count) => count + bytes)).ApplyTo(Factories.Default);
        using var peer = Create(factories, listener);
        await Connect(peer).WaitAsync(deadline.Token);
        using var server = await listener.AcceptSocketAsync(deadline.Token);
        Assert.Equal(ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback, ((IPEndPoint)server.RemoteEndPoint!).Address);
        await SendSocket(server, new byte[] { 12, 34, 56 }, deadline.Token);
        Assert.Equal(new byte[] { 12, 34, 56 }, await ReceivePeer(peer, 3).WaitAsync(deadline.Token));
        Assert.Equal(3, ledger[NetworkPath.DefaultId]);
    }

    [Fact]
    public async Task Dispose_before_connect_cancels_without_opening_a_socket()
    {
        using var listener = Listen(IPAddress.Loopback);
        var factories = new TorrentPeerNetworking(() => [First]).ApplyTo(Factories.Default);
        using var peer = Create(factories, listener);
        peer.Dispose();
        var error = await Record.ExceptionAsync(() => Connect(peer).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.NotNull(error);
        Assert.IsNotType<TimeoutException>(error);
        Assert.True(peer.Disposed);
        Assert.False(listener.Pending());
    }

    [Fact]
    public async Task Dispose_settles_an_active_receive_and_closes_the_socket()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = Listen(IPAddress.Loopback);
        var factories = new TorrentPeerNetworking(() => [First]).ApplyTo(Factories.Default);
        using var peer = Create(factories, listener);
        await Connect(peer).WaitAsync(deadline.Token);
        using var server = await listener.AcceptSocketAsync(deadline.Token);
        var pending = ReceiveOne(peer, new byte[16]);
        Assert.False(pending.IsCompleted);
        peer.Dispose();
        int? count = null;
        var error = await Record.ExceptionAsync(async () => { count = await pending.WaitAsync(deadline.Token); });
        Assert.True(error is null or SocketException or ObjectDisposedException or OperationCanceledException,
            "Disposal must settle the pending socket operation.");
        if (error == null) Assert.Equal(0, count);
        var buffer = new byte[1];
        try { Assert.Equal(0, await server.ReceiveAsync(buffer, SocketFlags.None, deadline.Token)); }
        catch (SocketException socket)
        {
            // Disposing a socket with an outstanding read may close with RST
            // instead of FIN. Both prove closure; a deadline or other error does not.
            Assert.True(socket.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted);
        }
        Assert.True(peer.Disposed);
    }

    [Fact]
    public async Task Dispose_cancels_owned_pacing_after_already_received_bytes_are_recorded()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = Listen(IPAddress.Loopback);
        var pacer = new BlockingPacer();
        var ledger = new ConcurrentDictionary<string, long>();
        var factories = new TorrentPeerNetworking(() => [Second], pacer,
            recordBytes: (id, bytes) => ledger.AddOrUpdate(id, bytes, (_, count) => count + bytes)).ApplyTo(Factories.Default);
        using var peer = Create(factories, listener);
        await Connect(peer).WaitAsync(deadline.Token);
        using var server = await listener.AcceptSocketAsync(deadline.Token);
        await SendSocket(server, new byte[] { 1, 2, 3 }, deadline.Token);
        var receiving = ReceiveOne(peer, new byte[16]);
        await pacer.Entered.Task.WaitAsync(deadline.Token);
        Assert.Equal(Second.Id, pacer.Network);
        Assert.Equal(3, pacer.Bytes);
        Assert.Equal(3, ledger[Second.Id]);
        Assert.False(receiving.IsCompleted);
        peer.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => receiving.WaitAsync(deadline.Token));
        Assert.True(pacer.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task Remote_eof_does_not_debit_byte_allowance_or_invoke_pacing()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = Listen(IPAddress.Loopback);
        var ledger = new ConcurrentQueue<long>();
        var pacer = new RecordingPacer();
        var factories = new TorrentPeerNetworking(() => [First], pacer, recordBytes: (_, bytes) => ledger.Enqueue(bytes))
            .ApplyTo(Factories.Default);
        using var peer = Create(factories, listener);
        await Connect(peer).WaitAsync(deadline.Token);
        using var server = await listener.AcceptSocketAsync(deadline.Token);
        server.Shutdown(SocketShutdown.Send);
        Assert.Equal(0, await ReceiveOne(peer, new byte[8]).WaitAsync(deadline.Token));
        Assert.Empty(ledger);
        Assert.Empty(pacer.Calls);
    }

    private static TcpListener Listen(IPAddress address)
    {
        var listener = new TcpListener(address, 0);
        listener.Start();
        return listener;
    }

    private static Uri PeerUri(TcpListener listener)
    {
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        return new Uri($"{(endpoint.AddressFamily == AddressFamily.InterNetwork ? "ipv4" : "ipv6")}://{endpoint}");
    }

    private static IPeerConnection Create(Factories factories, TcpListener listener) =>
        Assert.IsAssignableFrom<IPeerConnection>(factories.CreatePeerConnection(PeerUri(listener)));

    private static async Task Connect(IPeerConnection peer) => await peer.ConnectAsync();
    private static async Task<int> ReceiveOne(IPeerConnection peer, Memory<byte> buffer) => await peer.ReceiveAsync(buffer);

    private static async Task SendPeer(IPeerConnection peer, byte[] data)
    {
        for (var offset = 0; offset < data.Length;)
        {
            var count = await peer.SendAsync(data.AsMemory(offset));
            Assert.True(count > 0);
            offset += count;
        }
    }

    private static async Task<byte[]> ReceivePeer(IPeerConnection peer, int length)
    {
        var result = new byte[length];
        for (var offset = 0; offset < length;)
        {
            var count = await peer.ReceiveAsync(result.AsMemory(offset));
            Assert.True(count > 0);
            offset += count;
        }
        return result;
    }

    private static async Task SendSocket(Socket socket, byte[] data, CancellationToken token)
    {
        for (var offset = 0; offset < data.Length;)
        {
            var count = await socket.SendAsync(data.AsMemory(offset), SocketFlags.None, token);
            Assert.True(count > 0);
            offset += count;
        }
    }

    private static async Task<byte[]> ReceiveSocket(Socket socket, int length, CancellationToken token)
    {
        var result = new byte[length];
        for (var offset = 0; offset < length;)
        {
            var count = await socket.ReceiveAsync(result.AsMemory(offset), SocketFlags.None, token);
            Assert.True(count > 0);
            offset += count;
        }
        return result;
    }

    private sealed class RecordingPacer : ITransferPacer
    {
        public ConcurrentQueue<(string? Network, int Bytes, CancellationToken Token)> Calls { get; } = new();
        public ValueTask WaitAsync(string? networkId, int bytes, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Enqueue((networkId, bytes, ct));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingPacer : ITransferPacer
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? Network { get; private set; }
        public int Bytes { get; private set; }
        public CancellationToken Token { get; private set; }
        public async ValueTask WaitAsync(string? networkId, int bytes, CancellationToken ct)
        {
            Network = networkId;
            Bytes = bytes;
            Token = ct;
            Entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }
    }
}
