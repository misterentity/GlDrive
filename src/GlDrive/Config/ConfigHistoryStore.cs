using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GlDrive.AiAgent;
using GlDrive.Util;
using Serilog;

namespace GlDrive.Config;

public sealed class ConfigHistoryConfig
{
    // Opt-in until production save-rate evidence is available for the retention default.
    public bool Enabled { get; set; }
    public int RetentionCount { get; set; } = 512;
}

public sealed record ConfigHistoryDiff(string Pointer, string? Before, string? After);
public sealed record ConfigHistoryVersion(long Number, DateTime AtUtc, string Actor, string? AuditRef,
    List<ConfigHistoryDiff> Diff, string? ProtectedSnapshot);

/// <summary>Bookkeeping is best effort and never owns permission to save settings.</summary>
public sealed class ConfigHistoryStore
{
    private sealed record State(long NextVersion, List<ConfigHistoryVersion> Versions);
    private readonly string _path;
    private readonly Func<string, string> _protect;
    private readonly Func<string, string> _unprotect;
    private readonly Action<string, string> _write;
    private readonly object _gate = new();
    private readonly List<ConfigHistoryVersion> _versions = [];
    private long _next = 1;
    private bool _warned;

    public ConfigHistoryStore(string root) : this(root, Protect, Unprotect, SecureFile.WriteAllTextRestricted) { }
    internal ConfigHistoryStore(string root, Func<string, string> protect, Func<string, string> unprotect,
        Action<string, string> write)
    {
        _path = Path.Combine(root, "config-history.json");
        _protect = protect;
        _unprotect = unprotect;
        _write = write;
        try
        {
            if (!File.Exists(_path)) return;
            var state = JsonSerializer.Deserialize<State>(File.ReadAllText(_path)) ?? throw new IOException("Empty history");
            if (state.Versions == null || state.Versions.Any(v => v == null || v.Number < 1 || v.Diff == null) ||
                state.Versions.Select(v => v.Number).Distinct().Count() != state.Versions.Count)
                throw new IOException("Invalid history");
            _versions.AddRange(state.Versions.OrderBy(v => v.Number));
            _next = Math.Max(state.NextVersion, _versions.Select(v => v.Number).DefaultIfEmpty(0).Max() + 1);
        }
        catch
        {
            try { File.Copy(_path, _path + ".corrupt", true); } catch { }
            _versions.Clear();
            _next = 1;
            Warn();
        }
    }

    public IReadOnlyList<ConfigHistoryVersion> Read()
    {
        lock (_gate) return _versions.Select(v => v with { Diff = [.. v.Diff], ProtectedSnapshot = null }).ToArray();
    }

    public void Record(string? before, string after, ConfigWriteContext context, int capacity)
    {
        lock (_gate)
        {
            try
            {
                JsonNode? prior = null;
                try { prior = before == null ? null : JsonNode.Parse(before); } catch { }
                var current = JsonNode.Parse(after) ?? throw new IOException("Empty config");
                var diff = ConfigDiff.Diff(prior, current).Select(d => new ConfigHistoryDiff(d.pointer,
                    ConfigSecretPointers.MaskValue(d.pointer, d.before), ConfigSecretPointers.MaskValue(d.pointer, d.after))).ToList();
                if (diff.Count == 0) return;
                var secrets = OutboundSecretInventory.ConfigSecrets(current)
                    .Concat(prior == null ? [] : OutboundSecretInventory.ConfigSecrets(prior));
                diff = JsonSerializer.Deserialize<List<ConfigHistoryDiff>>(
                    OutboundPayloadRedactor.Prepare(JsonSerializer.Serialize(diff), secrets))!;
                string? snapshot = null;
                try { snapshot = _protect(after); } catch { Warn(); }
                _versions.Add(new(_next++, DateTime.UtcNow, context.Actor, context.AuditRef, diff, snapshot));
                while (_versions.Count > Math.Clamp(capacity, 1, 10000)) _versions.RemoveAt(0);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                    _write(_path, JsonSerializer.Serialize(new State(_next, _versions)));
                }
                catch { Warn(); }
            }
            catch { Warn(); }
        }
    }

    public JsonNode RestoreSnapshot(long version)
    {
        lock (_gate)
        {
            var row = _versions.SingleOrDefault(v => v.Number == version) ?? throw new ArgumentException("Unknown or expired configuration version");
            if (row.ProtectedSnapshot == null) throw new InvalidOperationException("This version has no restorable snapshot");
            return JsonNode.Parse(_unprotect(row.ProtectedSnapshot)) ?? throw new InvalidOperationException("Empty snapshot");
        }
    }

    private void Warn()
    {
        if (_warned) return;
        _warned = true;
        Log.Warning("Configuration history could not be read or persisted; tracking continues in memory");
    }

    internal static string Protect(string value) => Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
    internal static string Unprotect(string value) => Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser));
}
