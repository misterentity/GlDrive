using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using GlDrive.AiAgent;
using GlDrive.Config;
using GlDrive.Services.Automation;
using Xunit;

namespace GlDrive.Tests;

public sealed class AgentAutomationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-agent-automation-tests-" + Guid.NewGuid().ToString("N"));
    public AgentAutomationTests() => Directory.CreateDirectory(Path.Combine(_root, "ai-data"));
    public void Dispose() => Directory.Delete(_root, true);
    private AgentAutomation Create()
    {
        var freeze = new FreezeStore(Path.Combine(_root, "ai-data"));
        var audit = new AuditTrail(Path.Combine(_root, "ai-data"));
        return new(new(), () => null, () => freeze, () => audit, () => null, _root);
    }
    private static async Task<JsonNode> Call(AgentAutomation agent, string operation, object args) =>
        JsonSerializer.SerializeToNode(await agent.ExecuteAsync(operation, JsonSerializer.SerializeToElement(args), default))!;

    [Fact]
    public async Task FreezesPersistAndCanBeRemovedWithoutRunningAModel()
    {
        var agent = Create();
        await Call(agent, "agent_freeze", new { action = "set", pointer = "/servers/site-a/pool", note = "manual setting" });
        var saved = new FreezeStore(Path.Combine(_root, "ai-data"));
        Assert.True(saved.IsFrozen("/servers/site-a/pool/loginCap"));
        await Call(agent, "agent_freeze", new { action = "remove", pointer = "/servers/site-a/pool" });
        Assert.Empty(new FreezeStore(Path.Combine(_root, "ai-data")).All);
    }

    [Fact]
    public async Task MemoCanBeAtomicallyReplacedAndRead()
    {
        var agent = Create();
        await Call(agent, "agent_memo", new { action = "set", content = "Prefer explicit section mappings." });
        Assert.Equal("Prefer explicit section mappings.", (await Call(agent, "agent_memo", new { }))["content"]!.GetValue<string>());
        await Assert.ThrowsAsync<ArgumentException>(() => Call(agent, "agent_memo", new { action = "set", content = new string('x', 32769) }));
    }

    [Fact]
    public async Task AuditToleratesMalformedRowsAndRedactsNestedSecrets()
    {
        var agent = Create();
        var path = Path.Combine(_root, "ai-data", "ai-audit.jsonl");
        File.WriteAllText(path, "not json\n[]\n" + JsonSerializer.Serialize(new AuditRow
        {
            RunId = "run-a", Target = "/servers/site-a/irc/channels/0", Before = new { name = "#a", key = "synthetic-private-key" }
        }) + "\n" + JsonSerializer.Serialize(new AuditRow
        {
            RunId = "run-b", Target = "/controlApi/token", Before = "historical-control-token", After = "other-control-token"
        }) + "\n");
        var result = await Call(agent, "agent_audit", new { limit = 10 });
        Assert.Equal(2, result["parseSkips"]!.GetValue<int>());
        Assert.Equal(2, result["rows"]!.AsArray().Count);
        Assert.DoesNotContain("synthetic-private-key", result.ToJsonString());
        Assert.DoesNotContain("historical-control-token", result.ToJsonString());
        Assert.DoesNotContain("other-control-token", result.ToJsonString());
    }

    [Fact]
    public async Task RunCannotBypassExistingConsentRequirement()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Call(Create(), "agent_run", new { }));
    }
}
