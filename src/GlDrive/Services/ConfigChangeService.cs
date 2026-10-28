using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using GlDrive.AiAgent;
using GlDrive.Config;
using GlDrive.Services.Automation;
using GlDrive.Util;
using Serilog;

namespace GlDrive.Services;

public sealed record ConfigLease(string Id, string Label, string Pointer, string? ServerId, string Before,
    string Leased, DateTime ExpiresUtc, string Actor, bool ApplyWhenIdle, string Status = "pending");
public sealed record ConfigExperiment(string Id, string Label, string Hypothesis, DateTime StartedUtc,
    string Snapshot, ConfigExperimentMetrics Before, ConfigExperimentMetrics? After = null, string Status = "active");
public sealed record ConfigExperimentMetrics(int Races, int Completed, int RacesWithFiles, bool CompleteEvidence);

/// <summary>Explicit user experiments and leases share the validated settings transaction.</summary>
public sealed class ConfigChangeService
{
    private sealed record State(List<ConfigLease> Leases, List<ConfigExperiment> Experiments);
    private readonly AppConfig _config;
    private readonly ConfigurationAutomation _configuration;
    private readonly string _path;
    private readonly string _configPath;
    private readonly Func<string?, bool> _idle;
    private readonly Func<string, string> _protect;
    private readonly Func<string, string> _unprotect;
    private readonly Action<string, string> _write;
    private readonly Func<DateTime, ConfigExperimentMetrics> _metrics;
    private readonly List<ConfigLease> _leases = [];
    private readonly List<ConfigExperiment> _experiments = [];
    private bool _warned;
    public bool PersistenceHealthy { get; private set; } = true;

    public ConfigChangeService(AppConfig config, ConfigurationAutomation configuration, Func<string?, bool> idle,
        string? root = null) : this(config, configuration, idle, root ?? ConfigManager.AppDataPath,
            ConfigManager.ConfigPath, ConfigHistoryStore.Protect, ConfigHistoryStore.Unprotect,
            SecureFile.WriteAllTextRestricted, null) { }

    internal ConfigChangeService(AppConfig config, ConfigurationAutomation configuration, Func<string?, bool> idle,
        string root, string configPath, Func<string, string> protect, Func<string, string> unprotect,
        Action<string, string> write, Func<DateTime, ConfigExperimentMetrics>? metrics)
    {
        _config = config;
        _configuration = configuration;
        _idle = idle;
        _path = Path.Combine(root, "config-changes.json");
        _configPath = configPath;
        _protect = protect;
        _unprotect = unprotect;
        _write = write;
        _metrics = metrics ?? (now => ReadMetrics(Path.Combine(root, "ai-data"), now));
        try
        {
            if (!File.Exists(_path)) return;
            var state = JsonSerializer.Deserialize<State>(_unprotect(File.ReadAllText(_path))) ?? throw new IOException("Empty change store");
            if (state.Leases == null || state.Experiments == null || state.Leases.Any(l => l == null) || state.Experiments.Any(e => e == null))
                throw new IOException("Invalid change store");
            _leases.AddRange(state.Leases);
            _experiments.AddRange(state.Experiments);
        }
        catch
        {
            try { File.Copy(_path, _path + ".corrupt", true); } catch { }
            _leases.Clear();
            _experiments.Clear();
            Warn();
        }
    }

    public IReadOnlyList<ConfigLease> Leases()
    {
        ConfigLease[] rows = [];
        SecureFile.WithPathLock(_configPath, () => rows = _leases.Select(l => l with
        {
            Before = ConfigSecretPointers.MaskValue(l.Pointer, l.Before) ?? "null",
            Leased = ConfigSecretPointers.MaskValue(l.Pointer, l.Leased) ?? "null"
        }).ToArray());
        return rows;
    }

    public IReadOnlyList<ConfigExperiment> Experiments()
    {
        ConfigExperiment[] rows = [];
        SecureFile.WithPathLock(_configPath, () => rows = _experiments.Select(e => e with { Snapshot = "" }).ToArray());
        return rows;
    }

