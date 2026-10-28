using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using GlDrive.Config;

namespace GlDrive.Services;

/// <summary>A single configured endpoint, TCP only. SOCKS failures never fall back to a direct route.</summary>
internal static class SiteTcpProbe
{
    internal static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    internal static async Task<bool> ConnectAsync(ConnectionConfig connection, CancellationToken ct,
        Func<ProxyConfig, string?>? proxyPassword = null)
    {
        if (!ValidEndpoint(connection.Host, connection.Port)) return false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Deadline);
        using var client = new TcpClient();
        try
        {
            if (connection.Proxy is { Enabled: true } proxy)
            {
                if (!ValidEndpoint(proxy.Host, proxy.Port)) return false;
                await client.ConnectAsync(proxy.Host, proxy.Port, timeout.Token).ConfigureAwait(false);
                var password = string.IsNullOrEmpty(proxy.Username) ? "" :
                    proxyPassword?.Invoke(proxy) ?? CredentialStore.GetProxyPassword(proxy.Host, proxy.Port, proxy.Username) ?? "";
                return await NegotiateSocksAsync(client.GetStream(), connection.Host, connection.Port,
                    proxy.Username, password, timeout.Token).ConfigureAwait(false);
            }
            await client.ConnectAsync(connection.Host, connection.Port, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is SocketException or IOException or ArgumentException or InvalidOperationException) { return false; }
    }

    internal static bool ValidEndpoint(string host, int port) => !string.IsNullOrWhiteSpace(host) && host.Length <= 253 &&
        !host.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) && port is > 0 and <= 65535;

    internal static byte[] ConnectRequest(string host, int port)
    {
        if (!ValidEndpoint(host, port)) throw new ArgumentException("Invalid TCP endpoint");
        byte type;
        byte[] address;
        if (IPAddress.TryParse(host, out var ip))
        {
            type = ip.AddressFamily == AddressFamily.InterNetwork ? (byte)1 : (byte)4;
            address = ip.GetAddressBytes();
        }
        else
        {
            type = 3;
            var name = Encoding.ASCII.GetBytes(new IdnMapping().GetAscii(host));
            if (name.Length is 0 or > 255) throw new ArgumentException("Invalid SOCKS target name");
            address = [(byte)name.Length, .. name];
        }
        return [5, 1, 0, type, .. address, (byte)(port >> 8), (byte)(port & 255)];
    }

    internal static async Task<bool> NegotiateSocksAsync(Stream stream, string host, int port,
        string username, string password, CancellationToken ct)
    {
        var authenticated = !string.IsNullOrEmpty(username);
        byte[] greeting = authenticated ? [5, 1, 2] : [5, 1, 0];
        await stream.WriteAsync(greeting, ct).ConfigureAwait(false);
        var choice = new byte[2];
        await stream.ReadExactlyAsync(choice, ct).ConfigureAwait(false);
        if (choice[0] != 5 || choice[1] != (authenticated ? 2 : 0)) return false;
        if (authenticated)
        {
            var userBytes = Encoding.UTF8.GetBytes(username);
            var passwordBytes = Encoding.UTF8.GetBytes(password);
            if (userBytes.Length is 0 or > 255 || passwordBytes.Length is 0 or > 255) return false;
            byte[] auth = [1, (byte)userBytes.Length, .. userBytes, (byte)passwordBytes.Length, .. passwordBytes];
            try { await stream.WriteAsync(auth, ct).ConfigureAwait(false); }
            finally { Array.Clear(auth); Array.Clear(passwordBytes); }
            await stream.ReadExactlyAsync(choice, ct).ConfigureAwait(false);
            if (choice[0] != 1 || choice[1] != 0) return false;
        }
        await stream.WriteAsync(ConnectRequest(host, port), ct).ConfigureAwait(false);
        var reply = new byte[4];
        await stream.ReadExactlyAsync(reply, ct).ConfigureAwait(false);
        if (reply[0] != 5 || reply[1] != 0 || reply[2] != 0) return false;
        var length = reply[3] switch { 1 => 4, 4 => 16, 3 => -1, _ => 0 };
        if (length == 0) return false;
        if (length == -1)
        {
            var domainLength = new byte[1];
            await stream.ReadExactlyAsync(domainLength, ct).ConfigureAwait(false);
            length = domainLength[0];
            if (length == 0) return false;
        }
        await stream.ReadExactlyAsync(new byte[length + 2], ct).ConfigureAwait(false);
        // No TLS, banner read, USER, PASS or other target protocol bytes.
        return true;
    }
}
