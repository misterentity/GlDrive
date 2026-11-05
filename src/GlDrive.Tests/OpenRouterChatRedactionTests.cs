using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using GlDrive.AiAgent;
using GlDrive.Config;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Both OpenRouter callers — the agent loop and AI Setup — post through
/// <see cref="OpenRouterChat"/>, which runs <see cref="OutboundPayloadRedactor"/> on every body.
/// The 10-27 review found the agent sending the API token and FiSH keys to a free model; these
/// prove each client's real request is redacted, and that an unverifiable redaction sends nothing.
/// </summary>
public sealed class OpenRouterChatRedactionTests
{
    private const string Secret = "fish-key-0f9e8d7c6b5a";
    private static readonly string Masked = ConfigSecretPointers.Mask(Secret)!;

    private sealed class RecordingHandler(string replyContent) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            var body = JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = replyContent } } } });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static IEnumerable<string> LiveSecrets() => [Secret];
    private static IEnumerable<string> InventoryUnavailable() => throw new InvalidOperationException("inventory unavailable");

    [Fact]
    public async Task Agent_request_body_is_redacted()
    {
        var handler = new RecordingHandler("""{"changes":[]}""");
        using var client = new AgentClient("key", "vendor/model", handler, LiveSecrets);

        var outcome = await client.RunAsync("system", $"telemetry row: key={Secret}", CancellationToken.None);

        Assert.NotNull(outcome.Result);
        var body = Assert.Single(handler.Bodies);
        Assert.DoesNotContain(Secret, body);
        Assert.Contains(Masked, body);
    }

    [Fact]
    public async Task Agent_sends_nothing_when_redaction_cannot_be_verified()
    {
        var handler = new RecordingHandler("""{"changes":[]}""");
        using var client = new AgentClient("key", "vendor/model", handler, InventoryUnavailable);

        var outcome = await client.RunAsync("system", "user", CancellationToken.None);

        Assert.Equal("outbound-redaction-refused", outcome.ErrorMessage);
        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task Ai_setup_request_body_is_redacted()
    {
        var handler = new RecordingHandler("""{"affils":["GRP"]}""");
        using var client = new OpenRouterClient("key", "vendor/model", handler, LiveSecrets);

        var result = await client.AnalyzeSiteRules($"1. Site key is {Secret}",
            new Dictionary<string, string> { ["TV"] = "/tv" }, [],
            ircMessages: [$"<bot> +OK {Secret}"]);

        Assert.NotNull(result);
        var body = Assert.Single(handler.Bodies);
        Assert.DoesNotContain(Secret, body);
        Assert.Contains(Masked, body);
    }

    [Fact]
    public async Task Ai_setup_sends_nothing_when_redaction_cannot_be_verified()
    {
        var handler = new RecordingHandler("""{"affils":["GRP"]}""");
        using var client = new OpenRouterClient("key", "vendor/model", handler, InventoryUnavailable);

        var result = await client.AnalyzeSiteRules("rules", new Dictionary<string, string>(), []);

        Assert.Null(result);
        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task Request_shapes_are_unchanged_per_caller()
    {
        var agentHandler = new RecordingHandler("""{"changes":[]}""");
        using (var agent = new AgentClient("key", "vendor/model", agentHandler, () => []))
            await agent.RunAsync("system", "user", CancellationToken.None);
        var setupHandler = new RecordingHandler("{}");
        using (var setup = new OpenRouterClient("key", "vendor/model", setupHandler, () => []))
            await setup.AnalyzeSiteRules("rules", new Dictionary<string, string>(), []);

        using var agentDoc = JsonDocument.Parse(agentHandler.Bodies.Single());
        Assert.Equal(0.2, agentDoc.RootElement.GetProperty("temperature").GetDouble());
        Assert.Equal(AgentClient.MaxOutputTokens, agentDoc.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal("json_object", agentDoc.RootElement.GetProperty("response_format").GetProperty("type").GetString());

        using var setupDoc = JsonDocument.Parse(setupHandler.Bodies.Single());
        Assert.Equal(0.1, setupDoc.RootElement.GetProperty("temperature").GetDouble());
        Assert.Equal(8000, setupDoc.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.False(setupDoc.RootElement.TryGetProperty("response_format", out _));
        Assert.Equal("system", setupDoc.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
    }
}
