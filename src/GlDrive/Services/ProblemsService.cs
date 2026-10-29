using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GlDrive.AiAgent;
using GlDrive.Config;
using GlDrive.Irc;
using GlDrive.Services.Automation;
using GlDrive.Tls;
using GlDrive.Util;
using Serilog;

namespace GlDrive.Services;

public sealed record ProblemFixPreview(string ProblemId, string Revision, string Description, string Diff, string Impact)
{
    internal JsonArray Operations { get; init; } = [];
}

/// <summary>Background local diagnostics. Its cached findings never participate in operational admission.</summary>
public sealed class ProblemsService : IDisposable
{
    private readonly ConfigurationAutomation _configuration;
    private readonly Func<AppConfig, DoctorContext> _capture;
    private readonly Func<Func<string, string>> _redactor;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly System.Threading.Timer? _timer;
    private ProblemSnapshot _snapshot = new([], DateTimeOffset.MinValue, false);
    private bool _warned;
    private volatile bool _disposed;
    public ProblemSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public event Action? Changed;

    public ProblemsService(AppConfig config, ServerManager manager, CertificateManager certificates,
        ConfigurationAutomation configuration)
        : this(configuration, current => Capture(current, manager, certificates),
            CachedRedactor(config), start: true) { }

    private static Func<Func<string, string>> CachedRedactor(AppConfig config)
    {
        var diagnostics = new DiagnosticAutomation(config);
        return () => diagnostics.CreateTextRedactor(requireCompleteInventory: true);
    }

    internal ProblemsService(ConfigurationAutomation configuration, Func<AppConfig, DoctorContext> capture,
        Func<Func<string, string>>? redactor = null, bool start = false, TimeSpan? initialDelay = null)
    {
        _configuration = configuration;
        _capture = capture;
        _redactor = redactor ?? (() => value => value);
        if (!start) return;
        // Construction can happen inside an automation-created window/session.
        // Recurring checks must not inherit that request's actor or other ambient state.
        if (ExecutionContext.IsFlowSuppressed()) _timer = CreateTimer(initialDelay);
        else { using (ExecutionContext.SuppressFlow()) _timer = CreateTimer(initialDelay); }
        ConfigManager.SettingsSaved += SettingsSaved;
    }

    private System.Threading.Timer CreateTimer(TimeSpan? initialDelay) => new(_ => _ = RefreshAsync(), null,
        initialDelay ?? TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(5));

    private void SettingsSaved(string path, JsonNode? before, JsonNode after, ConfigWriteContext actor)
    {
        if (!_disposed && string.Equals(path, ConfigManager.ConfigPath, StringComparison.OrdinalIgnoreCase))
            try { _timer?.Change(TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(5)); }
            catch (ObjectDisposedException) { }
    }

    public Task<ProblemSnapshot> RefreshAsync(CancellationToken ct = default) => Task.Run(() => Refresh(ct), ct);

