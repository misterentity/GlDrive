using System.IO;
using System.Text.Json;
using GlDrive.Services.Automation;
using Xunit;

namespace GlDrive.Tests;

public sealed class AutomationStateApprovalTests
{
    [Fact]
    public void Unchanged_snapshot_permits_the_reviewed_action()
    {
        var resource = new object();
        var approval = new AutomationStateApproval(new { host = "host-one", paths = new[] { "archive-one" }, secret = "credential" }, "Install host-one", ["credential"], resource);
        var invoked = false;
        approval.Validate(new { host = "host-one", paths = new[] { "archive-one" }, secret = "credential" }, resource);
        invoked = true;
        Assert.True(invoked);
    }

    [Theory]
    [InlineData("host-two", "archive-one", "credential")]
    [InlineData("host-one", "archive-two", "credential")]
    [InlineData("host-one", "archive-one", "new-credential")]
    public void Changed_endpoint_queue_or_credential_refuses_action(string host, string path, string secret)
    {
        var resource = new object();
        var approval = new AutomationStateApproval(new { host = "host-one", paths = new[] { "archive-one" }, secret = "credential" }, "Install host-one", ["credential"], resource);
        var invoked = false;
        Assert.Throws<AutomationDeniedException>(() =>
        {
            approval.Validate(new { host, paths = new[] { path }, secret }, resource);
            invoked = true;
        });
        Assert.False(invoked);
    }

    [Fact]
    public void Reconnected_session_requires_new_approval_even_for_same_endpoint()
    {
        var approval = new AutomationStateApproval(new { host = "host-one" }, "Install host-one", [], new object());
        Assert.Throws<AutomationDeniedException>(() => approval.Validate(new { host = "host-one" }, new object()));
    }

    [Fact]
    public void Description_redacts_raw_and_json_escaped_secrets_before_bounding()
    {
        const string secret = "credential\"with\\escapes";
        var approval = new AutomationStateApproval(new { secret }, secret + JsonSerializer.Serialize(secret) + new string('x', 9000), [secret]);
        Assert.DoesNotContain(secret, approval.Description);
        Assert.DoesNotContain(JsonSerializer.Serialize(secret)[1..^1], approval.Description);
        Assert.Contains("[redacted]", approval.Description);
        Assert.InRange(approval.Description.Length, 1, 8100);
    }

    [Fact]
    public void Installer_checks_bound_state_again_after_async_preflight()
    {
        var source = Read("UI", "GlftpdInstallerWindow.Automation.cs");
        var body = source[source.IndexOf("case \"installer.start\":", StringComparison.Ordinal)..source.IndexOf("case \"installer.cancel\":", StringComparison.Ordinal)];
        var preflight = body.IndexOf("await AutomationCommand", StringComparison.Ordinal);
        var run = body.IndexOf("await RunInstallationAsync", StringComparison.Ordinal);
        Assert.Contains("validateApproval?.Invoke();", body[..preflight]);
        Assert.Contains("validateApproval?.Invoke();", body[preflight..run]);
        var adapter = Read("Services/Automation", "InstallerAutomation.cs");
        Assert.Contains("ReferenceEquals(window, expectedWindow)", adapter);
        Assert.Contains("window.ValidateAutomationApproval(approval)", adapter);
        Assert.Contains("AutomationExecute(operation, arguments, ct, validate)", adapter);
    }

    [Fact]
    public void Extractor_state_and_binding_initialize_without_starting_saved_watchers()
    {
        var adapter = Read("Services/Automation", "ExtractorAutomation.cs");
        Assert.Contains("new ExtractorWindow(demoMode: false, startWatchers: false)", adapter);
        var execute = LifecycleSourceGuard.BlockAfter(adapter, "private async Task<object?> ExecuteCoreAsync");
        Assert.True(execute.IndexOf("ValidateAutomationApproval(approval)", StringComparison.Ordinal) < execute.IndexOf("AutomationExecute(operation", StringComparison.Ordinal));
        var source = Read("UI", "ExtractorWindow.xaml.cs");
        Assert.Contains("LoadSettings(startWatchers);", source);
        Assert.Contains("_watchEnabled = startWatchers && s.WatchEnabled", source);
        Assert.Contains("WatchEnabled = _watchEnabled || _watchResumeDeferred", source);
    }

    [Fact]
    public void Extractor_batch_captures_output_targets_before_the_first_await()
    {
        var source = Read("UI", "ExtractorWindow.xaml.cs");
        var body = LifecycleSourceGuard.BlockAfter(source, "private async Task ExtractAllAsync()");
        Assert.Contains("var outputDirectories = toExtract.ToDictionary(item => item, GetOutputDir);", body[..body.IndexOf("await", StringComparison.Ordinal)]);
        Assert.Contains("var outputDir = outputDirectories[item];", body);
    }

    [Fact]
    public void Automation_enabled_watchers_and_recurring_work_do_not_capture_call_attribution()
    {
        var source = Read("UI", "ExtractorWindow.xaml.cs");
        var sweep = LifecycleSourceGuard.BlockAfter(source, "private void StartAbandonSweep()");
        Assert.Contains("ExecutionContext.SuppressFlow()", sweep);
        Assert.True(sweep.IndexOf("ExecutionContext.SuppressFlow()", StringComparison.Ordinal) < sweep.IndexOf("SweepAbandonedAsync()", StringComparison.Ordinal));
        var watcher = LifecycleSourceGuard.BlockAfter(source, "private void StartWatcherFor(string folder)");
        Assert.Contains("ExecutionContext.SuppressFlow()", watcher);
        Assert.True(watcher.IndexOf("ExecutionContext.SuppressFlow()", StringComparison.Ordinal) < watcher.IndexOf("watcher.EnableRaisingEvents = true", StringComparison.Ordinal));
        var toggle = LifecycleSourceGuard.BlockAfter(source, "private void WatchToggle_Click");
        Assert.Contains("using (ExecutionContext.IsFlowSuppressed() ? default : ExecutionContext.SuppressFlow())", toggle);
        var detached = LifecycleSourceGuard.BlockAfter(toggle, "using (ExecutionContext.IsFlowSuppressed() ? default : ExecutionContext.SuppressFlow())");
        Assert.Contains("ScanAndAutoExtractAsync(folder)", detached);
        Assert.DoesNotContain("SaveSettings()", detached);
        var installer = LifecycleSourceGuard.BlockAfter(Read("UI", "GlftpdInstallerWindow.xaml.cs"), "private async Task RunInstallationAsync");
        Assert.Contains("ExecutionContext.SuppressFlow()", installer);
        Assert.True(installer.IndexOf("ExecutionContext.SuppressFlow()", StringComparison.Ordinal) < installer.IndexOf("_elapsedTimer.Start()", StringComparison.Ordinal));
    }

    private static string Read(string directory, string name)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "GlDrive.sln"))) root = root.Parent;
        return File.ReadAllText(Path.Combine(root!.FullName, "src", "GlDrive", directory, name));
    }
}
