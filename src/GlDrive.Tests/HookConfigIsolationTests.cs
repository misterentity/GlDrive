using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using GlDrive.Config;
using GlDrive.Services.Automation;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace GlDrive.Tests;

[CollectionDefinition("Hook config isolation", DisableParallelization = true)]
public sealed class HookConfigIsolationCollection { }

[Collection("Hook config isolation")]
public sealed class HookConfigIsolationTests : IDisposable
{
    private readonly byte[]? _original = File.Exists(ConfigManager.ConfigPath) ? File.ReadAllBytes(ConfigManager.ConfigPath) : null;
    private readonly ILogger _previousLogger = Log.Logger;
    private readonly Sink _sink = new();
    private readonly Logger _logger;

    public HookConfigIsolationTests()
    {
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), ConfigManager.ConfigPath, StringComparison.OrdinalIgnoreCase);
        _logger = new LoggerConfiguration().WriteTo.Sink(_sink).CreateLogger();
        Log.Logger = _logger;
    }

    [Theory]
    [InlineData("{ \"commands\": [ { \"events\": \"download.complete\" } ], \"note\": \"keep this draft\" }")]
    [InlineData("{ \"commands\": [ { \"enabled\": \"true\" } ] }")]
    [InlineData("{ \"commands\": [ { \"timeoutSeconds\": \"60\" } ] }")]
    [InlineData("{ \"commands\": null }")]
    [InlineData("[ \"keep this draft\" ]")]
    [InlineData("null")]
    public void Bad_hooks_are_disabled_logged_and_preserved_through_load_edit_and_save(string raw)
    {
        File.WriteAllText(ConfigManager.ConfigPath, """
            {"servers":[{"id":"example","name":"Example"}],"downloads":{"maxRetries":7},"hooks":
            """ + raw + "}");

        var config = ConfigManager.Load();
        Assert.Equal("example", Assert.Single(config.Servers).Id);
        Assert.Equal(7, config.Downloads.MaxRetries);
        Assert.Empty(config.Hooks.Commands);
        Assert.Equal(raw, config.Hooks.UnparsedJson);
        Assert.Contains(_sink.Events, e => e.Level == LogEventLevel.Error && e.MessageTemplate.Text.Contains("Hooks configuration"));

        var automation = new ConfigurationAutomation(config, ConfigManager.Save, () => config);
        var patch = new JsonArray(new JsonObject { ["op"] = "replace", ["path"] = "/downloads/maxRetries", ["value"] = 8 });
        automation.Preview(patch);
        automation.Apply(patch);
        var saved = File.ReadAllText(ConfigManager.ConfigPath);
        Assert.Contains(raw, saved);
        Assert.Equal(raw, JsonDocument.Parse(saved).RootElement.GetProperty("hooks").GetRawText());
        Assert.Equal(8, ConfigManager.Load().Downloads.MaxRetries);
        Assert.DoesNotContain("keep this draft", JsonSerializer.Serialize(automation.Get("/hooks")));
    }

    [Fact]
    public void Config_views_keep_hooks_masked_even_with_credential_read_authority()
    {
        var config = new AppConfig { Hooks = new() { Commands = [new() { Id = "private-hook", Enabled = true,
            Events = ["download.complete"], Executable = @"C:\Tools\private-hook.exe" }] } };
        var automation = new ConfigurationAutomation(config, _ => { });
        foreach (var includeSecrets in new[] { false, true })
        {
            var subtree = JsonSerializer.SerializeToNode(automation.Get("/hooks", includeSecrets))!;
            Assert.Equal("[redacted]", subtree["value"]!.GetValue<string>());
            Assert.DoesNotContain("private-hook", JsonSerializer.Serialize(automation.Get(includeSecrets: includeSecrets)));
        }
    }

    [Fact]
    public void Hand_edited_invalid_then_corrected_hooks_survive_runtime_reconciliation_and_later_saves()
    {
        var runtime = new AppConfig();
        var automation = new ConfigurationAutomation(runtime);
        const string raw = "{ \"commands\": [ { \"events\": \"draft\" } ], \"note\": \"retain\" }";
        File.WriteAllText(ConfigManager.ConfigPath, "{\"hooks\":" + raw + "}");
        automation.Apply([new JsonObject { ["op"] = "replace", ["path"] = "/downloads/maxRetries", ["value"] = 8 }]);
        Assert.NotNull(runtime.Hooks.UnparsedJson);
        ConfigManager.Save(runtime);
        Assert.Contains(raw, File.ReadAllText(ConfigManager.ConfigPath));

        File.WriteAllText(ConfigManager.ConfigPath, """{"hooks":{"commands":[]}}""");
        automation.Apply([new JsonObject { ["op"] = "replace", ["path"] = "/downloads/maxRetries", ["value"] = 9 }]);
        Assert.Null(runtime.Hooks.UnparsedJson);
        ConfigManager.Save(runtime);
        Assert.DoesNotContain("retain", JsonNode.Parse(File.ReadAllText(ConfigManager.ConfigPath))!["hooks"]!.ToJsonString());
    }

    [Fact]
    public void Corrected_block_loads_commands_on_next_read()
    {
        File.WriteAllText(ConfigManager.ConfigPath, """{"hooks":{"commands":[{"events":"bad"}]}}""");
        Assert.NotNull(ConfigManager.Load().Hooks.UnparsedJson);
        File.WriteAllText(ConfigManager.ConfigPath, """
            {"hooks":{"commands":[{"id":"copy","enabled":true,"events":["download.complete"],"executable":"C:\\Tools\\copy.exe"}]}}
            """);
        var config = ConfigManager.Load();
        Assert.Null(config.Hooks.UnparsedJson);
        Assert.True(Assert.Single(config.Hooks.Commands).Enabled);
    }

    public void Dispose()
    {
        if (_original == null) File.Delete(ConfigManager.ConfigPath);
        else File.WriteAllBytes(ConfigManager.ConfigPath, _original);
        Log.Logger = _previousLogger;
        _logger.Dispose();
    }

    private sealed class Sink : ILogEventSink
    {
        public ConcurrentQueue<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
    }
}