    private ProblemSnapshot Refresh(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_disposed || !_refreshGate.Wait(0)) return Snapshot;
        try
        {
            var config = _configuration.CurrentSnapshot().Deserialize<AppConfig>(ConfigurationAutomation.Json)
                ?? throw new InvalidDataException("Settings snapshot unavailable");
            var context = _capture(config);
            ct.ThrowIfCancellationRequested();
            var result = HealthDoctor.Evaluate(context, DateTimeOffset.UtcNow);
            var redact = _redactor();
            bool incompleteText = false;
            string Text(string value)
            {
                var safe = RedactField(value, redact);
                if (value.Length > 0 && (safe.Length == 0 || safe == "[redaction unavailable]")) incompleteText = true;
                return safe;
            }
            result = result with { Problems = result.Problems.Select(p => p with
            {
                Title = Text(p.Title), Evidence = Text(p.Evidence), Remedy = Text(p.Remedy)
            }).ToArray() };
            result = result with { Partial = result.Partial || incompleteText };
            if (_disposed) return Snapshot;
            Volatile.Write(ref _snapshot, result);
            NotifyChanged();
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Diagnostics failure retains the last observations and never changes operational state.
            var previous = Snapshot with { Partial = true };
            Volatile.Write(ref _snapshot, previous);
            if (!_warned) { _warned = true; Log.Warning("Problems check unavailable; retaining previous local findings"); }
            NotifyChanged();
            return previous;
        }
        finally { _refreshGate.Release(); }
    }

    public ProblemFixPreview PreviewFix(string id)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Local drive/audit reads and credential inventory happen outside the shared settings lock.
        var node = _configuration.CurrentSnapshot();
        var config = node.Deserialize<AppConfig>(ConfigurationAutomation.Json)!;
        var problem = HealthDoctor.Evaluate(_capture(config), DateTimeOffset.UtcNow).Problems.FirstOrDefault(p => p.Id == id)
            ?? throw new InvalidOperationException("The condition changed. Refresh before previewing a fix.");
        var fix = problem.Fix ?? throw new InvalidOperationException("This condition requires manual review; there is no automatic fix.");
        var operations = new JsonArray(
            new JsonObject { ["op"] = "test", ["path"] = fix.Path, ["value"] = fix.Before?.DeepClone() },
            new JsonObject { ["op"] = "replace", ["path"] = fix.Path, ["value"] = fix.After?.DeepClone() });
        var diff = $"{fix.Path}\nBefore: {fix.Before?.ToJsonString() ?? "null"}\nAfter: {fix.After?.ToJsonString() ?? "null"}";
        if (diff.Length > 4096) throw new InvalidOperationException("This fix is too large for a complete preview; review it in Settings.");
        var runtime = _configuration.Preview(operations); // Same validation and runtime classification as ordinary edits.
        var redact = _redactor();
        var canonical = ChangeApplier.CanonicalizeTarget(fix.Path, config);
        var freezes = App.FreezeStore?.All.Where(f => FrozenDoctorReader.Affects(f.Path, canonical, config)).Take(8).ToArray() ?? [];
        var freezeNote = freezes.Length == 0 ? "" : "\nThis setting is frozen for the agent. Your explicit local edit does not remove that freeze. " +
            string.Join("; ", freezes.Select(f => f.Note));
        if (Revision(_configuration.CurrentSnapshot()) != Revision(node))
            throw new InvalidOperationException("Settings changed; preview the fix again.");
        var safeDescription = RedactField(problem.Title, redact);
        var safeDiff = RedactField(diff, redact);
        var safeImpact = RedactField(fix.Impact + "\n" + RuntimeImpact(runtime, config) + freezeNote, redact);
        if (safeDiff != diff || string.IsNullOrWhiteSpace(safeDescription) || string.IsNullOrWhiteSpace(safeImpact) ||
            safeDescription == "[redaction unavailable]" || safeImpact == "[redaction unavailable]")
            throw new InvalidOperationException("A complete fix preview is unavailable after redaction; review this setting locally in Settings.");
        return new(id, Revision(node), safeDescription, safeDiff, safeImpact) { Operations = operations };
    }

    private static string RuntimeImpact(object preview, AppConfig config)
    {
        var result = JsonSerializer.SerializeToElement(preview);
        var lines = new List<string>();
        foreach (var server in result.GetProperty("servers").EnumerateArray())
        {
            var changes = server.GetProperty("changes").EnumerateArray().Select(value => value.GetString()).ToArray();
            if (changes.Length == 0) continue;
            var id = server.GetProperty("serverId").GetString();
            var name = config.Servers.FirstOrDefault(candidate => candidate.Id == id)?.Name;
            lines.Add($"{(string.IsNullOrWhiteSpace(name) ? id : name)}: {string.Join(", ", changes)}; " +
                $"remount: {(server.GetProperty("remount").GetBoolean() ? "yes" : "no")}; " +
                $"IRC restart: {(server.GetProperty("ircRestart").GetBoolean() ? "yes" : "no")}.");
        }
        lines.Add("Saving any fix refreshes runtime settings for all servers and can resume due mount retries for enabled, unmounted servers. Existing retry and login limits remain in force.");
        return string.Join("\n", lines);
    }

    public async Task ApplyFixAsync(ProblemFixPreview preview, CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (preview.Operations.Count != 2) throw new ArgumentException("Preview the fix before applying it.", nameof(preview));
            var node = _configuration.CurrentSnapshot();
            if (Revision(node) != preview.Revision) throw new InvalidOperationException("Settings changed; preview the fix again.");
            var config = node.Deserialize<AppConfig>(ConfigurationAutomation.Json)!;
            var current = HealthDoctor.Evaluate(_capture(config), DateTimeOffset.UtcNow).Problems.FirstOrDefault(p => p.Id == preview.ProblemId);
            if (current?.Fix == null) throw new InvalidOperationException("The condition changed; refresh before applying a fix.");
        }, ct).ConfigureAwait(false);
        void Apply()
        {
            ct.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var actor = ConfigWriteScope.Begin("settings-ui:problems");
            // Apply owns the atomic revision/test/save lock. Reconcile must run on the same
            // dispatcher as ordinary config_patch because the UI reads the live collections.
            _configuration.Apply((JsonArray)preview.Operations.DeepClone(), preview.Revision);
        }
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess()) await dispatcher.InvokeAsync(Apply);
        else Apply();
    }

    private void NotifyChanged()
    {
        foreach (Action handler in Changed?.GetInvocationList() ?? [])
            try { handler(); } catch { /* A view cannot affect operational or diagnostic work. */ }
    }

    internal static string Revision(JsonNode node) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(node.ToJsonString()))).ToLowerInvariant();

    internal static string RedactField(string value, Func<string, string> redact)
    {
        // Cutting raw text may expose the prefix of a credential spanning the boundary.
        if (value.Length > 4096) return "";
        var safe = redact(value);
        return safe.Length <= 4096 ? safe : "";
    }

    private static DoctorContext Capture(AppConfig config, ServerManager manager, CertificateManager certificates)
    {
        var servers = config.Servers.Take(HealthDoctor.MaxServers).Select(server =>
        {
            var mount = manager.GetServer(server.Id);
            var monitor = mount?.ReleaseMonitorHealth.Snapshot();
            var irc = manager.GetIrcService(server.Id);
            return new DoctorServer(server.Id, manager.GetSiteHealth(server.Id), mount?.CurrentState == MountState.Connected,
                monitor?.Empty ?? false, monitor?.Path ?? "", monitor?.CheckedAtUtc,
                irc?.State == IrcServiceState.Connected, irc?.GetChannelSnapshot().Select(c => c.Name).Take(256).ToArray() ?? [],
                manager.AnnounceHealth(server.Id), irc?.KeyStore.RecoveryRequired == true || irc?.KeyStore.PersistenceHealthy == false);
        }).ToArray();
        var paths = new[] { config.Downloads.LocalPath }.Concat(config.Downloads.CategoryPaths.Values)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(257).ToArray();
        var drives = ReadDriveRoots(paths.Take(256));
        var frozen = FrozenDoctorReader.Read(Path.Combine(ConfigManager.AppDataPath, "ai-data", "ai-audit.jsonl"),
            App.FreezeStore?.All ?? [], config);
        return new(config, servers, drives, frozen.Decisions,
            certificates.GetPendingDecisions().Take(HealthDoctor.MaxServers).Select(d => new DoctorCertificate(d.Id, d.HostPort, d.DetectedAtUtc, d.KeptRejected)).ToArray(),
            paths.Length > 256 || frozen.Partial, frozen.Bounded, FishKeyRecovery.Snapshot.NeedsReview);
    }

    internal static IReadOnlyList<DoctorDrive> ReadDriveRoots(IEnumerable<string> paths)
    {
        var roots = paths.Select(LocalDriveRoot).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var result = new List<DoctorDrive>();
        string[] present;
        try { present = DriveInfo.GetDrives().Select(d => d.Name).ToArray(); }
        catch { return roots.Select(root => new DoctorDrive(root ?? "Non-local target", "Unavailable")).ToArray(); }
        foreach (var root in roots)
        {
            if (root == null) { result.Add(new("Non-local or relative target", "Not checked")); continue; }
            if (!present.Contains(root, StringComparer.OrdinalIgnoreCase)) { result.Add(new(root, "Missing")); continue; }
            try
            {
                var drive = new DriveInfo(root);
                result.Add(new(root, drive.DriveType == DriveType.Network ? "Network drive not checked" : drive.IsReady ? "Ready" : "Unavailable"));
            }
            catch { result.Add(new(root, "Unavailable")); }
        }
        return result;
    }

    internal static string? LocalDriveRoot(string? path) => path is { Length: >= 3 } &&
        char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/'
        ? char.ToUpperInvariant(path[0]) + ":\\" : null;

    public void Dispose()
    {
        _disposed = true;
        ConfigManager.SettingsSaved -= SettingsSaved;
        _timer?.Dispose();
        // A running worker may still release the semaphore; no synchronous UI wait is required.
    }
}

