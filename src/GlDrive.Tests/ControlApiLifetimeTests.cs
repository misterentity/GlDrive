using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using GlDrive.Config;
using GlDrive.Services;
using Xunit;

namespace GlDrive.Tests;

public class ControlApiLifetimeTests
{
    private static (ControlApi api, int port) Start(TimeSpan timeout)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var config = new AppConfig();
        config.ControlApi.Enabled = true;
        config.ControlApi.Port = port;
        config.ControlApi.Token = "test-only";
        var api = new ControlApi(config, () => null, () => []) { RequestTimeout = timeout };
        api.Start();
        return (api, port);
    }

    private static async Task<TcpClient> Stall(int port)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var header = $"POST /races HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nAuthorization: Bearer test-only\r\nContent-Length: 100\r\n\r\n{{";
        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(header));
        return client;
    }

    private static async Task WaitFor(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }

    [Fact]
    public async Task Stalled_request_body_expires_and_releases_handler()
    {
        var (api, port) = Start(TimeSpan.FromMilliseconds(400));
        using (api)
        using (var stalled = await Stall(port))
        {
            await WaitFor(() => api.ActiveRequestCount == 1);
            await WaitFor(() => api.ActiveRequestCount == 0);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            http.DefaultRequestHeaders.Authorization = new("Bearer", "test-only");
            using var response = await http.GetAsync($"http://127.0.0.1:{port}/status");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task Saturated_api_rejects_excess_work_and_shutdown_drains_stalled_bodies()
    {
        var (api, port) = Start(TimeSpan.FromSeconds(30));
        var clients = new List<TcpClient>();
        try
        {
            for (var i = 0; i < ControlApi.MaxConcurrentRequests; i++) clients.Add(await Stall(port));
            await WaitFor(() => api.ActiveRequestCount == ControlApi.MaxConcurrentRequests);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var response = await http.GetAsync($"http://127.0.0.1:{port}/status");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            api.Dispose();
            Assert.Equal(0, api.ActiveRequestCount);
            api.Dispose();
        }
        finally { foreach (var client in clients) client.Dispose(); api.Dispose(); }
    }
}
