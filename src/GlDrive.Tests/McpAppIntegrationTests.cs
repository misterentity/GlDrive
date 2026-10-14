using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using GlDrive.Config;
using GlDrive.Services;
using GlDrive.Services.Automation;
using Xunit;

namespace GlDrive.Tests;

public class McpAppIntegrationTests
{
    [Fact]
    public async Task Real_stdio_bridge_reads_and_writes_through_actual_authenticated_app_endpoint()
    {
        Assert.Null(System.Windows.Application.Current);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var config = new AppConfig { ControlApi = new() { Enabled = true, Port = port, Token = Guid.NewGuid().ToString("N") } };
        var saves = 0;
        using var registry = new AutomationRegistry(new ConfigurationAutomation(config, _ => Interlocked.Increment(ref saves)));
        using var api = new ControlApi(config, () => null, () => [], registry);
        api.Start();
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "GlDrive.sln"))) directory = directory.Parent;
        Assert.NotNull(directory);
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var executable = Path.Combine(directory.FullName, "src", "GlDrive.Mcp", "bin", configuration, "net10.0", "GlDrive.Mcp.dll");
        Assert.True(File.Exists(executable));
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(executable);
        start.Environment["GLDRIVE_CONTROL_PORT"] = port.ToString();
        start.Environment["GLDRIVE_CONTROL_TOKEN"] = config.ControlApi.Token;
        using var process = Process.Start(start)!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            async Task<JsonElement> Request(int id, string method, object parameters)
            {
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }));
                await process.StandardInput.FlushAsync();
                while (true)
                {
                    var line = await process.StandardOutput.ReadLineAsync(deadline.Token) ?? throw new IOException("MCP exited before responding");
                    using var response = JsonDocument.Parse(line);
                    if (response.RootElement.TryGetProperty("id", out var resultId) && resultId.ValueKind == JsonValueKind.Number && resultId.GetInt32() == id)
                    {
                        Assert.False(response.RootElement.TryGetProperty("error", out _));
                        return response.RootElement.GetProperty("result").Clone();
                    }
                }
            }
            await Request(1, "initialize", new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "integration-test", version = "1" } });
            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            await process.StandardInput.FlushAsync();
            var catalog = await Request(2, "tools/list", new { });
            Assert.Contains(catalog.GetProperty("tools").EnumerateArray(), t => t.GetProperty("name").GetString() == "config_patch");
            var read = await Request(3, "tools/call", new { name = "config_get", arguments = new { } });
            Assert.False(read.GetProperty("isError").GetBoolean());
            var initial = read.GetProperty("structuredContent");
            Assert.DoesNotContain(config.ControlApi.Token, read.GetRawText());
            var write = await Request(4, "tools/call", new { name = "config_patch", arguments = new
            {
                expectedRevision = initial.GetProperty("revision").GetString(),
                operations = new[] { new { op = "replace", path = "/logging/level", value = "Debug" } }
            } });
            Assert.False(write.GetProperty("isError").GetBoolean());
            Assert.True(write.TryGetProperty("structuredContent", out var written), "config_patch returned no structured content.");
            Assert.True(written.TryGetProperty("saved", out var saved),
                written.TryGetProperty("jobId", out _)
                    ? "config_patch did not complete within the inline deadline. It returned a background job; check for an unpumped test Application dispatcher."
                    : "config_patch completed without the required saved result.");
            Assert.True(saved.GetBoolean());
            Assert.Equal("Debug", config.Logging.Level); Assert.Equal(1, saves);
            var reread = await Request(5, "tools/call", new { name = "config_get", arguments = new { pointer = "/logging/level" } });
            Assert.False(reread.GetProperty("isError").GetBoolean());
            Assert.Equal("Debug", reread.GetProperty("structuredContent").GetProperty("value").GetString());
            var invalid = await Request(6, "tools/call", new { name = "config_patch", arguments = new { operations = new[] { new { op = "replace", path = "/unknownSetting", value = "bad" } } } });
            Assert.True(invalid.GetProperty("isError").GetBoolean()); Assert.Equal(1, saves);
        }
        finally
        {
            process.StandardInput.Close();
            if (!process.WaitForExit(5000)) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
    }
}
