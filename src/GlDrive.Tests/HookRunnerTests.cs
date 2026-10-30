using System.Diagnostics;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using GlDrive.Config;
using GlDrive.Services.Alerts;
using GlDrive.Services.Automation;
using GlDrive.Services.Hooks;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace GlDrive.Tests;

public sealed class HookRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hooks-" + Guid.NewGuid().ToString("N"));
    public HookRunnerTests() => Directory.CreateDirectory(_root);
    public void Dispose()
    {
        // The timeout test kills a real process tree. On CI the folder stayed in use briefly
        // after the hook's parent and child had both reported exit (runs 40787794556,
        // 40995764750, 40996827934), so teardown waits out the OS's asynchronous release.
        for (var attempt = 0; ; attempt++)
        {
            try { Directory.Delete(_root, true); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 40)
            { Thread.Sleep(250); }
        }
    }
    private static HookCommandConfig Command() => new()
    { Id = "one", Enabled = true, Events = ["download.complete"], Executable = Environment.ProcessPath! };
    private static HookEvent Event(string release = "Example.2026.1080p.WEB-GROUP") => new("download.complete", release, "Movies", "1080p", "Completed");

    [Fact]
    public void Defaults_execute_nothing_and_webhooks_are_empty()
    {
        var config = JsonSerializer.Deserialize<AppConfig>("{}", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Empty(config.Hooks.Commands);
        Assert.False(new HookCommandConfig().Enabled);
        Assert.Empty(new AlertTargetConfig().HookEvents);
    }

    [Theory]
    [InlineData("cmd.exe")]
    [InlineData("bash.exe")]
    [InlineData("wscript.exe")]
    [InlineData("python.exe")]
    [InlineData("node.exe")]
    public void Shells_and_implicit_script_interpreters_are_rejected(string name)
    {
        var command = Command(); command.Executable = Path.Combine(_root, name);
        Assert.Throws<ArgumentException>(() => HookCommand.Validate(command));
    }

    [Fact]
    public void PowerShell_uses_only_explicit_File_argument_and_preserves_script_data_arguments()
    {
        var command = Command(); command.Executable = @"C:\Tools\pwsh.exe";
        command.ScriptPath = @"C:\Scripts\notify.ps1";
        command.Arguments = ["-Destination", @"C:\Downloads\Movie", "-Command", "literal data"];
        var info = HookCommand.Build(command, Event("untrusted;value"));
        Assert.Equal(new[] { "-NoProfile", "-NonInteractive", "-File", command.ScriptPath }.Concat(command.Arguments), info.ArgumentList);
        command.ScriptPath = "";
        Assert.Throws<ArgumentException>(() => HookCommand.Build(command, Event()));
    }

    [Fact]
    public async Task Admission_is_nonblocking_serial_bounded_and_worker_does_not_inherit_attribution()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new List<string>();
        string? actor = null;
        using var attribution = ConfigWriteScope.Begin("mcp:synthetic");
        await using var runner = new HookRunner(new() { QueueCapacity = 1, Commands = [Command()] }, () => [], async (_, data, ct) =>
        {
            actor = ConfigWriteScope.Current.Actor;
            lock (received) received.Add(data.Release);
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
            return new("completed", 0, "", "");
        });
        runner.Publish(Event("first"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        runner.Publish(Event("second"));
        runner.Publish(Event("third"));
        Assert.Equal(1, runner.Dropped);
        Assert.NotEqual("mcp:synthetic", actor);
        release.SetResult();
        for (var i = 0; i < 100 && runner.Recent().Length < 2; i++) await Task.Delay(10);
        Assert.Equal(new[] { "first", "third" }, received);
    }

    [Fact]
    public async Task Malformed_hook_is_ignored_without_killing_worker_or_other_hooks()
    {
        var called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var runner = new HookRunner(new() { Commands = [new() { Enabled = true, Events = null! }, Command()] }, () => [], (_, _, _) =>
        { called.TrySetResult(); return Task.FromResult(new HookRunner.HookResult("completed", 0, "", "")); });
        runner.Publish(Event());
        await called.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Plain_text_stdout_and_stderr_are_bounded_redacted_and_logged()
    {
        var sink = new CapturingSink();
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        var command = Command();
        command.OutputLimitCharacters = 80;
        await using var runner = new HookRunner(new() { Commands = [command] }, () => [], (_, _, _) =>
            Task.FromResult(new HookRunner.HookResult("nonzero", 1,
                new string('x', 200) + "\nCopied 3 files synthetic-secret", "Cannot find path 'Q:\\'; synthetic-secret")),
            () => ["synthetic-secret"], logger);

        runner.Publish(Event());
        var logged = await sink.Output.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var stdout = (string)((ScalarValue)logged.Properties["Output"]).Value!;
        var stderr = (string)((ScalarValue)logged.Properties["Error"]).Value!;
        Assert.Contains("Copied 3 files", stdout);
        Assert.Contains("Cannot find path", stderr);
        Assert.Contains("sha256:", stdout);
        Assert.Contains("sha256:", stderr);
        Assert.DoesNotContain("synthetic-secret", logged.RenderMessage());
        Assert.True(stdout.Length <= 80);
        Assert.True(stderr.Length <= 80);
    }

    [Fact]
    public async Task Empty_hook_output_is_logged_without_a_json_parse_failure()
    {
        var sink = new CapturingSink();
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        await using var runner = new HookRunner(new() { Commands = [Command()] }, () => [], (_, _, _) =>
            Task.FromResult(new HookRunner.HookResult("completed", 0, "", "")), () => [], logger);
        runner.Publish(Event());
        var logged = await sink.Output.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("", ((ScalarValue)logged.Properties["Output"]).Value);
        Assert.Equal("", ((ScalarValue)logged.Properties["Error"]).Value);
    }

    [Fact]
    public async Task Invalid_definition_and_runtime_refusal_report_hook_id_and_reason()
    {
        var sink = new CapturingSink();
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        var invalid = Command();
        invalid.Id = "draft";
        invalid.Executable = "relative.exe";
        var command = Command();
        var called = false;
        await using var runner = new HookRunner(new() { Commands = [invalid, command] },
            () => [Path.GetDirectoryName(command.Executable)!], (_, _, _) =>
            { called = true; return Task.FromResult(new HookRunner.HookResult("completed", 0, "", "")); }, () => [], logger);
        Assert.Contains(sink.Events, e => e.Level == LogEventLevel.Warning && e.RenderMessage().Contains("draft") &&
            e.RenderMessage().Contains("absolute .exe path"));

        runner.Publish(Event());
        var logged = await sink.Output.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(called);
        var recent = Assert.Single(runner.Recent());
        Assert.Equal("one", recent.HookId);
        Assert.Equal("failed", recent.Outcome);
        Assert.Contains("outside content folders", recent.Reason);
        Assert.Contains(recent.Reason, logged.RenderMessage());
    }

    [Fact]
    public async Task Webhooks_bypass_slow_commands_and_unsubscribed_events_do_not_evict_local_work()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var config = new AppConfig { Alerts = new() { Enabled = true, Targets =
            [new() { Id = "hook", Kind = "webhook", Enabled = true, HookEvents = ["nuke"] }] } };
        await using var router = new AlertRouter(config, (_, _, _) => { }, Path.Combine(_root, "slow-alerts.json"),
            (_, body, _) => { sent.TrySetResult(body); return Task.CompletedTask; }, () => []);
        var received = new ConcurrentQueue<string>();
        await using var runner = new HookRunner(new() { QueueCapacity = 1, Commands = [Command()] }, () => [], async (_, data, ct) =>
        {
            received.Enqueue(data.Release);
            if (data.Release == "first")
            {
                started.TrySetResult();
                await release.Task.WaitAsync(ct);
            }
            else processed.TrySetResult();
            return new("completed", 0, "", "");
        }, () => []) { Alerts = router };
        runner.Publish(Event("first"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        runner.Publish(Event("second"));
        for (var i = 0; i < 100; i++) runner.Publish(new("race.finished", "unsubscribed", "", "", "Completed"));
        runner.Publish(new("nuke", "flagged", "", "", "Nuked"));

        Assert.Contains("nuke", await sent.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(release.Task.IsCompleted);
        Assert.Equal(0, runner.Dropped);
        release.SetResult();
        await processed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "first", "second" }, received);
    }

    [Fact]
    public async Task Local_queue_drops_are_logged_at_most_once_per_minute()
    {
        var sink = new CapturingSink();
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        await using var runner = new HookRunner(new() { QueueCapacity = 1, Commands = [Command()] }, () => [], async (_, _, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new("completed", 0, "", "");
        }, () => [], logger, () => now);
        runner.Publish(Event("first"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var i = 0; i < 10; i++) runner.Publish(Event("queued"));
        Assert.Equal(9, runner.Dropped);
        Assert.Single(sink.Events, e => e.Properties.ContainsKey("Dropped"));
        now = now.AddMinutes(1);
        runner.Publish(Event("later"));
        Assert.Equal(10, runner.Dropped);
        Assert.Equal(2, sink.Events.Count(e => e.Properties.ContainsKey("Dropped")));
    }

    [Fact]
    public async Task Missing_local_path_is_withheld_without_rejecting_hook()
    {
        var called = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var runner = new HookRunner(new() { Commands = [Command()] }, () => [], (_, data, _) =>
        { called.SetResult(data.LocalPath); return Task.FromResult(new HookRunner.HookResult("completed", 0, "", "")); });
        runner.Publish(Event() with { LocalPath = Path.Combine(_root, "missing") });
        Assert.Equal("", await called.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Automation_cannot_change_commands_even_with_secret_authority(bool allowSecrets)
    {
        var config = new AppConfig();
        var automation = new ConfigurationAutomation(config, _ => throw new Exception("must not save"));
        var patch = new JsonArray(new JsonObject { ["op"] = "replace", ["path"] = "/hooks", ["value"] = JsonSerializer.SerializeToNode(new HookConfig { Commands = [Command()] }, ConfigurationAutomation.Json) });
        Assert.Throws<ArgumentException>(() => automation.Apply(patch, allowSecrets: allowSecrets));
        Assert.Throws<ArgumentException>(() => automation.Preview(patch));
    }

    [Fact]
    public async Task Webhook_is_redacted_filtered_and_does_not_emit_a_tray_notice()
    {
        var config = new AppConfig { Alerts = new() { Enabled = true, Targets = [new() { Id = "hook", Kind = "webhook", Enabled = true, HookEvents = ["download.complete"] }] } };
        var sent = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tray = 0;
        await using var router = new AlertRouter(config, (_, _, _) => tray++, Path.Combine(_root, "alerts.json"),
            (_, body, _) => { sent.TrySetResult(body); return Task.CompletedTask; }, () => ["synthetic-secret"]);
        router.PublishHook("nuke", "ignored", "Nuked");
        router.Publish(new(AlertKind.DownloadComplete, AlertSeverity.Information, "ignored", "ignored"));
        Assert.False(sent.Task.IsCompleted);
        router.PublishHook("download.complete", "synthetic-secret.Host.192.0.2.20.Account.#channel", "Completed");
        using var payload = JsonDocument.Parse(await sent.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("download.complete", payload.RootElement.GetProperty("event").GetString());
        Assert.Equal(new[] { "severity", "kind", "event", "status" }, payload.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(1, tray); // Only the ordinary alert above.
    }

    [Fact]
    public async Task Real_script_times_out_keeps_bounded_output_and_kills_child_tree()
    {
        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
        Assert.True(File.Exists(shell));
        var script = Path.Combine(_root, "parent.ps1");
        var childScript = Path.Combine(_root, "child.ps1");
        var pidPath = Path.Combine(_root, "child.pid");
        await File.WriteAllTextAsync(childScript, "Start-Sleep -Seconds 60");
        await File.WriteAllTextAsync(script, "param($child, $pidFile)\n$start = [System.Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME 'pwsh.exe'))\n$start.UseShellExecute = $false\n$start.CreateNoWindow = $true\n$start.ArgumentList.Add('-NoProfile')\n$start.ArgumentList.Add('-File')\n$start.ArgumentList.Add($child)\n$p = [System.Diagnostics.Process]::Start($start)\n[System.IO.File]::WriteAllText($pidFile, $p.Id.ToString())\n[Console]::WriteLine(('x' * 20000) + 'TAIL-END')\nStart-Sleep -Seconds 60");
        var command = Command(); command.Executable = shell; command.ScriptPath = script;
        command.Arguments = [childScript, pidPath]; command.TimeoutSeconds = 3; command.OutputLimitCharacters = 32;
        var result = await HookRunner.ExecuteAsync(command, Event(), CancellationToken.None);
        Assert.Equal("timeout", result.Outcome);
        Assert.True(result.Output.Length <= 32);
        Assert.Contains("TAIL-END", result.Output);
        var pid = int.Parse(await File.ReadAllTextAsync(pidPath));
        try
        {
            using var child = Process.GetProcessById(pid);
            Assert.True(child.HasExited || child.WaitForExit(3000));
        }
        catch (ArgumentException) { /* Already reaped. */ }
    }

    private sealed class CapturingSink : ILogEventSink
    {
        public ConcurrentQueue<LogEvent> Events { get; } = new();
        public TaskCompletionSource<LogEvent> Output { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Emit(LogEvent logEvent)
        {
            Events.Enqueue(logEvent);
            if (logEvent.Properties.ContainsKey("Output")) Output.TrySetResult(logEvent);
        }
    }
}
