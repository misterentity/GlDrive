using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using GlDrive.Config;
using GlDrive.Services.Automation;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// The control API is the app's only remote-ish attack surface, so its three guarantees
/// are pinned here rather than left to review: loopback-only binding, a required bearer
/// token compared in fixed time, and off-by-default.
/// </summary>
public class ControlApiSecurityTests
{
    private static string ReadControlApiSource()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12 && dir != null; i++)
        {
            var candidate = Path.Combine(dir, "src", "GlDrive", "Services", "ControlApi.cs");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException("Could not locate src/GlDrive/Services/ControlApi.cs");
    }

    [Fact]
    public void Disabled_by_default()
    {
        var cfg = new AppConfig();
        Assert.False(cfg.ControlApi.Enabled);
        Assert.Equal("", cfg.ControlApi.Token);
    }

    [Fact]
    public void Token_is_generated_only_once_and_only_when_enabled()
    {
        var cfg = new AppConfig();

        // disabled -> never mints a token
        Assert.False(ControlApi_EnsureToken(cfg));
        Assert.Equal("", cfg.ControlApi.Token);

        cfg.ControlApi.Enabled = true;
        Assert.True(ControlApi_EnsureToken(cfg));
        var first = cfg.ControlApi.Token;
        Assert.Equal(64, first.Length);                 // 32 random bytes, hex
        Assert.Matches("^[0-9a-f]+$", first);

        // idempotent — an existing token is never rotated out from under a caller
        Assert.False(ControlApi_EnsureToken(cfg));
        Assert.Equal(first, cfg.ControlApi.Token);
    }

    private static bool ControlApi_EnsureToken(AppConfig cfg)
        => GlDrive.Services.ControlApi.EnsureToken(cfg);

    [Fact]
    public void Listener_binds_loopback_only()
    {
        var src = ReadControlApiSource();
        Assert.Contains("http://127.0.0.1:", src);
        // A wildcard prefix would expose the control surface to the LAN.
        Assert.DoesNotContain("http://+:", src);
        Assert.DoesNotContain("http://*:", src);
        Assert.DoesNotContain("http://0.0.0.0", src);
    }

    [Fact]
    public void Every_request_is_token_checked_and_loopback_checked()
    {
        var src = ReadControlApiSource();

        Assert.Contains("IPAddress.IsLoopback", src);
        Assert.Contains("FixedTimeEquals", src);

        // The remote-address and token gates must both precede any routing decision,
        // otherwise an endpoint could be reached unauthenticated.
        var loopbackGate = src.IndexOf("IPAddress.IsLoopback", StringComparison.Ordinal);
        var tokenGate    = src.IndexOf("FixedTimeEquals(presented", StringComparison.Ordinal);
        var routing      = src.IndexOf("_routes.TryMatch(", StringComparison.Ordinal);
        Assert.True(loopbackGate > 0 && tokenGate > loopbackGate,
            "loopback check must come before the token check");
        Assert.True(routing > tokenGate, "routing must come after both gates");
    }

    [Fact]
    public void Refuses_to_listen_when_enabled_without_a_token()
    {
        var src = ReadControlApiSource();
        Assert.Contains("refusing to listen", src);
    }

    [Fact]
    public void Token_is_never_logged()
    {
        var src = ReadControlApiSource();
        foreach (var line in src.Split('\n'))
        {
            if (!line.Contains("Log.", StringComparison.Ordinal)) continue;
            Assert.DoesNotContain("Token", line);
            Assert.DoesNotContain("token}", line);
        }
    }

    private sealed class WriteModule : IAutomationModule
    {
        public int Calls;
        public IReadOnlyList<AutomationToolDefinition> Tools { get; } =
            [AutomationToolDefinition.Create("test.write", "Synthetic write", new { type = "object" })];
        public Task<object?> ExecuteAsync(string operation, JsonElement arguments, CancellationToken ct)
        { Interlocked.Increment(ref Calls); return Task.FromResult<object?>(new { saved = true }); }
    }

    /// <summary>
    /// End-to-end over a real listener: the token gate, the scoped-client policy and the
    /// router all hold on the automation routes, and the retired REST routes stay gone (a
    /// stale script gets 404, never a race start).
    /// </summary>
    [Fact]
    public async Task Automation_routes_enforce_token_and_scope_and_retired_routes_are_gone()
    {
        const string owner = "synthetic-owner-token";
        const string observer = "synthetic-observer-token-abcdefghijklmnopqrstuvwxyz";
        var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start(); var port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
        var config = new AppConfig();
        config.ControlApi.Enabled = true;
        config.ControlApi.Port = port;
        config.ControlApi.Token = owner;
        config.ControlApi.Security.Clients.Add(new()
        {
            Id = "observer", Name = "Observer", Scopes = ["Observe"],
            TokenHash = AutomationAccessController.HashToken(observer)
        });
        var module = new WriteModule();
        using var broker = new AutomationApprovalBroker();
        using var registry = new AutomationRegistry(module) { Access = new AutomationAccessController(config.ControlApi, broker) };
        using var api = new GlDrive.Services.ControlApi(config, registry);
        api.Start();
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false })
        { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(10) };
        var call = new { name = "test.write", arguments = new { } };

        using (var none = await http.GetAsync("automation/tools"))
            Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        http.DefaultRequestHeaders.Authorization = new("Bearer", "wrong-token");
        using (var wrong = await http.PostAsJsonAsync("automation/call", call))
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        http.DefaultRequestHeaders.Authorization = new("Bearer", observer);
        using (var catalog = await http.GetAsync("automation/tools"))
            Assert.Equal(HttpStatusCode.OK, catalog.StatusCode);
        using (var refused = await http.PostAsJsonAsync("automation/call", call))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal("scope_denied", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
        Assert.Equal(0, module.Calls);

        http.DefaultRequestHeaders.Authorization = new("Bearer", owner);
        foreach (var retired in new[] { "", "status", "sections", "races", "races/abc", "history" })
            using (var gone = await http.GetAsync(retired))
                Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        using (var start = await http.PostAsJsonAsync("races", new { section = "TV", release = "Some.Release-GRP" }))
            Assert.Equal(HttpStatusCode.NotFound, start.StatusCode);
        using (var wrongMethod = await http.GetAsync("automation/call"))
            Assert.Equal(HttpStatusCode.MethodNotAllowed, wrongMethod.StatusCode);
        using (var ok = await http.PostAsJsonAsync("automation/call", call))
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(1, module.Calls);
    }
}