internal static class FrozenDoctorReader
{
    internal sealed record Result(IReadOnlyList<FrozenDoctorDecision> Decisions, bool Partial, bool Bounded = false);
    internal static bool Affects(string freezePath, string target, AppConfig config)
    {
        var frozen = ChangeApplier.CanonicalizeTarget(freezePath, config);
        target = ChangeApplier.CanonicalizeTarget(target, config);
        return JsonPointer.IsAncestorOrSelf(frozen, target) || JsonPointer.IsAncestorOrSelf(target, frozen);
    }
    internal static Result Read(string path, IReadOnlyList<FreezeEntry> freezes, AppConfig config)
    {
        if (!File.Exists(path) || freezes.Count == 0) return new([], false);
        try
        {
            var tail = DiagnosticAutomation.ReadTail(path, 1000, 256 * 1024);
            var parsed = Parse(tail.Text, freezes, config);
            return parsed with { Bounded = tail.Truncated };
        }
        catch { return new([], true); }
    }

    internal static Result Parse(string text, IReadOnlyList<FreezeEntry> freezes, AppConfig config)
    {
        var rows = new List<FrozenDoctorDecision>();
        var targets = new HashSet<string>(StringComparer.Ordinal);
        var partial = false;
        foreach (var line in text.Split('\n').TakeLast(1000).Reverse())
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 32 });
                var row = doc.RootElement;
                if (row.ValueKind != JsonValueKind.Object) { partial = true; continue; }
                string String(string name) => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                    ? (value.GetString() is { Length: <= 1024 } s ? s : "") : "";
                bool Flag(string name) => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
                var target = ChangeApplier.CanonicalizeTarget(String("Target"), config);
                if (string.IsNullOrEmpty(target) || !targets.Add(target)) continue;
                if (Flag("Undone") || Flag("Applied") && !Flag("DryRun") || String("RejectionReason") != "frozen") continue;
                var freeze = freezes.FirstOrDefault(f => Affects(f.Path, target, config));
                if (freeze == null) continue;
                var id = String("Id");
                rows.Add(new(string.IsNullOrWhiteSpace(id) ? target + String("Ts") : id, target, String("Ts"), freeze.Note ?? ""));
                if (rows.Count == 64) { partial = true; break; }
            }
            catch (JsonException) { partial = true; }
        }
        return new(rows, partial);
    }
}
