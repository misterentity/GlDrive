using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GlDrive.Config;
using GlDrive.Util;
using Serilog;

namespace GlDrive.Irc;

public class PmHistoryEntry
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string Nick { get; set; } = "";
    public string Text { get; set; } = "";
    public IrcMessageType Type { get; set; }
    public bool WasEncrypted { get; set; }
}

/// <summary>
/// Persistent per-server private-message history at
/// %AppData%\GlDrive\pm-history-{serverId}.json, DPAPI-encrypted (CurrentUser)
/// like the FiSH key store — PM plaintext (often FiSH-decrypted) must not sit
/// on disk unprotected the way channel announce logs do.
///
/// Keyed by peer nick. Saves are debounced (chat arrives in bursts); the owner
/// (IrcService) must call Flush()/Dispose() on teardown so the tail isn't lost.
/// </summary>
public class PmHistoryStore : IDisposable
{
    private const int MaxMessagesPerTarget = 200;
    private const int MaxTargets = 50;
    private const int SaveDebounceMs = 2000;

    private readonly string _filePath;
    private readonly object _lock = new();
    private Dictionary<string, List<PmHistoryEntry>> _history = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _debounceTimer;
    private volatile bool _savePending;
    private bool _disposed;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public PmHistoryStore(string serverId)
        : this(Path.Combine(ConfigManager.AppDataPath, $"pm-history-{serverId}.json"), loadNow: true) { }

    // Test seam: explicit path.
    public PmHistoryStore(string filePath, bool loadNow)
    {
        _filePath = filePath;
        _debounceTimer = new Timer(_ => FlushSave(), null, Timeout.Infinite, Timeout.Infinite);
        if (loadNow) Load();
    }

    public void Append(string target, IrcMessageItem item)
    {
        if (string.IsNullOrEmpty(target)) return;
        lock (_lock)
        {
            if (!_history.TryGetValue(target, out var list))
            {
                if (_history.Count >= MaxTargets)
                    EvictStalestTarget();
                _history[target] = list = [];
            }
            list.Add(new PmHistoryEntry
            {
                Timestamp = item.Timestamp,
                Nick = item.Nick,
                Text = item.Text,
                Type = item.Type,
                WasEncrypted = item.WasEncrypted
            });
            if (list.Count > MaxMessagesPerTarget)
                list.RemoveAt(0);
        }
        ScheduleSave();
    }

    public void RemoveTarget(string target)
    {
        lock (_lock)
        {
            if (!_history.Remove(target)) return;
        }
        ScheduleSave();
    }

    /// <summary>Snapshot of all conversations (targets → chronological entries).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<PmHistoryEntry>> GetAll()
    {
        lock (_lock)
        {
            var snapshot = new Dictionary<string, IReadOnlyList<PmHistoryEntry>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (target, list) in _history)
                snapshot[target] = list.ToList();
            return snapshot;
        }
    }

    // Caller must hold _lock.
    private void EvictStalestTarget()
    {
        string? stalest = null;
        var oldest = DateTime.MaxValue;
        foreach (var (target, list) in _history)
        {
            var last = list.Count > 0 ? list[^1].Timestamp : DateTime.MinValue;
            if (last < oldest)
            {
                oldest = last;
                stalest = target;
            }
        }
        if (stalest != null) _history.Remove(stalest);
    }

    private void ScheduleSave()
    {
        // A message appended during/after teardown: the debounce timer is gone, so
        // persist synchronously rather than dropping the entry (FlushSave is lock-safe).
        if (_disposed) { FlushSave(); return; }
        _savePending = true;
        try { _debounceTimer.Change(SaveDebounceMs, Timeout.Infinite); }
        catch (ObjectDisposedException) { FlushSave(); }
    }

    public void Flush() => FlushSave();

    // Fully serialized: two flushers (debounce timer + Dispose) must not both write the
    // shared .tmp path concurrently, and the serialize must see every committed Append.
    private void FlushSave()
    {
        lock (_lock)
        {
            _savePending = false;
            try
            {
                var json = JsonSerializer.Serialize(_history, JsonOptions);
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                var encrypted = ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser);
                var tempPath = _filePath + ".tmp";
                File.WriteAllBytes(tempPath, encrypted);
                File.Move(tempPath, _filePath, overwrite: true);
                SecureFile.RestrictFilePermissions(_filePath);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to save PM history to {Path}", _filePath);
            }
        }
    }

    private void Load()
    {
        if (!File.Exists(_filePath)) return;
        try
        {
            var fileBytes = File.ReadAllBytes(_filePath);
            var decrypted = ProtectedData.Unprotect(fileBytes, null, DataProtectionScope.CurrentUser);
            var json = Encoding.UTF8.GetString(decrypted);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, List<PmHistoryEntry>>>(json, JsonOptions);
            lock (_lock)
            {
                _history = loaded != null
                    ? new Dictionary<string, List<PmHistoryEntry>>(loaded, StringComparer.OrdinalIgnoreCase)
                    : new(StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex)
        {
            // Keep the unreadable file for forensics, then start empty — history is
            // expendable and must never block IRC startup.
            Log.Warning(ex, "Failed to load PM history from {Path}, starting empty", _filePath);
            try { File.Copy(_filePath, _filePath + ".corrupt", overwrite: true); } catch { }
            lock (_lock)
                _history = new(StringComparer.OrdinalIgnoreCase);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Wait for any in-flight debounce callback to finish so it can't run concurrently
        // with — or after — the final flush, then flush unconditionally to capture the tail.
        try
        {
            using var done = new ManualResetEvent(false);
            if (_debounceTimer.Dispose(done))
                done.WaitOne(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) { Log.Debug(ex, "PM history timer dispose"); }
        FlushSave();
    }
}