    public ConfigLease CreateLease(string pointer, JsonNode? value, DateTime expiresUtc, string label,
        bool applyWhenIdle = false, DateTime? nowOverride = null)
    {
        if (ConfigManager.ReadOnly) throw new InvalidOperationException("Settings are read-only");
        if (ConfigWriteScope.Current.Actor.StartsWith("agent:", StringComparison.Ordinal))
            throw new InvalidOperationException("The tuning agent cannot create leases");
        var now = nowOverride ?? DateTime.UtcNow;
        if (expiresUtc <= now) throw new ArgumentException("Lease expiry must be in the future");
        ConfigLease? row = null;
        SecureFile.WithPathLock(_configPath, () =>
        {
            var current = _configuration.CurrentSnapshot();
            var before = ValidateLeasePointer(current, pointer, out var serverId);
            if (value is JsonObject or JsonArray) throw new ArgumentException("Leases support scalar values only");
            if (JsonNode.DeepEquals(before, value)) throw new ArgumentException("The leased value already holds");
            if (_leases.Any(l => l.Status is "pending" or "active" && ResolveLeasePointer(l, current) == pointer))
                throw new ArgumentException("This setting already has an active or pending lease; keep or finish it first");
            _configuration.Preview(TestReplace(pointer, before, value));
            row = new(Guid.NewGuid().ToString("N"), label, pointer, serverId, before?.ToJsonString() ?? "null",
                value?.ToJsonString() ?? "null", expiresUtc, ConfigWriteScope.Current.Actor, applyWhenIdle);
            // Persist intent before applying. Failed bookkeeping retains intent in memory;
            // it never blocks an otherwise valid explicit settings edit.
            _leases.Add(row);
            Persist();
            try { TryAdvance(_leases.Count - 1, now); }
            catch
            {
                // A caller told that its apply failed must not get a delayed surprise.
                var failedIndex = _leases.FindIndex(l => l.Id == row.Id);
                if (failedIndex >= 0) Finish(failedIndex, "failed");
                throw;
            }
            row = _leases.Single(l => l.Id == row.Id);
        });
        return row!;
    }

    internal static JsonNode? ValidateLeasePointer(JsonNode root, string pointer, out string? serverId)
    {
        serverId = null;
        if (!pointer.StartsWith('/') || ConfigSecretPointers.IsSecret(pointer))
            throw new ArgumentException("A non-secret scalar pointer is required");
        if (pointer is "/library/enabled" or "/library/dryRun")
            throw new ArgumentException("Library enablement and dry-run require explicit settings edits and cannot be leased");
        var parts = JsonPointer.Split(pointer);
        JsonNode? node = root;
        for (var i = 0; i < parts.Length; i++)
        {
            if (node is JsonArray array)
            {
                if (i != 1 || parts[0] != "servers" || !int.TryParse(parts[i], out var index) || index < 0 || index >= array.Count)
                    throw new ArgumentException("Leases do not support indexed lists other than server identities");
                node = array[index];
                serverId = node?["id"]?.GetValue<string>() ?? throw new ArgumentException("Server identity is missing");
            }
            else if (node is JsonObject obj && obj.TryGetPropertyValue(parts[i], out var child)) node = child;
            else throw new ArgumentException("Unknown lease pointer");
            if (ConfigSecretPointers.IsSecret(parts[i])) throw new ArgumentException("Secrets cannot be leased");
        }
        if (node is JsonObject or JsonArray || pointer is "/servers" || parts.LastOrDefault() == "id")
            throw new ArgumentException("Leases support scalar settings, excluding identities");
        return node;
    }

    private static string? ResolveLeasePointer(ConfigLease lease, JsonNode current)
    {
        if (lease.ServerId == null) return lease.Pointer;
        var servers = current["servers"] as JsonArray;
        if (servers == null) return null;
        var matches = servers.Select((s, i) => (s, i)).Where(p => p.s?["id"]?.GetValue<string>() == lease.ServerId).ToArray();
        if (matches.Length != 1) return null;
        var tail = lease.Pointer.IndexOf('/', "/servers/".Length);
        return tail < 0 ? null : $"/servers/{matches[0].i}" + lease.Pointer[tail..];
    }

