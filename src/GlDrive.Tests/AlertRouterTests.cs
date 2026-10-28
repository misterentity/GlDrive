using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using GlDrive.Config;
using GlDrive.Services.Alerts;
using GlDrive.Services.Automation;
using GlDrive.UI;
using H.NotifyIcon.Core;
using Xunit;

namespace GlDrive.Tests;

public sealed class AlertRouterTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "alerts-test-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_directory, "alerts.json");
    public AlertRouterTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);
    private static AlertEvent Event(string text = "local only") => new(AlertKind.SiteOutage, AlertSeverity.Error, "Outage", text, "site");

    [Fact]
    public void Old_config_has_no_outbound_targets_or_unmeasured_default_on_limits()
    {
        var config = JsonSerializer.Deserialize<AppConfig>("{}", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.False(config.Alerts.Enabled);
        Assert.False(config.Alerts.Coalesce);
        Assert.False(config.Alerts.MorningDigestEnabled);
        Assert.Empty(config.Alerts.Targets);
        Assert.False(new AlertTargetConfig().Enabled);
        Assert.False(new AlertTargetConfig().IncludeReleaseNames);
        ConfigurationAutomation.Validate(config);
    }

    [Theory]
    [InlineData(23, true)] [InlineData(7, true)] [InlineData(8, false)] [InlineData(22, false)]
    public void Quiet_hours_wrap_midnight(int hour, bool quiet) =>
        Assert.Equal(quiet, AlertRouter.IsQuiet(new() { QuietHoursEnabled = true }, hour));

    [Fact]
    public void Quiet_hours_same_hour_means_all_day_but_disabled_never_mutes()
    {
        Assert.True(AlertRouter.IsQuiet(new() { QuietHoursEnabled = true, QuietStartHour = 1, QuietEndHour = 1 }, 10));
        Assert.False(AlertRouter.IsQuiet(new(), 0));
        Assert.True(AlertRouter.IsQuiet(new() { QuietHoursEnabled = true, QuietStartHour = 9, QuietEndHour = 17 }, 10));
    }

    [Fact]
    public void Corruption_starts_empty_and_copies_evidence()
    {
        File.WriteAllText(FilePath, "broken");
        var store = new AlertStore(FilePath, () => 10);
        Assert.Empty(store.Snapshot());
        Assert.Equal("broken", File.ReadAllText(FilePath + ".corrupt"));
        store.Add(Event(), DateTimeOffset.UtcNow, new());
        store.Save();
        Assert.Single(new AlertStore(FilePath, () => 10).Snapshot());
    }

    [Fact]
    public void Failed_write_retains_memory_and_capacity_evicts_oldest()
    {
        var store = new AlertStore(FilePath, () => 2, (_, _) => throw new IOException());
        for (var i = 0; i < 3; i++) { store.Add(Event(i.ToString()), DateTimeOffset.UnixEpoch.AddMinutes(i), new()); store.Save(); }
        Assert.Equal(new[] { "1", "2" }, store.Snapshot().Select(e => e.Alert.Message));
    }

    [Fact]
    public void Coalescing_is_opt_in_and_snooze_survives_restart()
    {
        var store = new AlertStore(FilePath, () => 10);
        var now = DateTimeOffset.UtcNow;
        var config = new AlertConfig { Coalesce = true };
        var first = store.Add(Event(), now, config);
        Assert.False(first.Repeated);
        Assert.True(store.Add(Event(), now.AddSeconds(1), config).Repeated);
        Assert.Equal(2, Assert.Single(store.Snapshot()).Count);
        Assert.True(store.Update(first.Entry.Id, true, now.AddHours(1)));
        store.Save();
        var restored = Assert.Single(new AlertStore(FilePath, () => 10).Snapshot());
        Assert.True(restored.Acknowledged);
        Assert.Equal(now.AddHours(1), restored.SnoozedUntilUtc);
        Assert.False(store.Add(Event(), now.AddMinutes(6), config).Repeated);
    }

    [Fact]
    public void Outbound_contract_excludes_bodies_accounts_hosts_channels_and_secrets()
    {
        var config = new AppConfig { Servers = [new() { Id = "site", Name = "Display ftp.invalid userfixture #private 192.0.2.1", Connection = new() { Host = "ftp.invalid", Username = "userfixture" }, Irc = new() { Channels = [new() { Name = "#private" }] } }] };
        var alert = Event("FiSH decrypted private conversation") with { Release = "Movie.2160p-secret123" };
        var target = new AlertTargetConfig();
        var body = AlertPayload.Build(alert, target, config, ["secret123"]);
        foreach (var secret in new[] { "ftp.invalid", "userfixture", "#private", "192.0.2.1", "FiSH", "Movie", "secret123" }) Assert.DoesNotContain(secret, body);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(3, json.RootElement.EnumerateObject().Count());
        target.IncludeReleaseNames = true;
        body = AlertPayload.Build(alert, target, config, ["secret123"]);
        Assert.Contains("Movie.2160p", body);
        Assert.DoesNotContain("secret123", body);
        body = AlertPayload.Build(alert, target, config, ["{\"token\":\"secret123\",\"user\":\"userfixture\"}"]);
        Assert.DoesNotContain("secret123", body);
        Assert.DoesNotContain("userfixture", body);
        config.Servers[0].Name = "Display old-alias.invalid";
        body = AlertPayload.Build(alert, target, config, []);
        Assert.DoesNotContain("old-alias.invalid", body);
    }

    [Fact]
    public async Task Blocked_sender_drops_only_outbound_work_and_every_notice_reaches_tray_and_history()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var trays = new List<string>();
        var config = new AppConfig { Alerts = new() { Enabled = true, QueueCapacity = 2, RetryCount = 0, Targets = [new() { Id = "test", Enabled = true }] } };
        await using var router = new AlertRouter(config, (_, message, _) => trays.Add(message), FilePath, async (_, _, ct) => { started.TrySetResult(); await blocked.Task.WaitAsync(ct); }, () => []);
        router.Publish(Event());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var i = 0; i < 100; i++) router.Publish(Event(i.ToString()));
        Assert.Equal(98, router.Dropped);
        Assert.Equal(101, trays.Count);
        Assert.Equal(101, router.Store.Snapshot().Length);
        config.Alerts.Targets[0].Enabled = false;
        config.Alerts.Enabled = false;
        router.Publish(Event("after disabling"));
        Assert.Equal("after disabling", trays[^1]);
        blocked.SetResult();
        Assert.Equal(101, router.Store.Snapshot().Length);
    }

    [Fact]
    public async Task Quiet_muted_and_disabled_targets_still_record_locally_without_sends()
    {
        var sends = 0;
        var trays = 0;
        var config = new AppConfig { Alerts = new() { Enabled = true, QuietHoursEnabled = true, QuietStartHour = 1, QuietEndHour = 1, Targets = [new() { Id = "test", Enabled = true }] } };
        await using var router = new AlertRouter(config, (_, _, _) => Interlocked.Increment(ref trays), FilePath, (_, _, _) => { sends++; return Task.CompletedTask; }, () => []);
        router.Publish(Event());
        await Until(() => router.Store.Snapshot().Length == 1);
        Assert.Equal(0, sends);
        Assert.Equal(0, trays);
        Assert.Contains("SiteOutage: 1", router.Digest(DateTimeOffset.UtcNow.AddHours(-1)));
    }

    [Fact]
    public async Task Heartbeat_ignores_alert_severity_and_has_no_local_history_or_toast()
    {
        var sent = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var config = new AppConfig { Alerts = new() { Enabled = true, Targets = [new() { Id = "alive", Kind = "heartbeat", Enabled = true }] } };
        await using var router = new AlertRouter(config, (_, _, _) => throw new Exception("No heartbeat toast"), FilePath,
            (_, body, _) => { sent.TrySetResult(body); return Task.CompletedTask; }, () => []);
        router.Tick(DateTimeOffset.Now);
        Assert.Contains("Heartbeat", await sent.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(router.Store.Snapshot());
    }

    [Fact]
    public async Task Snooze_does_not_spread_to_new_entries_even_with_the_same_alert_fields()
    {
        var trays = 0;
        var config = new AppConfig { Alerts = new() { Enabled = true } };
        await using var router = new AlertRouter(config, (_, _, _) => Interlocked.Increment(ref trays), FilePath, (_, _, _) => Task.CompletedTask, () => []);
        router.Publish(Event());
        await Until(() => Volatile.Read(ref trays) == 1);
        router.Store.Update(Assert.Single(router.Store.Snapshot()).Id, false, DateTimeOffset.UtcNow.AddHours(1));
        router.Publish(Event());
        await Until(() => router.Store.Snapshot().Length == 2);
        Assert.Equal(2, trays);
        Assert.Null(router.Store.Snapshot()[1].SnoozedUntilUtc);
    }

    [Fact]
    public async Task Retry_recovers_without_reemitting_tray_or_history()
    {
        var sends = 0;
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var config = new AppConfig { Alerts = new() { Enabled = true, RetryCount = 1, Targets = [new() { Id = "test", Enabled = true }] } };
        await using var router = new AlertRouter(config, (_, _, _) => { }, FilePath, (_, _, _) => { if (Interlocked.Increment(ref sends) == 1) throw new IOException(); delivered.SetResult(); return Task.CompletedTask; }, () => []);
        router.Publish(Event());
        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, sends);
        Assert.Single(router.Store.Snapshot());
    }

    [Theory]
    [InlineData("ntfy", "https://push.invalid/topic")]
    [InlineData("pushover", "https://api.pushover.net/1/messages.json")]
    [InlineData("telegram", "https://api.telegram.org/bottoken/sendMessage")]
    [InlineData("discord", "https://push.invalid/topic")]
    [InlineData("heartbeat", "https://push.invalid/topic")]
    public async Task Provider_adapters_only_post_to_explicit_target(string kind, string expected)
    {
        var handler = new CaptureHandler();
        using var transport = new AlertTransport(handler, _ => "{\"url\":\"https://push.invalid/topic\",\"token\":\"token\",\"user\":\"user\",\"chatId\":\"chat\"}");
        await transport.SendAsync(new() { Id = "test", Kind = kind }, "safe message", CancellationToken.None);
        Assert.Equal(expected, handler.Url);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Contains("safe", handler.Body);
    }

    [Theory]
    [InlineData(AlertSeverity.Information, NotificationIcon.Info)]
    [InlineData(AlertSeverity.Warning, NotificationIcon.Warning)]
    [InlineData(AlertSeverity.Error, NotificationIcon.Error)]
    public async Task Default_router_preserves_severity_through_the_tray_icon_mapping(AlertSeverity severity, NotificationIcon expected)
    {
        var delivered = new List<AlertSeverity>();
        await using var router = new AlertRouter(new(), (_, _, actual) => delivered.Add(actual), FilePath,
            (_, _, _) => throw new Exception("No outbound delivery while disabled"), () => []);
        router.Publish(Event() with { Severity = severity });
        Assert.Equal(expected, TrayIconSetup.NotificationIconFor(Assert.Single(delivered)));
        Assert.Empty(router.Store.Snapshot());
    }

    [Fact]
    public async Task Slow_target_cannot_hold_another_targets_delivery_queue()
    {
        var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fastDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var config = new AppConfig { Alerts = new() { Enabled = true, Targets =
            [new() { Id = "slow", Enabled = true }, new() { Id = "fast", Enabled = true }] } };
        await using var router = new AlertRouter(config, (_, _, _) => { }, FilePath, async (target, _, ct) =>
        {
            if (target.Id == "slow") { slowStarted.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }
            else fastDelivered.TrySetResult();
        }, () => []);
        router.Publish(Event());
        await slowStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fastDelivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(router.Store.Snapshot());
    }

    [Fact]
    public async Task Snoozing_one_general_entry_does_not_suppress_other_general_notices_after_restart()
    {
        var config = new AppConfig { Alerts = new() { Enabled = true } };
        await using (var router = new AlertRouter(config, (_, _, _) => { }, FilePath, (_, _, _) => Task.CompletedTask, () => []))
        {
            router.Publish(new(AlertKind.General, AlertSeverity.Information, "Connection", "Disconnected"));
            router.Store.Update(Assert.Single(router.Store.Snapshot()).Id, snoozedUntil: DateTimeOffset.UtcNow.AddDays(1));
        }
        var titles = new List<string>();
        await using var restored = new AlertRouter(config, (title, _, _) => titles.Add(title), FilePath, (_, _, _) => Task.CompletedTask, () => []);
        restored.Publish(new(AlertKind.General, AlertSeverity.Warning, "Authentication", "Login rejected"));
        restored.Publish(new(AlertKind.General, AlertSeverity.Information, "Update", "Available"));
        Assert.Equal(new[] { "Authentication", "Update" }, titles);
        Assert.NotNull(restored.Store.Snapshot()[0].SnoozedUntilUtc);
        Assert.All(restored.Store.Snapshot().Skip(1), entry => Assert.Null(entry.SnoozedUntilUtc));
    }

    [Fact]
    public async Task Snooze_cancels_only_that_entrys_queued_delivery()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new List<string>();
        var config = new AppConfig { Alerts = new() { Enabled = true, Targets = [new() { Id = "test", Enabled = true, IncludeReleaseNames = true }] } };
        await using var router = new AlertRouter(config, (_, _, _) => { }, FilePath, async (_, body, ct) =>
        {
            using var payload = JsonDocument.Parse(body);
            var name = payload.RootElement.GetProperty("release").GetString()!;
            sent.Add(name);
            if (name == "First") { started.TrySetResult(); await release.Task.WaitAsync(ct); }
            if (name == "Third") finished.TrySetResult();
        }, () => []);
        router.Publish(Event() with { Release = "First" });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        router.Publish(Event() with { Release = "Second" });
        router.Publish(Event() with { Release = "Third" });
        router.Store.Update(router.Store.Snapshot()[1].Id, snoozedUntil: DateTimeOffset.UtcNow.AddHours(1));
        release.TrySetResult();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "First", "Third" }, sent);
    }

    [Fact]
    public async Task Inbox_updates_preserve_omitted_fields_and_zero_clears_only_snooze()
    {
        await using var router = new AlertRouter(new() { Alerts = new() { Enabled = true } }, (_, _, _) => { }, FilePath,
            (_, _, _) => Task.CompletedTask, () => []);
        router.Publish(Event());
        var id = Assert.Single(router.Store.Snapshot()).Id;
        var automation = new AlertAutomation(() => router);
        async Task Update(object fields) => await automation.ExecuteAsync("alerts.update", JsonSerializer.SerializeToElement(fields), CancellationToken.None);
        await Update(new { id, acknowledged = true });
        await Update(new { id, snoozeMinutes = 60 });
        var snoozed = Assert.Single(router.Store.Snapshot());
        Assert.True(snoozed.Acknowledged);
        Assert.NotNull(snoozed.SnoozedUntilUtc);
        await Update(new { id, acknowledged = false });
        Assert.Equal(snoozed.SnoozedUntilUtc, Assert.Single(router.Store.Snapshot()).SnoozedUntilUtc);
        await Update(new { id, acknowledged = true });
        await Update(new { id, snoozeMinutes = 0 });
        Assert.True(Assert.Single(router.Store.Snapshot()).Acknowledged);
        Assert.Null(Assert.Single(router.Store.Snapshot()).SnoozedUntilUtc);
    }

    [Fact]
    public void Release_dates_remain_readable_but_actual_addresses_and_configured_secrets_are_redacted()
    {
        const string release = "Daily.Program.2026.10.27.1080p.WEB.h264-GRP";
        var target = new AlertTargetConfig { IncludeReleaseNames = true };
        using var payload = JsonDocument.Parse(AlertPayload.Build(Event() with { Release = release }, target, new(), []));
        Assert.Equal(release, payload.RootElement.GetProperty("release").GetString());
        var body = AlertPayload.Build(Event() with { Release = release + " 192.0.2.24 secret-fixture" }, target, new(), ["secret-fixture"]);
        Assert.Contains(release, body);
        Assert.DoesNotContain("192.0.2.24", body);
        Assert.DoesNotContain("secret-fixture", body);
    }

    [Fact]
    public async Task Published_history_is_batched_and_flushed_on_disposal()
    {
        var config = new AppConfig { Alerts = new() { Enabled = true } };
        await using (var router = new AlertRouter(config, (_, _, _) => { }, FilePath, (_, _, _) => Task.CompletedTask, () => []))
        {
            for (var i = 0; i < 20; i++) router.Publish(Event(i.ToString()));
            Assert.False(File.Exists(FilePath));
            Assert.Equal(20, router.Store.Snapshot().Length);
        }
        Assert.Equal(20, new AlertStore(FilePath, () => 100).Snapshot().Length);
    }

    [Fact]
    public void Clean_history_does_not_write_again_and_failed_write_remains_dirty()
    {
        var writes = 0;
        var store = new AlertStore(FilePath, () => 100, (_, _) => { if (++writes == 1) throw new IOException(); });
        for (var i = 0; i < 20; i++) store.Add(Event(i.ToString()), DateTimeOffset.UtcNow, new());
        store.Save();
        Assert.Equal(20, store.Snapshot().Length);
        store.Save();
        store.Save();
        Assert.Equal(2, writes);
    }

    [Fact]
    public async Task Slow_history_write_does_not_lock_local_alert_admission()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var store = new AlertStore(FilePath, () => 100, (_, _) => { started.TrySetResult(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); });
        store.Add(Event(), DateTimeOffset.UtcNow, new());
        var save = Task.Run(store.Save);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var admission = Task.Run(() => store.Add(Event("second"), DateTimeOffset.UtcNow, new()));
            await admission.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(2, store.Snapshot().Length);
        }
        finally { release.Set(); await save; }
    }

    [Theory]
    [InlineData(400, false)] [InlineData(401, false)] [InlineData(403, false)]
    [InlineData(408, true)] [InlineData(429, true)] [InlineData(503, true)]
    public async Task Http_failures_preserve_safe_class_retryability_and_retry_after(int status, bool retryable)
    {
        using var transport = new AlertTransport(new RefuseHandler(status), _ => "{\"url\":\"https://push.invalid/topic\"}");
        var failure = await Assert.ThrowsAsync<AlertDeliveryFailure>(() => transport.SendAsync(new() { Id = "test" }, "safe", CancellationToken.None));
        Assert.Equal("http-" + status, failure.FailureClass);
        Assert.Equal(retryable, failure.Retryable);
        Assert.Equal(TimeSpan.FromSeconds(90), failure.RetryAfter);
    }

    [Fact]
    public async Task Unconfigured_target_is_permanent_and_does_not_retry_each_alert_during_cooldown()
    {
        var calls = 0;
        var firstFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var config = new AppConfig { Alerts = new() { Enabled = true, RetryCount = 5, Targets = [new() { Id = "test", Enabled = true }] } };
        using var transport = new AlertTransport(credentials: _ => { Interlocked.Increment(ref calls); return null; });
        await using var router = new AlertRouter(config, (_, _, _) => { }, FilePath, transport.SendAsync, () => [],
            now: () => { if (Volatile.Read(ref calls) == 1) firstFailure.TrySetResult(); return DateTimeOffset.UnixEpoch; },
            delay: (_, _) => throw new Exception("Permanent failures must not retry"));
        router.Publish(Event());
        await firstFailure.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var i = 0; i < 20; i++) router.Publish(Event(i.ToString()));
        await Task.Delay(100);
        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.Equal(21, router.Store.Snapshot().Length);
    }

    [Fact]
    public async Task Retry_after_is_honoured_and_hand_edited_retry_count_is_clamped()
    {
        var calls = 0;
        var waits = new List<TimeSpan>();
        var exhausted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var config = new AppConfig { Alerts = new() { Enabled = true, RetryCount = 100, Targets = [new() { Id = "test", Enabled = true }] } };
        await using var router = new AlertRouter(config, (_, _, _) => { }, FilePath,
            (_, _, _) => { Interlocked.Increment(ref calls); throw new AlertDeliveryFailure("http-429", true, TimeSpan.FromSeconds(90)); }, () => [],
            now: () => { if (Volatile.Read(ref calls) == 6) exhausted.TrySetResult(); return DateTimeOffset.UnixEpoch; },
            delay: (wait, _) => { waits.Add(wait); return Task.CompletedTask; });
        router.Publish(Event());
        await exhausted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(6, Volatile.Read(ref calls));
        Assert.Equal(5, waits.Count);
        Assert.All(waits, wait => Assert.Equal(TimeSpan.FromSeconds(90), wait));
    }

    private sealed class RefuseHandler(int status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage((HttpStatusCode)status);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(90));
            return Task.FromResult(response);
        }
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Url, Body;
        public HttpMethod? Method;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Url = request.RequestUri!.ToString(); Method = request.Method; Body = await request.Content!.ReadAsStringAsync(ct); return new(HttpStatusCode.OK); }
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}
