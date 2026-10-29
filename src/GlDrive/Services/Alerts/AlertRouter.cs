using System.IO;
using System.Threading.Channels;
using GlDrive.AiAgent;
using GlDrive.Config;
using Serilog;

namespace GlDrive.Services.Alerts;

public sealed class AlertRouter : IDisposable, IAsyncDisposable
{
    internal event Action<AlertEvent>? Observed;
    private sealed class TargetDelivery(Channel<PendingDelivery> queue)
    {
        public Channel<PendingDelivery> Queue { get; } = queue;
        public Task Worker { get; set; } = Task.CompletedTask;
        public int Failures { get; set; }
        public DateTimeOffset RetryAt { get; set; }
    }
    private sealed record PendingDelivery(AlertEvent Alert, string Kind, string? EntryId);
    private readonly AppConfig _config;
    private readonly Action<string, string, AlertSeverity> _tray;
    private readonly object _deliveryGate = new();
    private readonly Dictionary<string, TargetDelivery> _deliveries = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<AlertTargetConfig, string, CancellationToken, Task> _send;
    private readonly Func<IEnumerable<string>> _secrets;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly AlertTransport? _transport;
    private readonly Timer _timer;
    private readonly Timer _saveTimer;
    private Task _completion = Task.CompletedTask;
    private DateOnly? _digestDate;
    private DateTimeOffset _lastHeartbeat;
    private long _dropped;
    private int _disposed;
    private int _ticking;
    public AlertStore Store { get; }
    public long Dropped => Interlocked.Read(ref _dropped);

    public AlertRouter(AppConfig config, Action<string, string, AlertSeverity> tray, string? path = null)
        : this(config, tray, path ?? Path.Combine(ConfigManager.AppDataPath, "alerts.json"), null, null) { }

