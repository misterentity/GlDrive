using System.IO.Pipes;
using System.Net;
using System.Text;
using GlDrive.Mcp;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace GlDrive.Mcp.Tests;

public sealed class AuthorizationBridgeTests
{
    [Theory]
    [InlineData(403, "{\"code\":\"scope_denied\"}", "scope_denied")]
    [InlineData(403, "{\"code\":\"approval_denied\"}", "approval_denied")]
    [InlineData(200, "{\"jobId\":\"fixture-job\",\"status\":\"denied\",\"code\":\"approval_denied\"}", "approval_denied")]
    public async Task Stdio_protocol_returns_tool_errors_and_keeps_the_same_authenticated_connection(int status, string refusal, string code)
    {
        var calls = 0; var catalogs = 0; var connectionReads = 0;
        using var api = new ControlApiClient(new Handler(request =>
        {
            Assert.Equal("scoped-fixture-token", request.Headers.Authorization!.Parameter);
            if (request.RequestUri!.AbsolutePath == "/automation/tools")
            {
                catalogs++;
                return Response(200, "{\"tools\":[{\"name\":\"fixture.read\",\"inputSchema\":{\"type\":\"object\"}}]}");
            }
            return ++calls == 1 ? Response(status, refusal) : Response(200, "{\"value\":42}");
        }), () => { connectionReads++; return new(8756, "scoped-fixture-token"); });
        using var input = new AnonymousPipeServerStream(PipeDirection.Out);
        using var serverInput = new AnonymousPipeClientStream(PipeDirection.In, input.ClientSafePipeHandle);
        using var output = new AnonymousPipeServerStream(PipeDirection.Out);
        using var clientOutput = new AnonymousPipeClientStream(PipeDirection.In, output.ClientSafePipeHandle);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var options = new McpBridge(api).CreateOptions();
        // The same line-framed transport used by stdio, with isolated streams instead
        // of process-wide Console handles and a fake HTTP handler instead of HTTP.sys.
        await using var server = McpServer.Create(new StreamServerTransport(serverInput, output), options);
        var running = server.RunAsync(deadline.Token);
        await using var client = await McpClient.CreateAsync(new StreamClientTransport(input, clientOutput), cancellationToken: deadline.Token);
        Assert.Equal(2, (await client.ListToolsAsync(cancellationToken: deadline.Token)).Count);
        var denied = await client.CallToolAsync("fixture.read", cancellationToken: deadline.Token);
        Assert.True(denied.IsError);
        var error = Assert.IsType<TextContentBlock>(Assert.Single(denied.Content)).Text;
        Assert.Contains(code, error);
        Assert.DoesNotContain("token rejected", error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("remove", error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("scoped-fixture-token", error);
        var next = await client.CallToolAsync("fixture.read", cancellationToken: deadline.Token);
        Assert.False(next.IsError); Assert.Equal(42, next.StructuredContent!.Value.GetProperty("value").GetInt32());
        Assert.Equal(2, calls); Assert.Equal(1, catalogs); Assert.Equal(3, connectionReads);
        await client.DisposeAsync();
        deadline.Cancel();
        try { await running; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Rejected_credentials_never_advise_removing_a_scoped_override()
    {
        using var api = new ControlApiClient(new Handler(_ => Response(401, "{\"code\":\"unauthorized\"}")), () => new(8756, "scoped-fixture-token"));
        var error = await Assert.ThrowsAsync<BridgeException>(() => api.CallAsync("fixture.read", null, default));
        Assert.Contains("same client's credential", error.Message);
        Assert.DoesNotContain("remove", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static HttpResponseMessage Response(int status, string body) => new((HttpStatusCode)status)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(send(request));
    }
}
