using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using GlDrive.Config;
using GlDrive.Services;
using Xunit;

namespace GlDrive.Tests;

public sealed class McpSetupServiceTests : IDisposable
{
    private const string Secret = "synthetic-secret-not-for-output";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-mcp-setup-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void MissingPackagedAdapterIsReportedWithoutFindingAnUnrelatedExecutable()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "GlDrive.Mcp.exe"), "not the packaged location");
        var setup = McpSetupService.GetSetup(_root);
        Assert.False(setup.AdapterAvailable);
        Assert.Equal(Path.Combine(_root, "mcp", "GlDrive.Mcp.exe"), setup.AdapterPath);
        Assert.Contains("missing", setup.Status);
        Assert.Contains("Install or repair", setup.Status);
    }

    [Fact]
    public void CommandsQuoteApostrophesAndShellMetacharactersAsOneLiteralPath()
    {
        var directory = Path.Combine(_root, "O'Brien $value `literal & [test]");
        Directory.CreateDirectory(Path.Combine(directory, "mcp"));
        File.WriteAllText(Path.Combine(directory, "mcp", "GlDrive.Mcp.exe"), "synthetic executable presence");
        var setup = McpSetupService.GetSetup(directory);
        Assert.True(setup.AdapterAvailable);
        string quotedPath = "'" + setup.AdapterPath.Replace("'", "''") + "'";
        Assert.Equal("codex mcp add fmountr -- " + quotedPath, setup.CodexCommand);
        Assert.Equal("claude mcp add --transport stdio --scope user fmountr -- " + quotedPath, setup.ClaudeCodeCommand);
        Assert.Contains("O''Brien $value `literal & [test]", setup.CodexCommand);
        // Single-quoted PowerShell literals round-trip by unescaping doubled apostrophes;
        // dollars, backticks and ampersands stay inside that literal.
        string commandArgument = setup.CodexCommand["codex mcp add fmountr -- ".Length..];
        Assert.Equal(setup.AdapterPath, commandArgument[1..^1].Replace("''", "'"));
    }

    [Fact]
    public void DesktopSnippetIsValidJsonWithAbsolutePathAndNoCredentials()
    {
        var setup = McpSetupService.GetSetup(Path.Combine(_root, "O'Brien $value"));
        using var json = JsonDocument.Parse(setup.ClaudeDesktopJson);
        var server = json.RootElement.GetProperty("mcpServers").GetProperty("fmountr");
        Assert.Equal(setup.AdapterPath, server.GetProperty("command").GetString());
        Assert.True(Path.IsPathFullyQualified(server.GetProperty("command").GetString()!));
        Assert.Empty(server.GetProperty("args").EnumerateArray());
        Assert.False(server.TryGetProperty("env", out _));
        Assert.DoesNotContain("Token", setup.ClaudeDesktopJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DisabledApiIsNotContacted()
    {
        using var handler = new StubHandler((_, _) => throw new Xunit.Sdk.XunitException("Disabled API must not be contacted"));
        var result = await McpSetupService.ProbeAsync(new ControlApiConfig { Enabled = false }, handler, TimeSpan.FromSeconds(1));
        Assert.False(result.Connected);
        Assert.Contains("disabled", result.Message);
        Assert.Null(result.AppToolCount);
    }

    [Theory]
    [InlineData(0, Secret)]
    [InlineData(65536, Secret)]
    [InlineData(8756, "")]
    [InlineData(8756, "line\r\ninjection")]
    public async Task InvalidConnectionSettingsAreNotContacted(int port, string token)
    {
        int calls = 0;
        using var handler = new StubHandler((_, _) => { calls++; return Task.FromResult(JsonResponse(Catalog)); });
        var result = await McpSetupService.ProbeAsync(new ControlApiConfig { Enabled = true, Port = port, Token = token }, handler, TimeSpan.FromSeconds(1));
        Assert.False(result.Connected);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void ProbeDisablesRedirectsProxiesAndCookies()
    {
        using var handler = McpSetupService.CreateHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
        Assert.False(handler.UseCookies);
    }

    [Fact]
    public async Task ProbeUsesOnlyAuthenticatedLoopbackCatalogGetAndCountsTools()
    {
        using var handler = new StubHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("http://127.0.0.1:9987/automation/tools", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal(Secret, request.Headers.Authorization.Parameter);
            Assert.Null(request.Content);
            return Task.FromResult(JsonResponse(Catalog));
        });
        var result = await McpSetupService.ProbeAsync(Settings(9987), handler, TimeSpan.FromSeconds(1));
        Assert.True(result.Connected);
        Assert.Equal(2, result.AppToolCount);
        Assert.Equal(3, result.McpToolCount);
        Assert.DoesNotContain(Secret, result.ToString());
        Assert.Contains("registration is separate", result.Message);
    }

    [Theory]
    [InlineData(301, "redirect")]
    [InlineData(401, "authentication")]
    [InlineData(403, "authentication")]
    [InlineData(404, "no MCP catalog")]
    [InlineData(500, "HTTP 500")]
    public async Task ServerErrorsNeverRevealResponseBodiesOrCredentials(int code, string expectedMessage)
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(JsonResponse(Secret, code)));
        var result = await McpSetupService.ProbeAsync(Settings(), handler, TimeSpan.FromSeconds(1));
        Assert.False(result.Connected);
        Assert.Contains(expectedMessage, result.Message);
        Assert.DoesNotContain(Secret, result.ToString());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"tools\":{}}")]
    [InlineData("{\"tools\":[null]}")]
    [InlineData("{\"tools\":[{\"name\":\"app.status\"}]}")]
    [InlineData("{\"tools\":[{\"name\":\"a\",\"inputSchema\":{}},{\"name\":\"a\",\"inputSchema\":{}}]}")]
    public async Task MalformedCatalogCannotReportAHealthyConnection(string body)
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(JsonResponse(body)));
        var result = await McpSetupService.ProbeAsync(Settings(), handler, TimeSpan.FromSeconds(1));
        Assert.False(result.Connected);
        Assert.Contains("invalid MCP catalog", result.Message);
        Assert.Null(result.AppToolCount);
    }

    [Fact]
    public async Task ChunkedCatalogIsBoundedEvenWithoutContentLength()
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new NonSeekableStream(new byte[McpSetupService.MaxCatalogBytes + 1])) }));
        var result = await McpSetupService.ProbeAsync(Settings(), handler, TimeSpan.FromSeconds(1));
        Assert.False(result.Connected);
        Assert.Contains("2 MiB", result.Message);
    }

    [Fact]
    public async Task TimeoutHasFriendlyStatusAndExternalCancellationIsPreserved()
    {
        using var timeoutHandler = new StubHandler(async (_, token) =>
        { await Task.Delay(Timeout.InfiniteTimeSpan, token); return JsonResponse(Catalog); });
        var timeout = await McpSetupService.ProbeAsync(Settings(), timeoutHandler, TimeSpan.FromMilliseconds(30));
        Assert.False(timeout.Connected);
        Assert.Contains("in time", timeout.Message);
        using var cancellation = new CancellationTokenSource();
        using var cancelledHandler = new StubHandler((_, _) =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(Secret, cancellation.Token);
        });
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            McpSetupService.ProbeAsync(Settings(), cancelledHandler, TimeSpan.FromSeconds(1), cancellation.Token));
        Assert.DoesNotContain(Secret, error.Message);
    }

    [Fact]
    public async Task ConnectionFailureNeverForwardsExceptionDetails()
    {
        using var handler = new StubHandler((_, _) => throw new HttpRequestException("Authorization: " + Secret));
        var result = await McpSetupService.ProbeAsync(Settings(), handler, TimeSpan.FromSeconds(1));
        Assert.False(result.Connected);
        Assert.Contains("Cannot reach", result.Message);
        Assert.DoesNotContain(Secret, result.ToString());
    }

    [Fact]
    public async Task DefaultTransportCanProbeALocalFakeAppWithoutAnyProductionSettings()
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serve = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync().WaitAsync(deadline.Token);
            Assert.Equal("GET", context.Request.HttpMethod);
            Assert.Equal("/automation/tools", context.Request.Url!.AbsolutePath);
            Assert.Equal("Bearer " + Secret, context.Request.Headers["Authorization"]);
            byte[] bytes = Encoding.UTF8.GetBytes(Catalog);
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, deadline.Token);
            context.Response.Close();
        }, deadline.Token);
        var result = await McpSetupService.ProbeAsync(Settings(port), deadline.Token);
        await serve;
        Assert.True(result.Connected);
        Assert.Equal(2, result.AppToolCount);
    }

    private const string Catalog = "{\"tools\":[{\"name\":\"app.status\",\"inputSchema\":{\"type\":\"object\"}},{\"name\":\"config_get\",\"inputSchema\":{\"type\":\"object\"}}]}";
    private static ControlApiConfig Settings(int port = 8756) => new() { Enabled = true, Port = port, Token = Secret };
    private static HttpResponseMessage JsonResponse(string json, int code = 200) => new((HttpStatusCode)code)
    { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request, cancellationToken);
    }
    private sealed class NonSeekableStream(byte[] data) : MemoryStream(data) { public override bool CanSeek => false; }
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