    internal AlertRouter(AppConfig config, Action<string, string, AlertSeverity> tray, string path,
        Func<AlertTargetConfig, string, CancellationToken, Task>? send, Func<IEnumerable<string>>? secrets,
        Func<DateTimeOffset>? now = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _config = config;
        _tray = tray;
        Store = new(path, () => config.Alerts.HistoryCapacity);
        if (send == null) { _transport = new(); _send = _transport.SendAsync; } else _send = send;
        _secrets = secrets ?? (() => OutboundSecretInventory.Collect());
        _now = now ?? (() => DateTimeOffset.Now);
        _delay = delay ?? Task.Delay;
        using var flow = ExecutionContext.IsFlowSuppressed() ? default : ExecutionContext.SuppressFlow();
        _timer = new(_ => Tick(_now()), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        _saveTimer = new(_ => Store.Save(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    public void Publish(AlertEvent alert)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        foreach (Action<AlertEvent> observer in Observed?.GetInvocationList() ?? [])
            try { observer(alert); } catch { /* Observers cannot interfere with delivery. */ }
        try
        {
            var settings = _config.Alerts;
            if (!settings.Enabled) { ShowTray(alert); return; }
            var now = _now();
            string? entryId = null;
            if (alert.Kind != AlertKind.Heartbeat)
            {
                var (entry, repeated) = Store.Add(alert, now, settings);
                entryId = entry.Id;
                // Snooze belongs to this inbox entry, never to all events of its kind.
                if (repeated || entry.SnoozedUntilUtc > now ||
                    settings.MutedKinds.Contains(alert.Kind.ToString(), StringComparer.OrdinalIgnoreCase) ||
                    settings.MutedServerIds.Contains(alert.ServerId) || IsQuiet(settings, now.LocalDateTime.Hour)) return;
                ShowTray(alert);
            }
            QueueDelivery(alert, entryId);
        }
        catch { Log.Warning("Alert processing failed; engine activity is unaffected"); }
    }

    private void ShowTray(AlertEvent alert)
    {
        try { _tray(alert.Title, alert.Message, alert.Severity); }
        catch { Log.Warning("Tray notification could not be shown"); }
    }

    private void QueueDelivery(AlertEvent alert, string? entryId = null)
    {
        if (IsLocalOnly(alert)) return;
        foreach (var target in _config.Alerts.Targets.ToArray())
        {
            if (target.Kind == "webhook" || alert.Kind == AlertKind.Hook)
            {
                if (target.Kind != "webhook" || alert.Kind != AlertKind.Hook || !target.HookEvents.Contains(alert.HookEvent)) continue;
            }
            if (!target.Enabled || (alert.Kind == AlertKind.Heartbeat) != (target.Kind == "heartbeat") ||
                (target.Kind == "smtp" ? alert.Kind != AlertKind.Digest : target.Kind is not ("heartbeat" or "webhook") && (int)alert.Severity < target.MinimumSeverity)) continue;
            lock (_deliveryGate)
            {
                if (_disposed != 0) return;
                if (!_deliveries.TryGetValue(target.Id, out var delivery))
                {
                    var queue = Channel.CreateBounded<PendingDelivery>(new BoundedChannelOptions(Math.Clamp(_config.Alerts.QueueCapacity, 1, 100000))
                    { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest, AllowSynchronousContinuations = false },
                        _ => Interlocked.Increment(ref _dropped));
                    delivery = new(queue);
                    _deliveries.Add(target.Id, delivery);
                    var id = target.Id;
                    using var flow = ExecutionContext.IsFlowSuppressed() ? default : ExecutionContext.SuppressFlow();
                    delivery.Worker = Task.Run(() => WorkAsync(id, delivery));
                }
                delivery.Queue.Writer.TryWrite(new(alert, target.Kind, entryId));
            }
        }
    }

    public void PublishHook(string eventName, string release, string status)
    {
        if (!_config.Alerts.Enabled || Volatile.Read(ref _disposed) != 0 || !Hooks.HookEvent.Names.Contains(eventName)) return;
        // Post-event webhooks do not create a second tray notice or carry a server identity.
        QueueDelivery(new(AlertKind.Hook, AlertSeverity.Information, "", "", Release: release,
            HookEvent: eventName, HookStatus: status));
    }

    internal static bool IsQuiet(AlertConfig config, int localHour) => config.QuietHoursEnabled &&
        (config.QuietStartHour == config.QuietEndHour || (config.QuietStartHour < config.QuietEndHour
            ? localHour >= config.QuietStartHour && localHour < config.QuietEndHour
            : localHour >= config.QuietStartHour || localHour < config.QuietEndHour));

    public string Digest(DateTimeOffset since) => string.Join(Environment.NewLine, Store.Snapshot()
        .Where(e => e.LastUtc >= since && e.Alert.Kind is not (AlertKind.Digest or AlertKind.Heartbeat))
        .GroupBy(e => (e.Alert.Severity, e.Alert.Kind))
        .OrderBy(g => g.Key.Severity).ThenBy(g => g.Key.Kind)
        .Select(g => $"{g.Key.Severity} / {g.Key.Kind}: {g.Sum(e => (long)e.Count)}"));

    internal void Tick(DateTimeOffset now)
    {
        if (Interlocked.Exchange(ref _ticking, 1) != 0) return;
        try { TickCore(now); }
        catch { Log.Warning("Alert timer could not process this tick; normal activity continues"); }
        finally { Volatile.Write(ref _ticking, 0); }
    }

    private void TickCore(DateTimeOffset now)
    {
        if (!_config.Alerts.Enabled || Volatile.Read(ref _disposed) != 0) return;
        var settings = _config.Alerts;
        var date = DateOnly.FromDateTime(now.LocalDateTime);
        if (settings.MorningDigestEnabled && now.LocalDateTime.Hour >= settings.DigestHourLocal && _digestDate != date)
        {
            _digestDate = date;
            if (!Store.Snapshot().Any(e => e.Alert.Kind == AlertKind.Digest && DateOnly.FromDateTime(e.LastUtc.LocalDateTime) == date))
                Publish(new(AlertKind.Digest, AlertSeverity.Information, "Morning digest", Digest(now.AddDays(-1))));
        }
        if (settings.Targets.Any(t => t.Enabled && t.Kind == "heartbeat") && now - _lastHeartbeat >= TimeSpan.FromMinutes(settings.HeartbeatMinutes))
        {
            _lastHeartbeat = now;
            QueueDelivery(new(AlertKind.Heartbeat, AlertSeverity.Information, "Heartbeat", ""));
        }
    }

    private async Task WorkAsync(string id, TargetDelivery delivery)
    {
        try
        {
            await foreach (var pending in delivery.Queue.Reader.ReadAllAsync(_stop.Token))
            {
                try { await DeliverAsync(id, delivery, pending).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch { Log.Warning("Alert delivery failed; engine activity is unaffected"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task DeliverAsync(string id, TargetDelivery delivery, PendingDelivery pending)
    {
        if (IsLocalOnly(pending.Alert)) return;
        if (_now() < delivery.RetryAt) return;
        var retries = Math.Clamp(_config.Alerts.RetryCount, 0, 5);
        for (var attempt = 0; attempt <= retries && !_stop.IsCancellationRequested; attempt++)
        {
            var current = _config.Alerts.Targets.FirstOrDefault(t => t.Id == id && t.Kind == pending.Kind && t.Enabled);
            if (!_config.Alerts.Enabled || current == null) return;
            if (pending.Alert.Kind == AlertKind.Hook && !current.HookEvents.Contains(pending.Alert.HookEvent)) return;
            if (pending.EntryId != null && Store.IsSnoozed(pending.EntryId, _now())) return;
            try
            {
                var alert = pending.Alert;
                var secrets = _secrets().ToArray();
                var body = AlertPayload.Build(alert, current, _config, secrets);
                if (alert.Kind == AlertKind.Digest)
                {
                    var rows = Store.Snapshot().Where(e => e.LastUtc >= _now().AddDays(-1) && !IsLocalOnly(e.Alert) && e.Alert.Kind is not (AlertKind.Digest or AlertKind.Heartbeat))
                        .Select(e => AlertPayload.Build(e.Alert, current, _config, secrets)).Distinct();
                    body = string.Join("\n", rows);
                    if (body.Length == 0) body = AlertPayload.Build(alert, current, _config, secrets);
                }
                if (body.Length > 1800) body = AlertPayload.Build(alert, current, _config, secrets);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(20));
                await _send(current, body, deadline.Token).ConfigureAwait(false);
                if (alert.Kind == AlertKind.Hook) Log.Information("Hook webhook {TargetId} {HookEvent}: delivered", id, alert.HookEvent);
                delivery.Failures = 0;
                delivery.RetryAt = default;
                return;
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                var failure = AlertDeliveryFailure.Classify(ex);
                if (failure.Retryable && attempt < retries)
                {
                    var wait = Max(TimeSpan.FromSeconds(1 << attempt), failure.RetryAfter ?? TimeSpan.Zero);
                    await _delay(wait, _stop.Token).ConfigureAwait(false);
                    continue;
                }
                delivery.Failures = Math.Min(delivery.Failures + 1, 10);
                var cooldown = TimeSpan.FromSeconds(Math.Min(1800, (failure.Retryable ? 30 : 300) * (1 << (delivery.Failures - 1))));
                delivery.RetryAt = _now() + Max(cooldown, failure.RetryAfter ?? TimeSpan.Zero);
                // Only a configured identifier and fixed failure class; no endpoint, body or credentials.
                Log.Warning("Alert target {TargetId} delivery paused ({FailureClass})", id, failure.FailureClass);
                return;
            }
        }
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
    internal static bool IsLocalOnly(AlertEvent alert) => alert.LocalOnly || alert.Kind is AlertKind.IrcMention or AlertKind.IrcPrivateMessage;

    public void Dispose()
    {
        lock (_deliveryGate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _timer.Dispose();
            _saveTimer.Dispose();
            _stop.Cancel();
            foreach (var delivery in _deliveries.Values) delivery.Queue.Writer.TryComplete();
            _completion = Task.WhenAll(_deliveries.Values.Select(d => d.Worker)).ContinueWith(_ =>
            {
                Store.Save();
                _transport?.Dispose();
                _stop.Dispose();
            }, TaskScheduler.Default);
        }
    }

    public async ValueTask DisposeAsync() { Dispose(); await _completion.ConfigureAwait(false); }
}
