using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using GlDrive.Config;
using GlDrive.Services;
using GlDrive.Services.Automation;
using Xunit;

namespace GlDrive.Tests;

public sealed class AutomationSecurityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "automation-security-" + Guid.NewGuid().ToString("N"));
    private const string ScopedToken = "synthetic-scoped-client-token-abcdefghijklmnopqrstuvwxyz";
    public AutomationSecurityTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);
    private static JsonElement Json(string text = "{}") => JsonDocument.Parse(text).RootElement.Clone();
    private static AutomationToolDefinition Tool(string name, bool read = false, bool destructive = false)
        => AutomationToolDefinition.Create(name, "Synthetic operation", new { type = "object" }, read, destructive);
    private static ControlApiConfig Config(params string[] scopes) => new()
    {
        Token = new string('a', 64), Security = new()
        {
            Clients = [new() { Id = "client-one", Name = "Fixture client", TokenHash = AutomationAccessController.HashToken(ScopedToken),
                Scopes = scopes.Length == 0 ? ["Observe"] : [.. scopes] }]
        }
    };
    private static AutomationPrincipal Principal(ControlApiConfig config) => AutomationAccessController.Authenticate(config, ScopedToken)!;

    [Fact]
    public void Legacy_token_retains_every_capability_and_scoped_tokens_are_additive()
    {
        var config = Config();
        using var broker = new AutomationApprovalBroker();
        var access = new AutomationAccessController(config, broker);
        var legacy = AutomationAccessController.Authenticate(config, config.Token)!;
        Assert.True(legacy.IsLegacy);
        Assert.Equal((AutomationCapability)63, legacy.Capabilities);
        Assert.NotNull(access.Authorize(legacy, Tool("ftp.delete", destructive: true), Json()).ApprovalReason);
        Assert.Throws<AutomationDeniedException>(() => access.Authorize(Principal(config), Tool("ftp.delete"), Json()));
        Assert.NotNull(AutomationAccessController.Authenticate(config, config.Token));
        Assert.Null(AutomationAccessController.Authenticate(config, "wrong-token"));
        Assert.Null(AutomationAccessController.Authenticate(config, ""));
        Assert.DoesNotContain(ScopedToken, JsonSerializer.Serialize(config));
    }

    [Fact]
    public void Revocation_rotation_and_expiry_take_effect_without_restart()
    {
        var config = Config(); using var broker = new AutomationApprovalBroker(); var access = new AutomationAccessController(config, broker);
        var principal = Principal(config);
        config.Security.Clients[0].ExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(-1);
        Assert.Null(AutomationAccessController.Authenticate(config, ScopedToken));
        Assert.Throws<AutomationDeniedException>(() => access.Authorize(principal, Tool("test.read", true), Json()));
        config.Security.Clients[0].ExpiresUtc = null;
        config.Security.Clients[0].TokenHash = AutomationAccessController.HashToken("replacement-fixture-token");
        Assert.Null(AutomationAccessController.Authenticate(config, ScopedToken));
        Assert.Throws<AutomationDeniedException>(() => access.Authorize(principal, Tool("test.read", true), Json()));
        var legacy = AutomationAccessController.Authenticate(config, config.Token)!;
        config.Token = new string('b', 64);
        Assert.Throws<AutomationDeniedException>(() => access.Authorize(legacy, Tool("test.read", true), Json()));
    }

    [Fact]
    public void Families_match_exactly_and_secret_reads_require_the_secret_scope()
    {
        var config = Config(); config.Security.Clients[0].ToolFamilies = ["irc"];
        using var broker = new AutomationApprovalBroker(); var access = new AutomationAccessController(config, broker);
        Assert.Null(access.Authorize(Principal(config), Tool("irc.status", true), Json()).ApprovalReason);
        Assert.Throws<AutomationDeniedException>(() => access.Authorize(Principal(config), Tool("ircish.status", true), Json()));
        Assert.Throws<AutomationDeniedException>(() => access.Authorize(Principal(config), Tool("irc.key_get", true), Json()));
        config.Security.Clients[0].Scopes.Add("Secrets");
        Assert.NotNull(access.Authorize(Principal(config), Tool("irc.key_get", true), Json()).ApprovalReason);
    }

    [Theory]
    [InlineData("ui.invoke", "{\"command\":\"ClearFinishedCommand\"}")]
    [InlineData("ui.invoke", "{\"command\":\"DismissJobCommand\"}")]
    [InlineData("ui.invoke", "{\"command\":\"SearchCommand\"}")]
    [InlineData("servers.remount", "{}")]
    [InlineData("spread.stop", "{}")]
    [InlineData("downloads.cancel", "{}")]
    [InlineData("installer.logs_clear", "{}")]
    public void Broad_catalog_destructive_hint_does_not_gate_ordinary_operations(string name, string args)
    {
        var config = Config("Observe", "Operate"); using var broker = new AutomationApprovalBroker(); var access = new AutomationAccessController(config, broker);
        var risk = access.Authorize(Principal(config), Tool(name, destructive: true), Json(args));
        Assert.False(risk.Destructive); Assert.Null(risk.ApprovalReason);
    }

    [Theory]
    [InlineData("credentials_manage", "{\"action\":\"get\"}")]
    [InlineData("config_get", "{}")]
    [InlineData("installer.profile_get", "{}")]
    [InlineData("ui.state", "{}")]
    public void Presence_and_ordinary_reads_do_not_prompt(string name, string args)
    {
        var config = Config(); using var broker = new AutomationApprovalBroker(); var access = new AutomationAccessController(config, broker);
        Assert.Null(access.Authorize(Principal(config), Tool(name, name == "config_get", true), Json(args)).ApprovalReason);
    }

    [Theory]
    [InlineData("irc.key_get", "{}")]
    [InlineData("config_get", "{\"includeSecrets\":true}")]
    [InlineData("config_snapshot_get", "{\"includeSecrets\":true}")]
    [InlineData("credentials_manage", "{\"action\":\"get\",\"includeValue\":true}")]
    [InlineData("installer.profile_get", "{\"includeSecrets\":true}")]
    [InlineData("ftp.command", "{\"command\":\"SITE WIPE /fixture\"}")]
    [InlineData("ftp.delete", "{}")]
    [InlineData("cleanup.delete", "{}")]
    public void Dangerous_calls_only_gate_legacy_after_explicit_opt_in(string name, string args)
    {
        var config = Config(); using var broker = new AutomationApprovalBroker(); var access = new AutomationAccessController(config, broker);
        var risk = access.Authorize(AutomationAccessController.Authenticate(config, config.Token)!, Tool(name, true), Json(args));
        Assert.NotNull(risk.ApprovalReason);
        Assert.False(access.RequiresApproval(AutomationPrincipal.Legacy, risk));
        config.Security.RequireLegacyApproval = true;
        Assert.True(access.RequiresApproval(AutomationPrincipal.Legacy, risk));
    }

    [Theory]
    [InlineData("config_patch", "{\"operations\":[{\"op\":\"replace\",\"path\":\"/controlApi/token\",\"value\":\"replacement\"}],\"allowSecrets\":true}")]
    [InlineData("config_patch", "{\"operations\":[{\"op\":\"replace\",\"path\":\"/controlApi\",\"value\":{}}]}")]
    [InlineData("config_patch", "{\"operations\":[{\"op\":\"add\",\"path\":\"/ControlApi\",\"value\":{}}]}")]
    [InlineData("config_patch", "{\"operations\":[{\"op\":\"add\",\"path\":\"/controlApi/Security\",\"value\":{}}]}")]
    [InlineData("config_lease", "{\"action\":\"start\",\"pointer\":\"/controlApi/port\"}")]
    [InlineData("config_lease", "{\"action\":\"start\",\"pointer\":\"/controlApi/enabled\"}")]
    public void Scoped_clients_cannot_change_owner_authority(string name, string args)
    {
        var config = Config(AutomationSecurityConfig.ScopeNames); config.Security.Clients[0].AllowDestructive = true;
        using var broker = new AutomationApprovalBroker(); var access = new AutomationAccessController(config, broker);
        Assert.Throws<AutomationDeniedException>(() => access.Authorize(Principal(config), Tool(name), Json(args)));
    }

    [Theory]
    [InlineData("[{\"op\":\"add\",\"path\":\"/ControlApi\",\"value\":{\"token\":\"replacement\"}}]")]
    [InlineData("[{\"op\":\"add\",\"path\":\"/controlApi/Security\",\"value\":{\"requireDangerousApproval\":false}}]")]
    [InlineData("[{\"op\":\"add\",\"path\":\"/controlApi/security/RequireConfigurationApproval\",\"value\":false}]")]
    [InlineData("[{\"op\":\"replace\",\"path\":\"/controlApi\",\"value\":{\"token\":\"first\",\"token\":\"second\",\"security\":{}}}]")]
    public void Case_aliases_and_duplicate_authority_properties_never_change_saved_or_live_authority(string patch)
    {
        var config = new AppConfig { ControlApi = Config() }; var saved = false;
        var before = JsonSerializer.Serialize(config.ControlApi);
        var configuration = new ConfigurationAutomation(config, _ => saved = true);
        Assert.ThrowsAny<Exception>(() => configuration.Apply(JsonNode.Parse(patch)!.AsArray(), allowSecrets: true));
        Assert.False(saved); Assert.Equal(before, JsonSerializer.Serialize(config.ControlApi));
    }

    [Theory]
    [InlineData("{\"clients\":\"bad\"}")]
    [InlineData("{\"approvalTimeoutSeconds\":\"bad\"}")]
    [InlineData("{\"requireDangerousApproval\":{}}")]
    [InlineData("{\"requireConfigurationApproval\":\"invalid\"}")]
    [InlineData("{\"clients\":[{\"id\":\"fixture\",\"expiresUtc\":\"not-a-date\"}]}")]
    [InlineData("null")]
    public void Malformed_optional_security_never_discards_ordinary_configuration(string security)
    {
        var text = "{\"servers\":[{\"id\":\"fixture\"}],\"controlApi\":{\"token\":\"current-token\",\"security\":" + security + "}}";
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var config = JsonSerializer.Deserialize<AppConfig>(text, options)!;
        Assert.Single(config.Servers); Assert.Equal("current-token", config.ControlApi.Token);
        Assert.Empty(config.ControlApi.Security.Clients); Assert.True(config.ControlApi.Security.RequireDangerousApproval);
        Assert.True(config.ControlApi.Security.RequireConfigurationApproval);
        Assert.NotNull(config.ControlApi.Security.UnparsedJson);
        var roundtrip = JsonSerializer.SerializeToNode(config, options)!;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(security), roundtrip["controlApi"]!["security"]));
    }

    [Fact]
    public void Security_settings_cannot_be_changed_by_configuration_automation()
    {
        var config = new AppConfig { ControlApi = Config() }; var saved = false;
        var automation = new ConfigurationAutomation(config, _ => saved = true);
        var patch = JsonNode.Parse("[{\"op\":\"replace\",\"path\":\"/controlApi/security/requireDangerousApproval\",\"value\":false}]")!.AsArray();
        Assert.Throws<ArgumentException>(() => automation.Apply(patch, allowSecrets: true));
        Assert.False(saved); Assert.True(config.ControlApi.Security.RequireDangerousApproval);
        var view = JsonSerializer.Serialize(automation.Get(includeSecrets: true));
        Assert.DoesNotContain(config.ControlApi.Security.Clients[0].TokenHash, view);
    }

    [Fact]
    public async Task Ordinary_configuration_edit_waits_for_review_without_needing_secret_access()
    {
        var config = new AppConfig { ControlApi = Config("Operate", "Configure") };
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var configuration = new ConfigurationAutomation(config, _ => saved.TrySetResult());
        using var broker = new AutomationApprovalBroker();
        var requested = ObserveApproval(broker);
        using var registry = new AutomationRegistry(configuration)
        { Access = new(config.ControlApi, broker), InlineWait = TimeSpan.FromMilliseconds(10) };
        var request = Json("{\"operations\":[{\"op\":\"replace\",\"path\":\"/downloads/maxRetries\",\"value\":7}]}");
        var result = JsonSerializer.SerializeToElement(await registry.CallAsync("config_patch", request, default, Principal(config.ControlApi)));
        var approval = await requested.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var jobArgs = JsonSerializer.SerializeToElement(new { jobId = result.GetProperty("jobId").GetString() });
        var pending = JsonSerializer.SerializeToElement(await registry.CallAsync("app.jobs_get", jobArgs, default, Principal(config.ControlApi)));
        Assert.Equal("awaiting-approval", pending.GetProperty("status").GetString());
        Assert.False(saved.Task.IsCompleted); Assert.Equal(3, config.Downloads.MaxRetries);
        Assert.Equal(approval.Id, Assert.Single(broker.Snapshot()).Id);
        Assert.Contains("/downloads/maxRetries", approval.Reason);
        Assert.True(broker.Decide(approval.Id, true));
        await saved.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await WaitFor(() => config.Downloads.MaxRetries == 7);
        Assert.Equal(7, config.Downloads.MaxRetries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Approved_configuration_patch_cannot_follow_a_concurrent_edit_or_server_reorder(bool reorder)
    {
        var config = new AppConfig { Servers = [new() { Id = "fixture-a", Name = "First fixture" }, new() { Id = "fixture-b", Name = "Second fixture" }] };
        var saved = false;
        var configuration = new ConfigurationAutomation(config, _ => saved = true);
        var bound = await configuration.BindApprovalAsync("config_patch",
            Json("{\"operations\":[{\"op\":\"replace\",\"path\":\"/servers/0/name\",\"value\":\"Reviewed name\"}]}"), default);
        Assert.NotNull(bound);
        if (reorder) config.Servers.Reverse(); else config.Downloads.MaxRetries = 8;
        await Assert.ThrowsAsync<InvalidOperationException>(() => bound.ExecuteAsync(default));
        Assert.False(saved);
        Assert.Equal("First fixture", config.Servers.Single(s => s.Id == "fixture-a").Name);
        Assert.Equal("Second fixture", config.Servers.Single(s => s.Id == "fixture-b").Name);
    }

    [Fact]
    public async Task Approval_binding_keeps_a_callers_explicit_revision_precondition()
    {
        var saved = false;
        var configuration = new ConfigurationAutomation(new AppConfig(), _ => saved = true);
        var bound = await configuration.BindApprovalAsync("config_patch",
            Json("{\"expectedRevision\":\"stale-revision\",\"operations\":[{\"op\":\"replace\",\"path\":\"/downloads/maxRetries\",\"value\":7}]}"), default);
        Assert.NotNull(bound);
        await Assert.ThrowsAsync<InvalidOperationException>(() => bound.ExecuteAsync(default));
        Assert.False(saved);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Configuration_and_dangerous_approval_switches_are_independent(bool configuration, bool dangerous)
    {
        var config = Config(); config.Security.RequireConfigurationApproval = configuration; config.Security.RequireDangerousApproval = dangerous;
        using var broker = new AutomationApprovalBroker(); var access = new AutomationAccessController(config, broker);
        var ordinary = access.Authorize(AutomationPrincipal.Legacy, Tool("config_patch"), Json("{\"operations\":[{\"op\":\"replace\",\"path\":\"/downloads/maxRetries\",\"value\":7}]}"));
        var secret = access.Authorize(AutomationPrincipal.Legacy, Tool("irc.key_get", true), Json());
        var both = access.Authorize(AutomationPrincipal.Legacy, Tool("config_patch"), Json("{\"allowSecrets\":true,\"operations\":[{\"op\":\"replace\",\"path\":\"/controlApi/token\",\"value\":\"fixture\"}]}"));
        Assert.Equal(configuration, access.RequiresApproval(Principal(config), ordinary));
        Assert.Equal(dangerous, access.RequiresApproval(Principal(config), secret));
        Assert.Equal(configuration || dangerous, access.RequiresApproval(Principal(config), both));
        Assert.False(ordinary.Required.HasFlag(AutomationCapability.Secrets));
    }

    [Theory]
    [InlineData("config_preview", "{}")]
    [InlineData("config_patch", "{\"operations\":[{\"op\":\"test\",\"path\":\"/downloads/maxRetries\",\"value\":3}]}")]
    [InlineData("config_lease", "{\"action\":\"list\"}")]
    [InlineData("config_lease", "{\"action\":\"keep\"}")]
    [InlineData("config_experiment", "{\"action\":\"start\"}")]
    [InlineData("config_experiment", "{\"action\":\"keep\"}")]
    [InlineData("installer.profile_set", "{\"profile\":{\"site_name\":\"Fixture\"}}")]
    public void Inspection_bookkeeping_and_ordinary_form_edits_do_not_request_configuration_approval(string tool, string args)
    {
        var config = Config(); using var broker = new AutomationApprovalBroker(); var access = new AutomationAccessController(config, broker);
        Assert.False(access.RequiresApproval(AutomationPrincipal.Legacy, access.Authorize(AutomationPrincipal.Legacy, Tool(tool), Json(args))));
    }

    [Fact]
    public void Configuration_approval_setting_has_a_safe_schema_default_and_roundtrips_false()
    {
        var configuration = new ConfigurationAutomation(new AppConfig(), _ => { });
        var schema = JsonSerializer.SerializeToNode(configuration.Schema())!;
        Assert.True(schema["defaults"]!["controlApi"]!["security"]!["requireConfigurationApproval"]!.GetValue<bool>());
        Assert.Equal("boolean", schema["schema"]!["properties"]!["controlApi"]!["properties"]!["security"]!["properties"]!["requireConfigurationApproval"]!["type"]!.GetValue<string>());
        var ownerApproval = schema["schema"]!["properties"]!["controlApi"]!["properties"]!["security"]!["properties"]!["requireLegacyApproval"]!;
        Assert.Equal("boolean", ownerApproval["type"]!.GetValue<string>());
        Assert.StartsWith("Default false.", ownerApproval["description"]!.GetValue<string>());
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var policy = JsonSerializer.Deserialize<AutomationSecurityConfig>("{\"requireConfigurationApproval\":false}", options)!;
        Assert.False(policy.RequireConfigurationApproval);
        Assert.False(JsonSerializer.Deserialize<AutomationSecurityConfig>(JsonSerializer.Serialize(policy, options), options)!.RequireConfigurationApproval);
    }

    [Fact]
    public async Task Long_lived_control_listener_does_not_inherit_a_settings_writer()
    {
        var ambient = new AsyncLocal<string?> { Value = "request" };
        using var attribution = ConfigWriteScope.Begin("mcp:fixture:config_patch");
        await ControlApi.RunAcceptLoop(() =>
        {
            Assert.Null(ambient.Value);
            Assert.Equal("application", ConfigWriteScope.Current.Actor);
            return Task.CompletedTask;
        });
        Assert.Equal("mcp:fixture:config_patch", ConfigWriteScope.Current.Actor);
    }

    [Fact]
    public void Explicit_local_paths_need_selected_roots_and_cannot_escape_by_prefix_or_parent()
    {
        var allowed = Path.Combine(_root, "allowed"); Directory.CreateDirectory(allowed);
        var config = Config("Observe", "Operate", "RemoteAdmin", "LocalFiles");
        using var broker = new AutomationApprovalBroker(); var access = new AutomationAccessController(config, broker);
        var args = JsonSerializer.SerializeToElement(new { localPath = Path.Combine(allowed, "file.bin") });
        Assert.Throws<AutomationDeniedException>(() => access.Authorize(Principal(config), Tool("ftp.upload"), args));
        config.Security.Clients[0].AllowedLocalRoots = [allowed];
        Assert.NotNull(access.Authorize(Principal(config), Tool("ftp.upload"), args));
        Assert.False(AutomationLocalPathPolicy.WithinRoot(allowed, Path.Combine(_root, "allowed-sibling", "file.bin")));
        Assert.False(AutomationLocalPathPolicy.WithinRoot(allowed, Path.Combine(allowed, "..", "outside.bin")));
        Assert.False(AutomationLocalPathPolicy.WithinRoot(allowed, "relative.bin"));
        Assert.True(AutomationLocalPathPolicy.WithinRoot(allowed, Path.Combine(allowed, "new", "file.bin")));
    }

    [Fact]
    public async Task Broker_times_out_denies_locked_sessions_and_does_not_block_the_caller()
    {
        using var broker = new AutomationApprovalBroker();
        // Nonblocking admission and deadline denial are independent assertions. A short
        // deadline may elapse while the test thread is descheduled by the full suite.
        var task = broker.RequestAsync(AutomationPrincipal.Legacy, "fixture", "Delete selected files", TimeSpan.FromMinutes(1), default);
        Assert.False(task.IsCompleted); Assert.Single(broker.Snapshot());
        broker.DenyAll();
        Assert.False(await task); Assert.Empty(broker.Snapshot());
        var expired = broker.RequestAsync(AutomationPrincipal.Legacy, "fixture", "Reveal", TimeSpan.FromMilliseconds(25), default);
        Assert.False(await expired.WaitAsync(TimeSpan.FromSeconds(10))); Assert.Empty(broker.Snapshot());
        broker.SetSessionLocked(true);
        Assert.False(await broker.RequestAsync(AutomationPrincipal.Legacy, "fixture", "Reveal", TimeSpan.FromMinutes(1), default));
        broker.SetSessionLocked(false);
        var pending = broker.RequestAsync(AutomationPrincipal.Legacy, "fixture", "Reveal", TimeSpan.FromMinutes(1), default);
        broker.SetSessionLocked(true);
        Assert.False(await pending);
    }

    [Fact]
    public async Task Broker_capacity_denies_oldest_and_retains_new_request()
    {
        using var broker = new AutomationApprovalBroker();
        var tasks = Enumerable.Range(0, 65).Select(_ => broker.RequestAsync(AutomationPrincipal.Legacy, "fixture", "Reveal", TimeSpan.FromMinutes(1), default)).ToArray();
        Assert.False(await tasks[0]); Assert.Equal(64, broker.Snapshot().Count);
        broker.DenyAll(); Assert.All(await Task.WhenAll(tasks), approved => Assert.False(approved));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Awaiting_approval_is_a_job_and_revocation_before_approval_never_executes(bool legacy)
    {
        var config = Config("Observe", "Secrets"); config.Security.RequireLegacyApproval = true;
        using var broker = new AutomationApprovalBroker(); var access = new AutomationAccessController(config, broker);
        var requested = ObserveApproval(broker);
        var principal = legacy ? AutomationAccessController.Authenticate(config, config.Token)! : Principal(config);
        var module = new Module(Tool("irc.key_get", true));
        using var registry = new AutomationRegistry(module) { Access = access, InlineWait = TimeSpan.FromMilliseconds(10) };
        var denied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        registry.Audit = audit => { if (audit.Outcome == "denied") denied.TrySetResult(); };
        var result = JsonSerializer.SerializeToElement(await registry.CallAsync("irc.key_get", Json(), default, principal));
        var approval = await requested.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var jobArgs = JsonSerializer.SerializeToElement(new { jobId = result.GetProperty("jobId").GetString() });
        var pending = JsonSerializer.SerializeToElement(await registry.CallAsync("app.jobs_get", jobArgs, default, principal));
        Assert.Equal("awaiting-approval", pending.GetProperty("status").GetString());
        Assert.Equal(0, module.Calls);
        Assert.Equal(approval.Id, Assert.Single(broker.Snapshot()).Id);
        if (legacy) config.Token = new string('b', 64); else config.Security.Clients.Clear();
        Assert.True(broker.Decide(approval.Id, true));
        await denied.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, module.Calls);
    }

    [Fact]
    public async Task Job_results_are_private_and_scope_reduction_prevents_old_secret_reads()
    {
        var config = Config("Observe", "Secrets"); config.Security.Clients[0].ToolFamilies = ["irc"];
        using var broker = new AutomationApprovalBroker(); var access = new AutomationAccessController(config, broker);
        var requested = ObserveApproval(broker);
        var module = new Module(Tool("irc.key_get", true));
        using var registry = new AutomationRegistry(module) { Access = access, InlineWait = TimeSpan.FromMilliseconds(10) };
        var owner = Principal(config);
        var result = JsonSerializer.SerializeToElement(await registry.CallAsync("irc.key_get", Json(), default, owner));
        var jobArgs = JsonSerializer.SerializeToElement(new { jobId = result.GetProperty("jobId").GetString() });
        var approval = await requested.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var pending = JsonSerializer.SerializeToElement(await registry.CallAsync("app.jobs_get", jobArgs, default, owner));
        Assert.Equal("awaiting-approval", pending.GetProperty("status").GetString());
        Assert.Equal(approval.Id, Assert.Single(broker.Snapshot()).Id);
        Assert.True(broker.Decide(approval.Id, true));
        await WaitFor(() => module.Calls == 1);
        var legacy = AutomationAccessController.Authenticate(config, config.Token)!;
        await Assert.ThrowsAsync<KeyNotFoundException>(() => registry.CallAsync("app.jobs_get", jobArgs, default, legacy));
        var outsider = JsonSerializer.SerializeToElement(await registry.CallAsync("app.jobs", Json(), default, legacy));
        Assert.Empty(outsider.GetProperty("jobs").EnumerateArray());
        config.Security.Clients[0].Scopes = ["RemoteAdmin"];
        var ownList = JsonSerializer.SerializeToElement(await registry.CallAsync("app.jobs", Json(), default, owner));
        Assert.Single(ownList.GetProperty("jobs").EnumerateArray());
        await Assert.ThrowsAsync<AutomationDeniedException>(() => registry.CallAsync("app.jobs_get", jobArgs, default, owner));
        var cancellation = JsonSerializer.SerializeToElement(await registry.CallAsync("app.jobs_cancel", jobArgs, default, owner));
        Assert.Equal(JsonValueKind.Null, cancellation.GetProperty("result").ValueKind);
    }

    [Fact]
    public async Task Ledger_failure_never_refuses_calls_and_ordinary_calls_never_ask_approval()
    {
        var config = Config("Observe", "Operate"); using var broker = new AutomationApprovalBroker();
        var requested = 0; broker.Requested += _ => requested++;
        var module = new Module(Tool("ui.invoke", destructive: true));
        using var registry = new AutomationRegistry(module) { Access = new(config, broker), Audit = _ => throw new IOException("fixture") };
        await registry.CallAsync("ui.invoke", Json("{\"command\":\"ClearFinishedCommand\"}"), default, Principal(config));
        Assert.Equal(1, module.Calls); Assert.Equal(0, requested);
    }

    [Fact]
    public async Task Worker_does_not_inherit_ambient_context_and_uses_explicit_client_attribution()
    {
        var context = new AsyncLocal<string?> { Value = "outer" };
        string? inherited = "unset", actor = null;
        var config = Config("Operate"); using var broker = new AutomationApprovalBroker();
        var module = new Module(Tool("test.write")) { Handler = () => { inherited = context.Value; actor = ConfigWriteScope.Current.Actor; } };
        using var registry = new AutomationRegistry(module) { Access = new(config, broker) };
        await registry.CallAsync("test.write", Json(), default, Principal(config));
        Assert.Null(inherited); Assert.Equal("mcp:test.write", actor);
    }

    [Fact]
    public void Rest_routes_use_the_same_scope_policy()
    {
        var config = Config(); using var broker = new AutomationApprovalBroker(); var access = new AutomationAccessController(config, broker);
        Assert.NotNull(access.Authorize(Principal(config), ControlApi.RestTool("GET", "/races")!, Json()));
        Assert.Throws<AutomationDeniedException>(() => access.Authorize(Principal(config), ControlApi.RestTool("POST", "/races")!, Json()));
        config.Security.Clients[0].Scopes.Add("Operate");
        Assert.False(access.Authorize(Principal(config), ControlApi.RestTool("POST", "/races/fixture/stop")!, Json()).Destructive);
    }

    [Fact]
    public void Approval_description_names_targets_without_command_secrets()
    {
        var description = AutomationApprovalDescription.Describe("ftp.command",
            Json("{\"serverId\":\"fixture\",\"command\":\"SITE CHPASS account private-password\",\"value\":\"another-secret\"}"), "Remote administration", id => id == "fixture" ? "Fixture site" : null);
        Assert.Contains("Fixture site", description); Assert.Contains("SITE CHPASS", description);
        Assert.DoesNotContain("private-password", description); Assert.DoesNotContain("another-secret", description); Assert.DoesNotContain("account", description);
    }

    [Theory]
    [InlineData("PWD", true)]
    [InlineData("site who", true)]
    [InlineData("SITE HELP", true)]
    [InlineData("NOOP", true)]
    [InlineData("SITE WHO extra", false)]
    [InlineData("PWD\r\nDELE /fixture", false)]
    [InlineData("SITE WIPE /fixture", false)]
    public void Only_exact_no_argument_read_commands_avoid_admin_approval(string command, bool read)
    {
        var risk = AutomationRiskPolicy.Classify(Tool("ftp.command", destructive: true), JsonSerializer.SerializeToElement(new { command }));
        Assert.Equal(read, risk.ApprovalReason == null);
        Assert.Equal(!read, risk.Destructive);
        Assert.Equal(read, risk.Required == AutomationCapability.Observe);
    }

    [Fact]
    public void Installer_profile_passwords_require_explicit_secret_scope_and_approval()
    {
        var config = Config("Observe", "Operate", "Configure");
        using var broker = new AutomationApprovalBroker(); var access = new AutomationAccessController(config, broker);
        var args = Json("{\"profile\":{\"ssh_password\":\"fixture-secret\"}}");
        Assert.Throws<AutomationDeniedException>(() => access.Authorize(Principal(config), Tool("installer.profile_set"), args));
        config.Security.Clients[0].Scopes.Add("Secrets"); config.Security.Clients[0].AllowDestructive = true;
        var risk = access.Authorize(Principal(config), Tool("installer.profile_set"), args);
        Assert.NotNull(risk.ApprovalReason);
        Assert.DoesNotContain("fixture-secret", access.DescribeApproval("installer.profile_set", args, risk.ApprovalReason!));
    }

    [Theory]
    [InlineData("ui.invoke", "{\"command\":\"RemoveWishlistCommand\"}")]
    [InlineData("installer.start", "{}")]
    [InlineData("extractor.start", "{}")]
    [InlineData("extractor.settings", "{}")]
    [InlineData("config_patch", "{\"operations\":[{\"op\":\"replace\",\"path\":\"/downloads/maxRetries\",\"value\":7}]}")]
    public async Task Mutable_dangerous_targets_without_binding_never_request_or_execute_approval(string tool, string args)
    {
        var config = Config(); using var broker = new AutomationApprovalBroker();
        var requested = 0; broker.Requested += _ => requested++;
        var module = new Module(Tool(tool));
        using var registry = new AutomationRegistry(module) { Access = new(config, broker) };
        config.Security.RequireLegacyApproval = true;
        var result = JsonSerializer.SerializeToElement(await registry.CallAsync(tool, Json(args), default));
        Assert.Equal("denied", result.GetProperty("status").GetString());
        Assert.Equal("approval_denied", result.GetProperty("code").GetString());
        Assert.Equal(0, requested); Assert.Equal(0, module.Calls);
    }

    private static TaskCompletionSource<AutomationApprovalRequest> ObserveApproval(AutomationApprovalBroker broker)
    {
        var requested = new TaskCompletionSource<AutomationApprovalRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        broker.Requested += request => requested.TrySetResult(request);
        return requested;
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }

    [Theory]
    [InlineData("irc.key_exchange")]
    [InlineData("plex.logout")]
    [InlineData("plex.login_start")]
    public void Credential_lifecycle_tools_require_the_secret_scope(string name)
    {
        var config = Config("Operate", "Configure", "LocalFiles"); config.Security.Clients[0].AllowDestructive = true;
        using var broker = new AutomationApprovalBroker(); var access = new AutomationAccessController(config, broker);
        Assert.Throws<AutomationDeniedException>(() => access.Authorize(Principal(config), Tool(name), Json()));
        config.Security.Clients[0].Scopes.Add("Secrets");
        var risk = access.Authorize(Principal(config), Tool(name), Json());
        Assert.Equal(name == "plex.login_start", risk.ApprovalReason == null);
    }

    private sealed class Module(AutomationToolDefinition tool) : IAutomationModule
    {
        public int Calls;
        public Action? Handler;
        public IReadOnlyList<AutomationToolDefinition> Tools { get; } = [tool];
        public Task<object?> ExecuteAsync(string operation, JsonElement arguments, CancellationToken ct)
        { Handler?.Invoke(); Interlocked.Increment(ref Calls); return Task.FromResult<object?>(new { result = "fixture-result" }); }
    }
}
