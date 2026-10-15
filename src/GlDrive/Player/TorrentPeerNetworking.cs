using System.IO;
using System.Net;
using System.Net.Sockets;
using GlDrive.Config;
using GlDrive.Downloads.Segmented;
using MonoTorrent;
using MonoTorrent.Connections;
using MonoTorrent.Connections.Peer;
using ReusableTasks;
using Serilog;

namespace GlDrive.Player;

/// <summary>Distributes outgoing peer sockets over the download networks, retaining one engine-wide peer budget.</summary>
internal sealed class TorrentPeerNetworking(
    Func<IReadOnlyList<NetworkPath>> resolveNetworks,
    ITransferPacer? pacer = null,
    Func<NetworkPath, bool>? overAllowance = null,
    Action<string, long>? recordBytes = null)
{
    private int _next = -1;
    private readonly ITransferPacer? _pacer = pacer;
    private readonly Func<NetworkPath, bool>? _overAllowance = overAllowance;
    private readonly Action<string, long>? _recordBytes = recordBytes;

    internal Factories ApplyTo(Factories original) => original
        .WithPeerConnectionCreator("ipv4", uri => new RoutedConnection(this, uri))
        .WithPeerConnectionCreator("ipv6", uri => new RoutedConnection(this, uri));

    internal static TorrentPeerNetworking ForDownloads(string? vpnAdapterName)
    {
        string? priorRoutes = null;
        IReadOnlyList<NetworkPath> Resolve()
        {
            IReadOnlyList<NetworkPath> paths;
            var config = DownloadNetworkServices.Config.MultiStream;
            if (string.IsNullOrWhiteSpace(vpnAdapterName))
                paths = NetworkCatalog.Choose(config.Networks, NetworkCatalog.EnumerateAdapters());
            else
            {
                // An explicit torrent VPN preference takes precedence over ordinary
                // download adapters. Resolve again for every connection after a reconnect.
                var address = VpnBinding.ResolveAddress(vpnAdapterName, VpnBinding.EnumerateAdapters());
                if (address != null && IPAddress.TryParse(address, out var bind))
                {
                    var adapter = NetworkCatalog.EnumerateAdapters().FirstOrDefault(a => a.IPv4 == address);
                    var id = adapter.Id ?? "torrent-vpn";
                    paths = [new NetworkPath(id, adapter.Name ?? vpnAdapterName, bind,
                        config.Networks.FirstOrDefault(n => n.AdapterId == id))];
                }
                else paths = [NetworkPath.Default]; // Existing warn-and-continue VPN policy.
            }
            var signature = string.Join("|", paths.Select(p => p.Id + ":" + p.BindAddress));
            if (!string.Equals(Interlocked.Exchange(ref priorRoutes, signature), signature, StringComparison.Ordinal))
            {
                if (paths.All(p => p.IsDefault) &&
                    (!string.IsNullOrWhiteSpace(vpnAdapterName) || config.Networks.Any(n => n.Enabled)))
                    Log.Warning("Torrent peer routing: selected adapters unavailable; using the default route");
                else
                    Log.Information("Torrent peer routing: {NetworkCount} network(s): {Networks}", paths.Count,
                        string.Join(", ", paths.Select(p => p.DisplayName)));
            }
            return paths;
        }
        return new(Resolve, DownloadNetworkServices.Pacer,
            path => IsOverCurrentAllowance(path, DownloadNetworkServices.Config.MultiStream.Networks,
                DownloadNetworkServices.Usage), DownloadNetworkServices.RecordBytes);
    }

    internal static bool IsOverCurrentAllowance(NetworkPath path, IEnumerable<NetworkPathConfig> settings,
        NetworkUsageStore usage)
    {
        // Saving Settings replaces the network config objects. A connected peer must
        // observe the new allowance instead of retaining its connection-time value.
        var current = settings.FirstOrDefault(n => n.AdapterId == path.Id) ??
            settings.FirstOrDefault(n => !string.IsNullOrEmpty(path.Settings?.AdapterName) &&
                string.Equals(n.AdapterName, path.Settings.AdapterName, StringComparison.OrdinalIgnoreCase));
        return usage.IsOverAllowance(path with { Settings = current });
    }

    private IReadOnlyList<NetworkPath> SelectPaths(AddressFamily family)
    {
        var selected = resolveNetworks();
        var eligible = selected.Where(p => (p.IsDefault || p.BindAddress!.AddressFamily == family) &&
                _overAllowance?.Invoke(p) != true)
            .DistinctBy(p => p.BindAddress).ToArray();
        if (eligible.Length == 0)
        {
            // No implicit unbound escape when selected IPv4 paths cannot carry IPv6,
            // or every selected network has exhausted its data allowance.
            if (selected.All(p => !p.IsDefault && p.BindAddress!.AddressFamily != family))
                throw new SocketException((int)SocketError.AddressFamilyNotSupported);
            throw new NetworkAllowanceExhaustedException("selected torrent networks");
        }
        var start = (int)(unchecked((uint)Interlocked.Increment(ref _next)) % (uint)eligible.Length);
        return Enumerable.Range(0, eligible.Length).Select(i => eligible[(start + i) % eligible.Length]).ToArray();
    }

    private sealed class Connector(TorrentPeerNetworking owner, Action<NetworkPath> connected) : ISocketConnector
    {
        public async ReusableTask<Socket> ConnectAsync(Uri uri, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var endpoint = new IPEndPoint(IPAddress.Parse(uri.Host), uri.Port);
            var paths = owner.SelectPaths(endpoint.AddressFamily);
            Exception? lastFailure = null;
            foreach (var path in paths)
            {
                token.ThrowIfCancellationRequested();
                var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    if (path.BindAddress is { } bind) socket.Bind(new IPEndPoint(bind, 0));
                    // Give another selected adapter a chance before the engine's
                    // overall connection timeout expires on a dead first route.
                    using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
                    if (paths.Count > 1) attempt.CancelAfter(TimeSpan.FromSeconds(3));
                    await socket.ConnectAsync(endpoint, attempt.Token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    connected(path);
                    return socket;
                }
                catch (Exception ex) when (ex is SocketException or OperationCanceledException)
                {
                    socket.Dispose();
                    token.ThrowIfCancellationRequested();
                    lastFailure = ex;
                }
                catch { socket.Dispose(); throw; }
            }
            throw new IOException("No selected network could connect to the torrent peer.", lastFailure);
        }
    }

    private sealed class RoutedConnection : IPeerConnection
    {
        private readonly TorrentPeerNetworking _owner;
        private readonly IPeerConnection _inner;
        private readonly CancellationTokenSource _stop = new();
        private readonly CancellationToken _token;
        private NetworkPath? _network;
        private int _disposed;

        public RoutedConnection(TorrentPeerNetworking owner, Uri uri)
        {
            _owner = owner;
            _token = _stop.Token;
            _inner = new SocketPeerConnection(uri, new Connector(owner, path => _network = path));
        }
        public ReadOnlyMemory<byte> AddressBytes => _inner.AddressBytes;
        public bool CanReconnect => _inner.CanReconnect;
        public bool Disposed => Volatile.Read(ref _disposed) != 0;
        public IPEndPoint? EndPoint => _inner.EndPoint;
        public bool IsIncoming => _inner.IsIncoming;
        public Uri Uri => _inner.Uri;
        public ReusableTask ConnectAsync() => _inner.ConnectAsync();
        public ReusableTask<int> SendAsync(Memory<byte> buffer) => _inner.SendAsync(buffer);

        public async ReusableTask<int> ReceiveAsync(Memory<byte> buffer)
        {
            _token.ThrowIfCancellationRequested();
            var path = _network ?? throw new InvalidOperationException("Peer is not connected.");
            if (_owner._overAllowance?.Invoke(path) == true)
                throw new NetworkAllowanceExhaustedException(path.DisplayName);
            var count = await _inner.ReceiveAsync(buffer);
            if (count > 0)
            {
                // Include received peer protocol bytes: this is network usage,
                // not the hash-verified payload progress shown in Downloads.
                _owner._recordBytes?.Invoke(path.Id, count);
                if (_owner._pacer != null) await _owner._pacer.WaitAsync(path.Id, count, _token);
            }
            return count;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _stop.Cancel();
            _inner.Dispose();
            _stop.Dispose();
        }
    }
}