    public void Reconcile(DateTime? nowOverride = null)
    {
        SecureFile.WithPathLock(_configPath, () =>
        {
            for (var i = 0; i < _leases.Count; i++)
                if (_leases[i].Status is "pending" or "active")
                {
                    try { TryAdvance(i, nowOverride ?? DateTime.UtcNow); }
                    catch { Warn(); } // Retry later; a bookkeeping failure cannot hold runtime work.
                }
        });
    }

    private void TryAdvance(int index, DateTime now)
    {
        var lease = _leases[index];
        var current = _configuration.CurrentSnapshot();
        var pointer = ResolveLeasePointer(lease, current);
        JsonNode? actual = null;
        try { if (pointer != null) actual = ValidateLeasePointer(current, pointer, out _); }
        catch { pointer = null; }
        if (pointer == null) { Finish(index, "superseded"); return; }
        var before = JsonNode.Parse(lease.Before);
        var leased = JsonNode.Parse(lease.Leased);
        if (lease.Status == "pending")
        {
            // Equality alone cannot prove ownership: a user may have chosen this value
            // while an idle-deferred lease was waiting. Preserve that later edit.
            if (JsonNode.DeepEquals(actual, leased))
            {
                Finish(index, "superseded");
                return;
            }
            else
            {
                if (!JsonNode.DeepEquals(actual, before)) { Finish(index, "superseded"); return; }
                if (now >= lease.ExpiresUtc) { Finish(index, "expired-before-apply"); return; }
                if (lease.ApplyWhenIdle && !_idle(lease.ServerId)) return;
                using var attribution = ConfigWriteScope.Begin("lease:" + lease.Id);
                _configuration.Apply(TestReplace(pointer, before, leased));
                _leases[index] = lease = lease with { Status = "active" };
                Persist();
                actual = leased;
            }
        }
        if (!JsonNode.DeepEquals(actual, leased)) { Finish(index, "superseded"); return; }
        if (now < lease.ExpiresUtc) return;
        if (lease.ApplyWhenIdle && !_idle(lease.ServerId)) return;
        using (ConfigWriteScope.Begin("lease-revert:" + lease.Id))
            _configuration.Apply(TestReplace(pointer, leased, before));
        Finish(index, "reverted");
    }

    public void KeepLease(string id)
    {
        if (ConfigManager.ReadOnly) throw new InvalidOperationException("Settings are read-only");
        SecureFile.WithPathLock(_configPath, () =>
        {
            var index = _leases.FindIndex(l => l.Id == id);
            if (index < 0) throw new ArgumentException("Unknown lease");
            Finish(index, "kept");
        });
    }

    private void Finish(int index, string status)
    {
        _leases[index] = _leases[index] with { Status = status };
        Persist();
    }

    internal static JsonArray TestReplace(string pointer, JsonNode? expected, JsonNode? replacement) =>
    [new JsonObject { ["op"] = "test", ["path"] = pointer, ["value"] = expected?.DeepClone() },
     new JsonObject { ["op"] = "replace", ["path"] = pointer, ["value"] = replacement?.DeepClone() }];

    public object Restore(JsonNode snapshot, string actor, bool requireIdle = false)
    {
        object? result = null;
        SecureFile.WithPathLock(_configPath, () =>
        {
            if (requireIdle && !_idle(null)) throw new InvalidOperationException("Races are active or queued; retry restoration when idle");
            var candidate = snapshot.DeepClone();
            // Never revive a revoked control token. Other secrets are DPAPI protected in
            // version snapshots and are restored only by this explicit user action.
            candidate["controlApi"] ??= new JsonObject();
            candidate["controlApi"]!["token"] = _configuration.CurrentSnapshot()["controlApi"]?["token"]?.DeepClone();
            var typed = candidate.Deserialize<AppConfig>(ConfigurationAutomation.Json) ?? throw new ArgumentException("Invalid snapshot");
            ConfigurationAutomation.Validate(typed);
            var canonical = JsonSerializer.SerializeToNode(typed, ConfigurationAutomation.Json)!.AsObject();
            var operations = new JsonArray(canonical.Select(p => (JsonNode?)new JsonObject
                { ["op"] = "replace", ["path"] = "/" + p.Key, ["value"] = p.Value?.DeepClone() }).ToArray());
            using var attribution = ConfigWriteScope.Begin(actor);
            result = _configuration.Apply(operations, allowSecrets: true);
        });
        return result!;
    }

