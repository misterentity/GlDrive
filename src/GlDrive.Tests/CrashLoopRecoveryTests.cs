using System.IO;
using System.Text.Json;
using GlDrive.Config;
using GlDrive.Services;
using GlDrive.Services.Recovery;
using Xunit;

namespace GlDrive.Tests;

public sealed class CrashLoopRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GlDrive-crash-recovery-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static string Id() => Guid.NewGuid().ToString("N");
    public CrashLoopRecoveryTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public void First_and_second_crash_preserve_three_second_normal_recovery()
    {
        var chain = Id();
        var store = new CrashLoopHistory(Path.Combine(_root, "history.json"));
        for (var count = 1; count <= 2; count++)
        {
            var result = store.Record(new(Id(), chain, false), Now.AddSeconds(count), new());
            Assert.False(result.SafeMode);
            Assert.Equal(3, result.RestartDelaySeconds);
            Assert.Equal(count, result.RecentCrashes);
        }
        Assert.True(store.Record(new(Id(), chain, false), Now.AddSeconds(3), new()).SafeMode);
    }

    [Fact]
    public void WER_and_watchdog_restarts_of_same_session_count_once_across_reloads()
    {
        var path = Path.Combine(_root, "history.json"); var origin = new CrashRestartOrigin(Id(), Id(), false);
        new CrashLoopHistory(path).Record(origin, Now, new());
        var second = new CrashLoopHistory(path).Record(origin, Now.AddSeconds(1), new());
        Assert.False(second.SafeMode); Assert.Equal(1, second.RecentCrashes);
    }

    [Fact]
    public void A_normal_launch_ignores_old_loop_and_starts_a_fresh_chain()
    {
        var chain = Id(); var path = Path.Combine(_root, "watchdog-restarts.json");
        var history = new CrashLoopHistory(path);
        for (var i = 0; i < 3; i++) history.Record(new(Id(), chain, false), Now.AddSeconds(i), new());
        var launched = CrashRecoveryStartup.Prepare([], _root, Now.AddSeconds(4));
        Assert.False(launched.Decision.SafeMode); Assert.NotEqual(chain, launched.ChainId);
        File.WriteAllText(Path.Combine(_root, ".running"), ExitMarker.Running(Now.UtcDateTime, launched.SessionId));
        var firstCrash = CrashRecoveryStartup.Prepare(launched.NextCrash.Arguments(false).Split(' '), _root, Now.AddSeconds(5));
        Assert.False(firstCrash.Decision.SafeMode); Assert.Equal(1, firstCrash.Decision.RecentCrashes);
    }

    [Theory]
    [InlineData("--restart-after")]
    [InlineData("--update-restart")]
    [InlineData("--apply-update")]
    [InlineData("--apply-update-task")]
    [InlineData("--cleanup-old-updates")]
    [InlineData("--rollback-update")]
    public void Manual_and_update_flags_override_conflicting_crash_arguments(string flag)
    {
        var args = new CrashRestartOrigin(Id(), Id(), true).Arguments(false).Split(' ').Concat(new[] { flag, "123" }).ToArray();
        Assert.Null(CrashRestartOrigin.Parse(args));
        Assert.False(CrashRecoveryStartup.Prepare(args, _root, Now).Decision.SafeMode);
    }

    [Fact]
    public void Recovery_crash_round_trips_identity_but_explicit_recovery_is_ordinary_startup()
    {
        var origin = new CrashRestartOrigin(Id(), Id(), true);
        Assert.Equal(origin, CrashRestartOrigin.Parse(origin.Arguments(true).Split(' ')));
        Assert.Null(CrashRestartOrigin.Parse(["--recovery"]));
        Assert.Null(CrashRestartOrigin.Parse(["--crash-restart", origin.SessionId]));
        Assert.Null(CrashRestartOrigin.Parse(origin.Arguments(false).Split(' ').Concat(["--crash-restart", Id()]).ToArray()));
    }

    [Fact]
    public void Safe_mode_retries_back_off_then_give_normal_startup_a_fresh_chance()
    {
        var store = new CrashLoopHistory(Path.Combine(_root, "history.json")); var chain = Id();
        CrashRecoveryDecision decision = new(false, 0, 0, 3);
        for (var i = 0; i < 3; i++) decision = store.Record(new(Id(), chain, false), Now.AddSeconds(i), new());
        Assert.True(decision.SafeMode); Assert.Equal(60, decision.RestartDelaySeconds);
        decision = store.Record(new(Id(), chain, true), Now.AddMinutes(1), new());
        Assert.Equal(300, decision.RestartDelaySeconds);
        decision = store.Record(new(Id(), chain, true), Now.AddMinutes(6), new());
        Assert.Equal(900, decision.RestartDelaySeconds);
        decision = store.Record(new(Id(), chain, true), Now.AddMinutes(21), new());
        Assert.False(decision.SafeMode); Assert.Equal(3, decision.RestartDelaySeconds);
    }

    [Fact]
    public void Expired_or_future_events_do_not_turn_a_new_single_crash_into_a_loop()
    {
        var chain = Id();
        var records = new[] { new CrashRecoveryEvent(Id(), chain, Now.AddMinutes(-16), false, false), new(Id(), chain, Now.AddMinutes(1), false, false), new(Id(), chain, Now, false, false) };
        var result = CrashLoopPolicy.Evaluate(records, chain, Now, new());
        Assert.False(result.SafeMode); Assert.Equal(1, result.RecentCrashes);
    }

    [Fact]
    public void A_recovery_window_left_open_for_days_cannot_resume_the_engine_after_it_crashes()
    {
        var path = Path.Combine(_root, "history.json"); var chain = Id();
        var history = new CrashLoopHistory(path);
        for (var i = 0; i < 3; i++) history.Record(new(Id(), chain, false), Now.AddSeconds(i), new());
        var afterDays = new CrashLoopHistory(path).Record(new(Id(), chain, true), Now.AddDays(10), new());
        Assert.True(afterDays.SafeMode); Assert.Equal(300, afterDays.RestartDelaySeconds);
    }

    [Fact]
    public void No_config_or_stale_loop_flag_can_lower_threshold_below_three()
    {
        var chain = Id();
        var single = new[] { new CrashRecoveryEvent(Id(), chain, Now, true, true) };
        Assert.False(CrashLoopPolicy.Evaluate(single, chain, Now, new() { CrashLoopThreshold = 1 }).SafeMode);
        Assert.Throws<ArgumentException>(() => RecoveryConfig.Validate(new() { CrashLoopThreshold = 2 }));
    }

    [Fact]
    public void Disabling_protection_preserves_normal_recovery_after_many_crashes()
    {
        var store = new CrashLoopHistory(Path.Combine(_root, "history.json")); var chain = Id();
        for (var i = 0; i < 5; i++)
        {
            var result = store.Record(new(Id(), chain, false), Now.AddSeconds(i), new() { CrashLoopProtectionEnabled = false });
            Assert.False(result.SafeMode); Assert.Equal(3, result.RestartDelaySeconds);
        }
    }

    [Fact]
    public void Empty_retry_budget_keeps_normal_recovery_available()
    {
        var store = new CrashLoopHistory(Path.Combine(_root, "history.json")); var chain = Id();
        var settings = new RecoveryConfig { SafeModeRetryDelaysSeconds = [] };
        Assert.Equal(3, store.Record(new(Id(), chain, false), Now, settings).RestartDelaySeconds);
        Assert.Equal(3, store.Record(new(Id(), chain, false), Now.AddSeconds(1), settings).RestartDelaySeconds);
        Assert.Equal(3, store.Record(new(Id(), chain, false), Now.AddSeconds(2), settings).RestartDelaySeconds);
    }

    [Fact]
    public void BOM_settings_keep_explicit_recovery_opt_out_and_normal_launch_does_not_consult_broken_history()
    {
        var configPath = Path.Combine(_root, "appsettings.json");
        File.WriteAllText(configPath, "{\"recovery\":{\"crashLoopProtectionEnabled\":false}}", new System.Text.UTF8Encoding(true));
        Assert.False(CrashRecoveryStartup.ReadSettings(configPath).CrashLoopProtectionEnabled);
        var historyPath = Path.Combine(_root, "watchdog-restarts.json"); File.WriteAllText(historyPath, "{bad");
        var startup = CrashRecoveryStartup.Prepare([], _root, Now);
        Assert.False(startup.Decision.SafeMode);
        Assert.False(File.Exists(historyPath + ".corrupt")); Assert.Equal("{bad", File.ReadAllText(historyPath));
    }

    [Fact]
    public void Unreadable_history_is_preserved_and_does_not_gate_single_crash()
    {
        var path = Path.Combine(_root, "history.json"); File.WriteAllText(path, "{truncated");
        var notices = new List<string>();
        var history = new CrashLoopHistory(path, notices.Add);
        Assert.False(history.Record(new(Id(), Id(), false), Now, new()).SafeMode);
        Assert.Equal("{truncated", File.ReadAllText(path + ".corrupt")); Assert.Single(notices);
    }

    [Fact]
    public void Failed_write_keeps_memory_warns_once_and_capacity_evicts_oldest()
    {
        var notices = new List<string>(); var chain = Id();
        var history = new CrashLoopHistory(Path.Combine(_root, "history.json"), notices.Add, (_, _) => throw new IOException());
        for (var i = 0; i < CrashLoopHistory.Capacity + 1; i++) history.Record(new(Id(), chain, false), Now.AddSeconds(i), new());
        Assert.Single(notices); Assert.Equal(CrashLoopHistory.Capacity, history.Snapshot().Count);
        Assert.DoesNotContain(history.Snapshot(), e => e.TimeUtc == Now);
        Assert.True(history.Record(new(Id(), chain, false), Now.AddSeconds(300), new()).SafeMode);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"crashLoopThreshold\":1}")]
    [InlineData("{\"safeModeRetryDelaysSeconds\":null}")]
    [InlineData("{\"crashLoopThreshold\":3,\"crashLoopThreshold\":1}")]
    [InlineData("{\"autoInstallUpdates\":\"maybe\"}")]
    public void Malformed_optional_settings_preserve_the_rest_of_the_profile_and_raw_draft(string draft)
    {
        var json = "{\"servers\":[{\"id\":\"synthetic\",\"name\":\"Example\"}],\"recovery\":" + draft + "}";
        var config = JsonSerializer.Deserialize<AppConfig>(json, Json)!;
        Assert.Single(config.Servers); Assert.False(config.Recovery.CrashLoopProtectionEnabled);
        Assert.Equal(draft, config.Recovery.UnparsedJson);
        var roundtrip = JsonSerializer.Serialize(config, Json);
        Assert.Contains("\"recovery\":" + draft, roundtrip);
    }

    [Theory]
    [InlineData("{\"autoInstallUpdates\":false,\"updateProbationEnabled\":false,\"typo\":1}")]
    [InlineData("{\"autoInstallUpdates\":true,\"autoInstallUpdates\":false,\"updateProbationEnabled\":false,\"updateProbationEnabled\":true}")]
    [InlineData("{\"autoInstallUpdates\":false,\"autoInstallUpdates\":true,\"updateProbationEnabled\":true,\"updateProbationEnabled\":false}")]
    public void An_unrelated_malformed_field_or_duplicate_cannot_reenable_explicit_update_opt_outs(string draft)
    {
        var settings = JsonSerializer.Deserialize<RecoveryConfig>(draft, Json)!;
        Assert.False(settings.AutoInstallUpdates); Assert.False(settings.UpdateProbationEnabled);
        Assert.False(settings.CrashLoopProtectionEnabled); Assert.Equal(draft, settings.UnparsedJson);
    }

    [Fact]
    public void Offline_configuration_read_never_migrates_or_saves_and_tolerates_null_sections()
    {
        var path = Path.Combine(_root, "appsettings.json");
        const string original = "{\"servers\":null,\"downloads\":null,\"logging\":null,\"controlApi\":null}";
        File.WriteAllText(path, original);
        var config = CrashRecoveryStartup.ReadConfiguration(path);
        Assert.NotNull(config.Servers); Assert.NotNull(config.Downloads); Assert.NotNull(config.Logging); Assert.NotNull(config.ControlApi);
        Assert.Equal(original, File.ReadAllText(path)); Assert.Single(Directory.GetFiles(_root));
        File.WriteAllText(path, "{bad");
        Assert.Empty(CrashRecoveryStartup.ReadConfiguration(path).Servers);
        Assert.Equal("{bad", File.ReadAllText(path)); Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public void Oversized_profile_cannot_gate_normal_launch_or_prevent_recovery_presentation()
    {
        var path = Path.Combine(_root, "appsettings.json");
        using (var stream = File.Create(path)) stream.SetLength(CrashRecoveryStartup.MaxBootstrapProfileBytes + 1L);
        Assert.False(CrashRecoveryStartup.Prepare([], _root, Now).Decision.SafeMode);
        Assert.Empty(CrashRecoveryStartup.ReadConfiguration(path).Servers);
        Assert.Equal(CrashRecoveryStartup.MaxBootstrapProfileBytes + 1L, new FileInfo(path).Length);
        Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public void Session_marker_is_backward_compatible_and_requires_positive_identity_for_mismatch()
    {
        var session = Id(); var marker = ExitMarker.Running(Now.UtcDateTime, session);
        Assert.Equal(WatchdogExitVerdict.Unclean, ExitMarker.ClassifyForWatchdog(true, marker));
        Assert.False(ExitMarker.BelongsToDifferentSession(marker, session));
        Assert.True(ExitMarker.BelongsToDifferentSession(marker, Id()));
        Assert.False(ExitMarker.BelongsToDifferentSession("CRASH:" + Now.ToString("O"), session));
        Assert.False(ExitMarker.BelongsToDifferentSession("unreadable", session));
    }

    [Fact]
    public void Startup_branch_precedes_migrations_cleanup_and_every_engine_constructor()
    {
        var source = ReadSource("App.xaml.cs");
        var start = source.IndexOf("if (Program.RecoveryStartup?.Decision.SafeMode == true || e.Args.Contains(\"--recovery\"))", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var branch = LifecycleSourceGuard.BlockAfter(source, "if (Program.RecoveryStartup?.Decision.SafeMode == true || e.Args.Contains(\"--recovery\"))");
        Assert.Contains("return;", branch); Assert.Contains("CrashRecoveryStartup.ReadConfiguration", branch);
        Assert.DoesNotContain("ConfigManager.Load", branch);
        foreach (var constructor in new[] { "ConfigManager.Load(out", "UpdateChecker.CleanupOldUpdateFiles()", "DownloadNetworkServices.Initialize", "new ServerManager", "new GlDrive.AiAgent.AgentRunner", "new AutomationRegistry", "AutoStartExtractorWatch(" })
        {
            Assert.DoesNotContain(constructor, branch);
            Assert.True(source.IndexOf(constructor, start, StringComparison.Ordinal) > start, "Safe mode must precede " + constructor);
        }
        Assert.Contains("catch { /* Unavailable diagnostics storage", branch);
    }

    [Fact]
    public void Primary_claims_marker_before_WPF_and_WER_cannot_bypass_safe_mode_budget()
    {
        var program = ReadSource("Program.cs");
        var main = LifecycleSourceGuard.BlockAfter(program, "public static int Main");
        Assert.True(main.IndexOf("guard.TryAcquire()", StringComparison.Ordinal) < main.IndexOf("CrashRecoveryStartup.Prepare", StringComparison.Ordinal));
        Assert.True(main.IndexOf("File.WriteAllText(crashMarker", StringComparison.Ordinal) < main.IndexOf("SpawnWatchdog(", StringComparison.Ordinal));
        Assert.True(main.IndexOf("SpawnWatchdog(", StringComparison.Ordinal) < main.IndexOf("var app = new App()", StringComparison.Ordinal));
        var app = ReadSource("App.xaml.cs");
        Assert.Contains("if (Program.RecoveryStartup?.Decision.SafeMode != true)", app);
        const string registration = "RegisterApplicationRestart(Program.CrashRestartArguments(e.Args.Contains(\"--recovery\")), 0)";
        static void AssertInstallerRestartEnabled(string source) => Assert.Contains(registration, source);
        AssertInstallerRestartEnabled(app);
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertInstallerRestartEnabled(
            app.Replace(registration, registration.Replace(", 0)", ", 12)", StringComparison.Ordinal), StringComparison.Ordinal)));
        Assert.Contains("Program.PreviousSession ?? PreviousSessionDiagnostics.Capture", app);
        var watchdog = LifecycleSourceGuard.BlockAfter(program, "private static int RunWatchdog");
        Assert.Contains("Thread.Sleep(3000);", watchdog);
        Assert.Contains("origin?.WasSafeMode == true", watchdog);
        Assert.Contains("Math.Clamp(delaySeconds, 3, 3600) - 3", watchdog);
        var classify = watchdog.IndexOf("var crashReason = GetCrashReason(targetPid)", StringComparison.Ordinal);
        var claim = watchdog.IndexOf("using var markerGuard", classify, StringComparison.Ordinal);
        var write = watchdog.IndexOf("File.WriteAllText(crashMarker", classify, StringComparison.Ordinal);
        Assert.True(claim > classify && write > claim);
        Assert.Contains("if (!markerGuard.TryAcquire()) return 0;", watchdog[claim..write]);
        Assert.Contains("&& AnotherPrimaryIsRunning()", watchdog); // stale different session alone cannot suppress restart
    }

    [Fact]
    public void Rollback_is_local_offline_only_and_success_can_close_both_recovery_windows()
    {
        var recovery = ReadSource(Path.Combine("UI", "RecoveryCenterWindow.xaml.cs"));
        var handler = LifecycleSourceGuard.BlockAfter(recovery, "private async void Rollback_Click");
        var guard = handler.IndexOf("_busy || !_offline || _rollbackGeneration == null", StringComparison.Ordinal);
        var invoke = handler.IndexOf("UpdateChecker.TryLaunchRollback", StringComparison.Ordinal);
        Assert.True(guard >= 0 && invoke > guard);
        foreach (var source in new[] { recovery, ReadSource(Path.Combine("UI", "SafeModeWindow.xaml.cs")) })
        {
            var rollback = LifecycleSourceGuard.BlockAfter(source, "private async void Rollback_Click");
            var accepted = LifecycleSourceGuard.BlockAfter(rollback, "if (result.Started)");
            Assert.Contains("Busy(false);", accepted);
            Assert.True(accepted.IndexOf("Busy(false);", StringComparison.Ordinal) < accepted.IndexOf("Application.Current.Shutdown();", StringComparison.Ordinal));
            Assert.Contains("await Task.Run(() => UpdateChecker.TryLaunchRollback", rollback);
        }
        var uiAutomation = ReadSource(Path.Combine("Services", "Automation", "UiAutomation.cs"));
        Assert.DoesNotContain("RecoveryCenterWindow", uiAutomation);
        Assert.DoesNotContain("SafeModeWindow", uiAutomation);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("intended")]
    [InlineData("different-session")]
    public void Restart_manager_relaunch_after_installation_starts_fresh_despite_registered_crash_arguments(string markerKind)
    {
        var origin = new CrashRestartOrigin(Id(), Id(), false);
        var history = new CrashLoopHistory(Path.Combine(_root, "watchdog-restarts.json"));
        for (var i = 0; i < 3; i++) history.Record(new(Id(), origin.ChainId, false), Now.AddSeconds(i), new());
        var marker = Path.Combine(_root, ".running");
        if (markerKind == "intended") File.WriteAllText(marker, ExitMarker.Exiting(Now.UtcDateTime, "installer shutdown"));
        if (markerKind == "different-session") File.WriteAllText(marker, ExitMarker.Running(Now.UtcDateTime, Id()));
        var result = CrashRecoveryStartup.Prepare(origin.Arguments(false).Split(' '), _root, Now.AddMinutes(1));
        Assert.False(result.Decision.SafeMode);
        Assert.Equal(0, result.Decision.RecentCrashes);
        Assert.NotEqual(origin.ChainId, result.ChainId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_matching_crashed_session_still_enters_safe_mode_through_WER_or_watchdog(bool watchdog)
    {
        var origin = new CrashRestartOrigin(Id(), Id(), false);
        var history = new CrashLoopHistory(Path.Combine(_root, "watchdog-restarts.json"));
        for (var i = 0; i < 2; i++) history.Record(new(Id(), origin.ChainId, false), Now.AddSeconds(i), new());
        File.WriteAllText(Path.Combine(_root, ".running"),
            (watchdog ? ExitMarker.CrashPrefix : "") + ExitMarker.Running(Now.UtcDateTime, origin.SessionId));
        var result = CrashRecoveryStartup.Prepare(origin.Arguments(false).Split(' '), _root, Now.AddMinutes(1));
        Assert.True(result.Decision.SafeMode);
        Assert.Equal(3, result.Decision.RecentCrashes);
        Assert.Equal(origin.ChainId, result.ChainId);
    }

    [Fact]
    public void Exhausted_recovery_UI_retries_restart_normal_operation_with_a_fresh_chain()
    {
        var chain = Id();
        var history = new CrashLoopHistory(Path.Combine(_root, "watchdog-restarts.json"));
        for (var i = 0; i < 5; i++) history.Record(new(Id(), chain, i >= 3), Now.AddMinutes(i), new());
        var origin = new CrashRestartOrigin(Id(), chain, true);
        File.WriteAllText(Path.Combine(_root, ".running"), ExitMarker.Running(Now.UtcDateTime, origin.SessionId));
        var result = CrashRecoveryStartup.Prepare(origin.Arguments(false).Split(' '), _root, Now.AddMinutes(21));
        Assert.False(result.Decision.SafeMode);
        Assert.Equal(3, result.Decision.RestartDelaySeconds);
        Assert.NotEqual(chain, result.ChainId);
        File.WriteAllText(Path.Combine(_root, ".running"), ExitMarker.Running(Now.UtcDateTime, result.SessionId));
        var firstCrash = CrashRecoveryStartup.Prepare(result.NextCrash.Arguments(false).Split(' '), _root, Now.AddMinutes(22));
        Assert.False(firstCrash.Decision.SafeMode);
        Assert.Equal(1, firstCrash.Decision.RecentCrashes);
    }

    [Fact]
    public async Task Safe_mode_keeps_updater_running_and_automatically_resumes_after_backoff()
    {
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var updateChecksStarted = false;
        var resumed = false;
        var run = SafeModeRecoverySchedule.RunAsync(new(), () => updateChecksStarted = true,
            () => resumed = true, CancellationToken.None, (delay, _) =>
            {
                Assert.Equal(TimeSpan.FromMinutes(21), delay);
                return waiting.Task;
            });
        Assert.True(updateChecksStarted);
        Assert.False(resumed);
        waiting.SetResult();
        await run;
        Assert.True(resumed);
    }

    [Fact]
    public async Task Safe_mode_waits_for_active_recovery_then_retries_normal_launch()
    {
        var delays = new List<TimeSpan>();
        var attempts = 0;
        await SafeModeRecoverySchedule.RunAsync(new(), () => { }, () => ++attempts == 3,
            CancellationToken.None, (delay, _) => { delays.Add(delay); return Task.CompletedTask; });
        Assert.Equal(3, attempts);
        Assert.Equal(new[] { TimeSpan.FromMinutes(21), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30) }, delays);
        Assert.InRange(SafeModeRecoverySchedule.ResumeDelay(new() { SafeModeRetryDelaysSeconds = [] }).TotalMinutes, 1, 60);
        Assert.Equal(TimeSpan.FromHours(1), SafeModeRecoverySchedule.ResumeDelay(new() { SafeModeRetryDelaysSeconds = Enumerable.Repeat(3600, 8).ToArray() }));
    }

    [Fact]
    public async Task Closing_safe_mode_cancels_automatic_resume()
    {
        using var cancellation = new CancellationTokenSource();
        var resumed = false;
        var run = SafeModeRecoverySchedule.RunAsync(new(), () => { }, () => resumed = true,
            cancellation.Token, (_, token) => Task.Delay(Timeout.Infinite, token));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.False(resumed);
    }

    [Fact]
    public void Safe_mode_wires_signed_updater_and_only_shuts_down_after_the_install_handoff()
    {
        var source = ReadSource("UI/SafeModeWindow.xaml.cs");
        Assert.Contains("_updater.StartPeriodicCheck(_lifetime.Token)", source);
        Assert.Contains("AutoInstallEnabled = () => config.Recovery.AutoInstallUpdates", source);
        var handoff = LifecycleSourceGuard.BlockAfter(source, "_updater.RestartRequested +=");
        Assert.Contains("Application.Current.Shutdown();", handoff);
        var resume = LifecycleSourceGuard.BlockAfter(source, "private bool TryResume()");
        Assert.True(resume.IndexOf("TryStopForRecoveryRestart", StringComparison.Ordinal) < resume.IndexOf("Process.Start", StringComparison.Ordinal));
        Assert.Contains("--restart-after", resume);
    }

    [Fact]
    public void Active_install_cannot_overlap_recovery_and_recovery_cannot_admit_an_install()
    {
        var admission = new UpdateRecoveryAdmission();
        using (var install = admission.TryBeginInstall())
        {
            Assert.NotNull(install);
            Assert.True(admission.Installing);
            Assert.Null(admission.TryBeginRecovery());
            Assert.False(admission.TryStopForRestart());
        }
        Assert.True(admission.TryStopForRestart());
        admission.RestartFailed();
        using (var recovery = admission.TryBeginRecovery())
        {
            Assert.NotNull(recovery);
            Assert.Null(admission.TryBeginInstall());
            Assert.False(admission.TryStopForRestart());
        }
        Assert.True(admission.TryStopForRestart());
        Assert.Null(admission.TryBeginInstall());
        admission.RestartFailed();
        using var nextInstall = admission.TryBeginInstall();
        Assert.NotNull(nextInstall);
    }

    [Fact]
    public async Task Simultaneous_update_and_recovery_admission_have_exactly_one_owner()
    {
        var admission = new UpdateRecoveryAdmission();
        using var ready = new Barrier(2);
        var results = await Task.WhenAll(Task.Run(() => { ready.SignalAndWait(); return admission.TryBeginInstall(); }),
            Task.Run(() => { ready.SignalAndWait(); return admission.TryBeginRecovery(); }));
        Assert.Single(results, lease => lease != null);
        foreach (var lease in results) lease?.Dispose();
        using var resumed = admission.TryBeginInstall();
        Assert.NotNull(resumed);
    }

    private static string ReadSource(string name)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "GlDrive.sln"))) root = root.Parent;
        return File.ReadAllText(Path.Combine(root!.FullName, "src", "GlDrive", name));
    }
}
