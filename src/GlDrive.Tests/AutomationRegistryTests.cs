using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using GlDrive.Config;
using GlDrive.Services;
using GlDrive.Services.Automation;
using Xunit;

namespace GlDrive.Tests;

public class AutomationRegistryTests
{
    private sealed class Module : IAutomationModule
    {
        public int Calls;
        public Func<CancellationToken, Task<object?>> Handler = _ => Task.FromResult<object?>(new { saved = true });
        public IReadOnlyList<AutomationToolDefinition> Tools { get; } =
        [AutomationToolDefinition.Create("test.write", "Synthetic write", new { type = "object", properties = new { count = new { type = "integer", minimum = 1, maximum = 3 } }, required = new[] { "count" }, additionalProperties = false })];
        public Task<object?> ExecuteAsync(string operation, JsonElement arguments, CancellationToken ct)
        { Interlocked.Increment(ref Calls); return Handler(ct); }
    }
    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"count\":0}")]
    [InlineData("{\"count\":4}")]
    [InlineData("{\"count\":1.1}")]
    [InlineData("{\"count\":\"1\"}")]
    [InlineData("{\"count\":1,\"oops\":true}")]
    public async Task Invalid_arguments_never_reach_mutator(string args)
    {
        var module = new Module();
        using var registry = new AutomationRegistry(module);
        await Assert.ThrowsAsync<ArgumentException>(() => registry.CallAsync("test.write", Json(args), default));
        Assert.Equal(0, module.Calls);
    }

    [Fact]
    public async Task Pending_operation_survives_http_poll_and_can_be_cancelled()
    {
        var module = new Module { Handler = async ct => { await Task.Delay(Timeout.Infinite, ct); return null; } };
        using var registry = new AutomationRegistry(module) { InlineWait = TimeSpan.FromMilliseconds(20) };
        var pending = JsonSerializer.SerializeToElement(await registry.CallAsync("test.write", Json("{\"count\":1}"), default));
        Assert.Equal("running", pending.GetProperty("status").GetString());
        var id = pending.GetProperty("jobId").GetString();
        var input = JsonSerializer.SerializeToElement(new { jobId = id });
        await registry.CallAsync("app.jobs_cancel", input, default);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var state = JsonSerializer.SerializeToElement(await registry.CallAsync("app.jobs_get", input, default));
            if (state.GetProperty("status").GetString() == "cancelled") break;
            await Task.Delay(10, deadline.Token);
        }
        Assert.Equal(1, module.Calls);
    }

    [Fact]
    public async Task Long_operation_result_is_retrievable_and_detached()
    {
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var module = new Module { Handler = _ => completion.Task };
        using var registry = new AutomationRegistry(module) { InlineWait = TimeSpan.FromMilliseconds(10) };
        var pending = JsonSerializer.SerializeToElement(await registry.CallAsync("test.write", Json("{\"count\":1}"), default));
        completion.SetResult(new { value = "done" });
        var input = JsonSerializer.SerializeToElement(new { jobId = pending.GetProperty("jobId").GetString() });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var state = JsonSerializer.SerializeToElement(await registry.CallAsync("app.jobs_get", input, default));
            if (state.GetProperty("status").GetString() == "completed")
            { Assert.Equal("done", state.GetProperty("result").GetProperty("value").GetString()); break; }
            await Task.Delay(10, deadline.Token);
        }
    }

    [Fact]
    public async Task Actual_http_endpoint_requires_auth_and_validates_before_writing()
    {
        var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start(); var port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
        var config = new AppConfig { ControlApi = new() { Enabled = true, Port = port, Token = "synthetic-test-token" } };
        var module = new Module();
        using var registry = new AutomationRegistry(module);
        using var api = new ControlApi(config, () => null, () => [], registry);
        api.Start();
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
        using var denied = await http.PostAsJsonAsync("automation/call", new { name = "test.write", arguments = new { count = 1 } });
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Equal(0, module.Calls);
        http.DefaultRequestHeaders.Authorization = new("Bearer", config.ControlApi.Token);
        var tools = await http.GetFromJsonAsync<JsonElement>("automation/tools");
        Assert.Contains(tools.GetProperty("tools").EnumerateArray(), t => t.GetProperty("name").GetString() == "test.write");
        using var invalid = await http.PostAsJsonAsync("automation/call", new { name = "test.write", arguments = new { count = 0 } });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(0, module.Calls);
        using var success = await http.PostAsJsonAsync("automation/call", new { name = "test.write", arguments = new { count = 1 } });
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        Assert.True((await success.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("saved").GetBoolean());
        Assert.Equal(1, module.Calls);
    }

    [Fact]
    public async Task Awaited_relay_command_reports_completion_and_errors()
    {
        var value = 0;
        var command = new GlDrive.UI.RelayCommand(async () => { await Task.Yield(); value = 1; });
        await command.ExecuteAsync(null);
        Assert.Equal(1, value);
        var failing = new GlDrive.UI.RelayCommand(() => Task.FromException(new ArgumentException("test")));
        await Assert.ThrowsAsync<ArgumentException>(() => failing.ExecuteAsync(null));
    }
}