    public ConfigExperiment StartExperiment(string label, string hypothesis, DateTime? nowOverride = null)
    {
        if (ConfigManager.ReadOnly) throw new InvalidOperationException("Settings are read-only");
        var now = nowOverride ?? DateTime.UtcNow;
        var metrics = _metrics(now);
        ConfigExperiment? experiment = null;
        SecureFile.WithPathLock(_configPath, () =>
        {
            experiment = new(Guid.NewGuid().ToString("N"), label, hypothesis, now,
                _configuration.CurrentSnapshot().ToJsonString(), metrics);
            _experiments.Add(experiment);
            Persist();
        });
        return experiment! with { Snapshot = "" };
    }

    public ConfigExperiment EndExperiment(string id, bool revert, DateTime? nowOverride = null)
    {
        var metrics = _metrics(nowOverride ?? DateTime.UtcNow);
        ConfigExperiment? experiment = null;
        SecureFile.WithPathLock(_configPath, () =>
        {
            var index = _experiments.FindIndex(e => e.Id == id && e.Status == "active");
            if (index < 0) throw new ArgumentException("Unknown active experiment");
            experiment = _experiments[index];
            if (revert) Restore(JsonNode.Parse(experiment.Snapshot)!, "experiment-revert:" + id);
            _experiments[index] = experiment = experiment with { After = metrics, Status = revert ? "reverted" : "kept" };
            Persist();
        });
        return experiment! with { Snapshot = "" };
    }

    private void Persist()
    {
        // Active leases/experiments are explicit user commitments. Only completed
        // bookkeeping is evicted; no new edit is refused at a retention boundary.
        var capacity = Math.Clamp(_config.ConfigHistory.RetentionCount, 1, 10000);
        while (_leases.Count(l => l.Status is not ("pending" or "active")) > capacity)
            _leases.RemoveAt(_leases.FindIndex(l => l.Status is not ("pending" or "active")));
        while (_experiments.Count(e => e.Status != "active") > capacity)
            _experiments.RemoveAt(_experiments.FindIndex(e => e.Status != "active"));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            _write(_path, _protect(JsonSerializer.Serialize(new State(_leases, _experiments))));
            PersistenceHealthy = true;
        }
        catch { Warn(); }
    }

    private void Warn()
    {
        PersistenceHealthy = false;
        if (_warned) return;
        _warned = true;
        Log.Warning("Configuration leases/experiments could not be read or persisted; tracking continues in memory");
    }

    internal static ConfigExperimentMetrics ReadMetrics(string root, DateTime now)
    {
        try
        {
            var reader = new LogDigester(root);
            // Stream files use local dates. Widen the UTC calendar range to cover
            // every local offset, then select the precise UTC interval below.
            var rows = reader.ReadStream<RaceOutcomeEvent>("races", now.AddHours(-24).Date.AddDays(-1), now.Date.AddDays(1))
                .Where(r => DateTime.TryParse(r.Ts, out var at) && at.ToUniversalTime() >= now.AddHours(-24) && at.ToUniversalTime() <= now).ToArray();
            return new(rows.Length, rows.Count(r => r.Result == "complete"),
                rows.Count(r => r.Participants.Any(p => p.Role == "dst" && p.Files > 0)), rows.Length > 0 && reader.ParseSkips == 0);
        }
        catch { return new(0, 0, 0, false); }
    }
}
