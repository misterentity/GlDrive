using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using GlDrive.Mcp;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace GlDrive.Mcp.Tests;

public sealed class BridgeTests
{
    [Fact]
    public void EnvironmentOverridesNeedNoConfigAndNeverFormatTheToken()
    {
        var settings = ControlConnection.Load(key => key switch
        {
            "GLDRIVE_CONTROL_TOKEN" => "test-secret",
            "GLDRIVE_CONTROL_PORT" => "9876",
            _ => null
        }, "missing.json");
        Assert.Equal("http://127.0.0.1:9876/", settings.BaseUri.AbsoluteUri);
        Assert.DoesNotContain("test-secret", settings.ToString());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("http://example.com")]
    [InlineData("8756/path")]
    public void InvalidPortCannotRedirectCredentials(string port)
    {
        Assert.Throws<BridgeException>(() => ControlConnection.Load(key => key switch
        {
            "GLDRIVE_CONTROL_TOKEN" => "test-secret", "GLDRIVE_CONTROL_PORT" => port, _ => null
        }, "missing.json"));
    }

    [Fact]
    public void ConfigIsCaseInsensitiveAndReadsBomButRejectsDisabledApi()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{\"controlApi\":{\"enabled\":true,\"port\":9876,\"token\":\"test-secret\"}}", new UTF8Encoding(true));
            Assert.Equal(9876, ControlConnection.Load(_ => null, path).Port);
            File.WriteAllText(path, "{\"ControlApi\":{\"Enabled\":false,\"Token\":\"test-secret\"}}");
            Assert.Contains("disabled", Assert.Throws<BridgeException>(() => ControlConnection.Load(_ => null, path)).Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TransportNeverUsesProxyRedirectsOrCookies()
    {
        using var handler = ControlApiClient.CreateHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
        Assert.False(handler.UseCookies);
    }

    [Fact]
    public async Task RequestsUseBearerAndPreserveTypedArguments()
    {
        using var api = new ControlApiClient(new StubHandler(async request =>
        {
            Assert.Equal("http://127.0.0.1:8756/automation/call", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("test-secret", request.Headers.Authorization.Parameter);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("config.patch", body.RootElement.GetProperty("name").GetString());
            Assert.True(body.RootElement.GetProperty("arguments").GetProperty("enabled").GetBoolean());
            return JsonResponse("{\"saved\":true}");
        }), () => new(8756, "test-secret"));
        var result = await api.CallAsync("config.patch", new Dictionary<string, JsonElement>
        { ["enabled"] = JsonSerializer.SerializeToElement(true) }, CancellationToken.None);
        Assert.True(result.GetProperty("saved").GetBoolean());
    }

    [Theory]
    [InlineData(302)]
    [InlineData(401)]
    [InlineData(403)]
    public async Task HttpErrorsAreActionableAndNeverRevealToken(int code)
    {
        using var api = new ControlApiClient(new StubHandler(_ => Task.FromResult(JsonResponse("test-secret", code))), () => new(8756, "test-secret"));
        var exception = await Assert.ThrowsAsync<BridgeException>(() => api.GetCatalogAsync(CancellationToken.None));
        Assert.DoesNotContain("test-secret", exception.Message);
    }

    [Fact]
    public async Task ServerErrorsRedactTokenAndWritesAreNotRetried()
    {
        int attempts = 0;
        using var api = new ControlApiClient(new StubHandler(_ =>
        {
            attempts++;
            return Task.FromResult(JsonResponse("{\"error\":\"test-secret\"}", 409));
        }), () => new(8756, "test-secret"));
        var exception = await Assert.ThrowsAsync<BridgeException>(() => api.CallAsync("test.write", null, CancellationToken.None));
        Assert.DoesNotContain("test-secret", exception.Message);
        Assert.Contains("409", exception.Message);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task OversizedChunkedResponseIsRejected()
    {
        using var api = new ControlApiClient(new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new NonSeekableStream(new byte[ControlApiClient.MaxResponseBytes + 1])) })), () => new(8756, "test-secret"));
        var exception = await Assert.ThrowsAsync<BridgeException>(() => api.GetCatalogAsync(CancellationToken.None));
        Assert.Contains("16 MiB", exception.Message);
    }

    [Fact]
    public async Task ClientCancellationIsPropagated()
    {
        using var api = new ControlApiClient(new StubHandler(_ => throw new OperationCanceledException()), () => new(8756, "test-secret"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => api.GetCatalogAsync(cancellation.Token));
    }

    [Theory]
    [InlineData("2024-11-05")]
    [InlineData("2025-03-26")]
    [InlineData("2025-11-25")]
    public async Task RealStdioServerInitializesOfflineAndExposesHelp(string version)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var client = await ConnectAsync(ReservePort(), version, deadline.Token);
        Assert.Equal("fmountr", client.ServerInfo.Name);
        var tools = await client.ListToolsAsync(cancellationToken: deadline.Token);
        Assert.Equal(McpBridge.StatusTool, Assert.Single(tools).Name);
        var status = await client.CallToolAsync(McpBridge.StatusTool, cancellationToken: deadline.Token);
        Assert.False(status.StructuredContent!.Value.GetProperty("connected").GetBoolean());
        Assert.DoesNotContain("test-secret", Assert.IsType<TextContentBlock>(Assert.Single(status.Content)).Text);
        var resources = await client.ListResourcesAsync(cancellationToken: deadline.Token);
        Assert.Equal(2, resources.Count);
        var help = await client.ReadResourceAsync("gldrive://mcp/help", cancellationToken: deadline.Token);
        Assert.Contains("GLDRIVE_CONFIG_PATH", Assert.IsType<TextResourceContents>(Assert.Single(help.Contents)).Text);
    }

    [Fact]
    public async Task RealStdioDiscoversAndCallsAppWithStructuredResultsAndToolErrors()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        int port = ReservePort();
        using var app = new FakeApp(port);
        await using var client = await ConnectAsync(port, "2025-11-25", deadline.Token);
        var tools = await client.ListToolsAsync(cancellationToken: deadline.Token);
        Assert.Contains(tools, tool => tool.Name == "app.read");
        Assert.Contains(tools, tool => tool.Name == "app.write");
        var result = await client.CallToolAsync("app.read", new Dictionary<string, object?> { ["value"] = 42 }, cancellationToken: deadline.Token);
        Assert.False(result.IsError);
        Assert.Equal(42, result.StructuredContent!.Value.GetProperty("value").GetInt32());
        var error = await client.CallToolAsync("app.write", cancellationToken: deadline.Token);
        Assert.True(error.IsError);
        Assert.Contains("validation_failed", Assert.IsType<TextContentBlock>(Assert.Single(error.Content)).Text);
        Assert.Equal(2, app.CallCount);
        Assert.True(app.AllAuthenticated);
    }

    [Fact]
    public async Task OfflineClientRecoversAndIsNotifiedWhenAppStarts()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        int port = ReservePort();
        await using var client = await ConnectAsync(port, "2025-11-25", deadline.Token);
        Assert.Single(await client.ListToolsAsync(cancellationToken: deadline.Token));
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registration = client.RegisterNotificationHandler("notifications/tools/list_changed", (_, _) =>
        { changed.TrySetResult(); return ValueTask.CompletedTask; });
        using var app = new FakeApp(port);
        var status = await client.CallToolAsync(McpBridge.StatusTool, cancellationToken: deadline.Token);
        Assert.True(status.StructuredContent!.Value.GetProperty("connected").GetBoolean());
        await changed.Task.WaitAsync(deadline.Token);
        Assert.Equal(3, (await client.ListToolsAsync(cancellationToken: deadline.Token)).Count);
    }

    [Fact]
    public async Task MalformedAppCatalogKeepsConnectionDiagnosticsUsable()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        int port = ReservePort();
        using var app = new FakeApp(port) { Catalog = "{\"tools\":[null]}" };
        await using var client = await ConnectAsync(port, "2025-11-25", deadline.Token);
        Assert.Equal(McpBridge.StatusTool, Assert.Single(await client.ListToolsAsync(cancellationToken: deadline.Token)).Name);
        var status = await client.CallToolAsync(McpBridge.StatusTool, cancellationToken: deadline.Token);
        Assert.False(status.StructuredContent!.Value.GetProperty("connected").GetBoolean());
        Assert.Contains("invalid tool catalog", status.StructuredContent.Value.GetProperty("message").GetString());
    }

    private static Task<McpClient> ConnectAsync(int port, string version, CancellationToken cancellationToken) => McpClient.CreateAsync(
        new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = "dotnet",
            Arguments = [typeof(McpBridge).Assembly.Location],
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["GLDRIVE_CONTROL_PORT"] = port.ToString(),
                ["GLDRIVE_CONTROL_TOKEN"] = "test-secret",
                ["GLDRIVE_CONFIG_PATH"] = "missing-test-config.json"
            }
        }), new McpClientOptions { ProtocolVersion = version }, cancellationToken: cancellationToken);

    private static int ReservePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static HttpResponseMessage JsonResponse(string json, int code = 200) => new((HttpStatusCode)code)
    { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request);
    }

    private sealed class NonSeekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }

    private sealed class FakeApp : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _loop;
        public int CallCount { get; private set; }
        public bool AllAuthenticated { get; private set; } = true;
        public string Catalog { get; set; } = "{\"tools\":[{\"name\":\"app.read\",\"inputSchema\":{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"integer\"}}}},{\"name\":\"app.write\",\"inputSchema\":{\"type\":\"object\",\"properties\":{}}}]}";

        public FakeApp(int port)
        {
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _loop = Task.Run(async () =>
            {
                try
                {
                    while (_listener.IsListening)
                    {
                        var request = await _listener.GetContextAsync();
                        AllAuthenticated &= request.Request.Headers["Authorization"] == "Bearer test-secret";
                        string response;
                        if (request.Request.Url!.AbsolutePath == "/automation/tools")
                            response = Catalog;
                        else
                        {
                            CallCount++;
                            using var body = await JsonDocument.ParseAsync(request.Request.InputStream);
                            if (body.RootElement.GetProperty("name").GetString() == "app.write")
                            { request.Response.StatusCode = 400; response = "{\"error\":\"invalid field\",\"code\":\"validation_failed\"}"; }
                            else response = body.RootElement.GetProperty("arguments").GetRawText();
                        }
                        byte[] bytes = Encoding.UTF8.GetBytes(response);
                        request.Response.ContentType = "application/json";
                        request.Response.ContentLength64 = bytes.Length;
                        await request.Response.OutputStream.WriteAsync(bytes);
                        request.Response.Close();
                    }
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { }
            });
        }

        public void Dispose()
        {
            _listener.Close();
            _loop.GetAwaiter().GetResult();
        }
    }
}
