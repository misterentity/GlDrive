using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace GlDrive.Downloads.Segmented;

/// <summary>
/// HttpClients pinned to one network. A bound path gets a ConnectCallback that binds the socket
/// to the adapter's address before connecting, so the OS sends it out that adapter regardless of
/// the routing table. The default path is a plain client.
/// </summary>
public static class NetworkHttpClients
{
    public static HttpClient Create(NetworkPath path, TimeSpan? timeout = null)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            AllowAutoRedirect = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
        };

        if (path.BindAddress is { } bind)
            handler.ConnectCallback = (ctx, ct) => ConnectBoundAsync(bind, ctx.DnsEndPoint, ct);

        var client = new HttpClient(handler, disposeHandler: true) { Timeout = timeout ?? TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("GlDrive");
        return client;
    }

    internal static async ValueTask<Stream> ConnectBoundAsync(IPAddress bind, DnsEndPoint target, CancellationToken ct)
    {
        // The target must be the bind address's family: an IPv4-bound socket cannot reach an IPv6 host.
        var family = bind.AddressFamily;
        var addresses = IPAddress.TryParse(target.Host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(target.Host, family, ct);

        Exception? last = null;
        foreach (var address in addresses.Where(a => a.AddressFamily == family))
        {
            var socket = new Socket(family, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                socket.Bind(new IPEndPoint(bind, 0));
                await socket.ConnectAsync(new IPEndPoint(address, target.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                socket.Dispose();
                last = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw new HttpRequestException(
            $"No {family} address of {target.Host} reachable from {bind}", last);
    }
}
