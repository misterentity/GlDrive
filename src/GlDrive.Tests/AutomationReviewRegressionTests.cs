using System.Text.Json;
using GlDrive.AiAgent;
using GlDrive.Config;
using GlDrive.Services.Automation;
using Xunit;

namespace GlDrive.Tests;

public sealed class AutomationReviewRegressionTests
{
    private static JsonElement Json(string text = "{}") => JsonDocument.Parse(text).RootElement.Clone();
    private sealed class Module(params string[] names) : IAutomationModule, IAutomationTargetDescription
    {
        public int Calls;
        public IReadOnlyList<AutomationToolDefinition> Tools { get; } = names.Select(name =>
            AutomationToolDefinition.Create(name, "Fixture", new { type = "object" }, name.EndsWith("read"))).ToArray();
        public Task<object?> ExecuteAsync(string operation, JsonElement arguments, CancellationToken ct)
        { Interlocked.Increment(ref Calls); return Task.FromResult<object?>(new { saved = true }); }
        public string? ApprovalTargetName(string tool, JsonElement arguments) => tool == "wishlist.remove" ? "Fixture movie" : null;
    }

    [Theory]
    [InlineData("irc.key_get")]
    [InlineData("ftp.command")]
    [InlineData("downloads.remove")]
    [InlineData("spread.queue_remove")]
    [InlineData("credentials_manage")]
    public async Task Owner_tools_keep_their_previous_execution_behavior_on_a_locked_desktop(string name)
    {
        var config = new ControlApiConfig();
        using var broker = new AutomationApprovalBroker(); broker.SetSessionLocked(true);
        var prompts = 0; broker.Requested += _ => prompts++;
        var module = new Module(name);
        using var registry = new AutomationRegistry(module) { Access = new(config, broker) };
        var result = JsonSerializer.SerializeToElement(await registry.CallAsync(name,
            Json("{\"action\":\"get\",\"includeValue\":true,\"command\":\"SITE STAT\"}"), default));
        Assert.True(result.GetProperty("saved").GetBoolean()); Assert.Equal(1, module.Calls); Assert.Equal(0, prompts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Owner_configuration_edits_remain_immediate_and_are_human_corrections(bool locked)
    {
        var config = new AppConfig(); config.Downloads.MaxRetries = 7;
        ConfigOverrideEvent? recorded = null;
        using var broker = new AutomationApprovalBroker(); broker.SetSessionLocked(locked);
        var prompts = 0; broker.Requested += _ => prompts++;
        var configuration = new ConfigurationAutomation(config, candidate =>
        {
            var context = ConfigWriteScope.Current;
            recorded = new() { Actor = context.Actor, AiAuditRef = context.AuditRef, Ts = DateTime.UtcNow.ToString("O"),
                JsonPointer = "/downloads/maxRetries", BeforeValue = "7", AfterValue = candidate.Downloads.MaxRetries.ToString() };
        });
        using var registry = new AutomationRegistry(configuration) { Access = new(config.ControlApi, broker) };
        await registry.CallAsync("config_patch", Json("""{"operations":[{"op":"replace","path":"/downloads/maxRetries","value":3}]}"""), default);
        Assert.Equal(3, config.Downloads.MaxRetries); Assert.Equal(0, prompts);
        Assert.NotNull(recorded); Assert.Equal("mcp:config_patch", recorded.Actor); Assert.Null(recorded.AiAuditRef);
        Assert.Empty(new OverridesDigester().Build([recorded]).RevertedAiPaths);
        var prior = new AuditRow { Id = "fixture-audit", Applied = true, Target = "/downloads/maxRetries", After = 7,
            Ts = DateTime.UtcNow.AddHours(-1).ToString("O") };
        Assert.Equal("fixture-audit", Assert.Single(CorrectionDetector.Detect(config, [prior], [recorded])).AuditId);
        Assert.Empty(CorrectionDetector.Detect(config, [], [recorded]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fast_denial_and_locked_desktop_return_denied_jobs_without_token_failure(bool locked)
    {
        var config = new ControlApiConfig(); config.Security.RequireLegacyApproval = true;
        using var broker = new AutomationApprovalBroker(); broker.SetSessionLocked(locked);
        broker.Requested += request => broker.Decide(request.Id, false);
        var module = new Module("irc.key_get");
        using var registry = new AutomationRegistry(module) { Access = new(config, broker) };
        var result = JsonSerializer.SerializeToElement(await registry.CallAsync("irc.key_get", Json(), default));
        Assert.Equal("denied", result.GetProperty("status").GetString());
        Assert.Equal("approval_denied", result.GetProperty("code").GetString());
        Assert.Equal(0, module.Calls);
        var poll = JsonSerializer.SerializeToElement(await registry.CallAsync("app.jobs_get",
            JsonSerializer.SerializeToElement(new { jobId = result.GetProperty("jobId").GetString() }), default));
        Assert.Equal("denied", poll.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Scope_refusal_has_a_distinct_code_and_does_not_execute()
    {
        var config = new ControlApiConfig();
        config.Security.Clients.Add(new() { Id = "fixture", Name = "Fixture", Scopes = ["Observe"],
            TokenHash = AutomationAccessController.HashToken("fixture-token") });
        using var broker = new AutomationApprovalBroker();
        var module = new Module("fixture.write");
        using var registry = new AutomationRegistry(module) { Access = new(config, broker) };
        var principal = AutomationAccessController.Authenticate(config, "fixture-token");
        var denied = await Assert.ThrowsAsync<AutomationDeniedException>(() => registry.CallAsync("fixture.write", Json(), default, principal));
        Assert.Equal("scope_denied", denied.Code); Assert.Equal(0, module.Calls);
        Assert.NotNull(AutomationAccessController.Authenticate(config, "fixture-token"));
    }

    [Fact]
    public async Task Full_approval_backlog_leaves_execution_slots_for_ordinary_reads()
    {
        var config = new ControlApiConfig(); config.Security.RequireLegacyApproval = true;
        using var broker = new AutomationApprovalBroker();
        var module = new Module("irc.key_get", "fixture.read");
        using var registry = new AutomationRegistry(module) { Access = new(config, broker), InlineWait = TimeSpan.FromMilliseconds(5) };
        for (var i = 0; i < 64; i++) await registry.CallAsync("irc.key_get", Json(), default);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (broker.Snapshot().Count != 64) await Task.Delay(5, deadline.Token);
        registry.InlineWait = TimeSpan.FromSeconds(5);
        var result = JsonSerializer.SerializeToElement(await registry.CallAsync("fixture.read", Json(), default));
        Assert.True(result.GetProperty("saved").GetBoolean()); Assert.Equal(1, module.Calls);
        Assert.Equal(64, broker.Snapshot().Count);
        broker.DenyAll();
    }

    [Theory]
    [InlineData("spread.queue_remove", "{\"section\":\"TV\",\"releaseName\":\"Fixture.S01E01\"}", "Fixture.S01E01")]
    [InlineData("spread.blacklist_remove", "{\"section\":\"TV\"}", "TV")]
    [InlineData("downloads.remove", "{\"id\":\"item-fixture\"}", "item-fixture")]
    [InlineData("downloads.clear_finished", "{\"status\":\"Failed\"}", "Failed")]
    [InlineData("downloads.clear_finished", "{}", "all finished downloads")]
    [InlineData("irc.raw", "{\"line\":\"PRIVMSG #fixture :private-body\"}", "IRC target: #fixture")]
    public void Approval_prompts_show_nonsecret_target_identity(string tool, string args, string expected)
    {
        var description = AutomationApprovalDescription.Describe(tool, Json(args), "Review", null);
        Assert.Contains(expected, description); Assert.DoesNotContain("private-body", description);
    }

    [Theory]
    [InlineData("PASS private-password")]
    [InlineData("AUTHENTICATE private-password")]
    [InlineData("private-password")]
    public void Irc_approval_never_displays_authentication_payloads(string line)
    {
        var description = AutomationApprovalDescription.Describe("irc.raw", JsonSerializer.SerializeToElement(new { line }), "Review", null);
        Assert.DoesNotContain("private-password", description);
    }

    [Fact]
    public async Task Registry_includes_the_resolved_item_title_in_approval()
    {
        var config = new ControlApiConfig(); config.Security.RequireLegacyApproval = true;
        using var broker = new AutomationApprovalBroker();
        string? description = null;
        broker.Requested += request => { description = request.Reason; broker.Decide(request.Id, false); };
        using var registry = new AutomationRegistry(new Module("wishlist.remove")) { Access = new(config, broker) };
        await registry.CallAsync("wishlist.remove", Json("{\"id\":\"fixture-id\"}"), default);
        Assert.Contains("Fixture movie", description); Assert.Contains("fixture-id", description);
    }
}
