using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Serilog;
using GlDrive.Util;

namespace GlDrive.Irc;

public sealed record IrcUnreadEntry(string Target, int Messages, int Highlights, DateTime LastUtc,
    string Generation = "", long Received = 0, long ReceivedHighlights = 0);
public sealed record IrcUnreadMarker(string Generation, long Received, long ReceivedHighlights);

/// <summary>Passive bookkeeping. Its encrypted file never contains message text.</summary>
public sealed class IrcUnreadStore : IDisposable
{
    private readonly object _gate = new();
    private readonly object _saveGate = new();
    private readonly string _path;
    private readonly int _capacity;
    private readonly Action<string, byte[]> _write;
    private readonly Timer _timer;
    private readonly Dictionary<string, IrcUnreadEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private long _revision;
    private long _savedRevision;
    private bool _warned;
    private int _disposed;

    public IrcUnreadStore(string path, int capacity = 512, Action<string, byte[]>? write = null)
    {
        _path = path; _capacity = Math.Max(1, capacity);
        _write = write ?? ((file, bytes) =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file + ".tmp", bytes);
            SecureFile.RestrictFilePermissions(file + ".tmp");
            File.Move(file + ".tmp", file, true);
            SecureFile.RestrictFilePermissions(file);
        });
        try
        {
            if (File.Exists(path))
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > 4 * 1024 * 1024) throw new InvalidDataException();
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                var data = ProtectedData.Unprotect(memory.ToArray(), null, DataProtectionScope.CurrentUser);
                var rows = JsonSerializer.Deserialize<IrcUnreadEntry[]>(data) ?? throw new InvalidDataException();
                if (rows.Any(e => e == null || string.IsNullOrEmpty(e.Target) || e.Messages < 0 || e.Highlights < 0 || e.Highlights > e.Messages ||
                    e.Received < e.Messages || e.ReceivedHighlights < e.Highlights || e.ReceivedHighlights > e.Received || string.IsNullOrEmpty(e.Generation)))
                    throw new InvalidDataException();
                // A callback from a previous service instance cannot acknowledge this session.
                foreach (var row in rows.OrderBy(e => e.LastUtc).TakeLast(_capacity))
                    _entries[row.Target] = row with { Generation = Guid.NewGuid().ToString("N") };
            }
        }
        catch
        {
            try { File.Copy(path, path + ".corrupt", true); } catch { }
            _entries.Clear(); Warn();
        }
        using var flow = ExecutionContext.IsFlowSuppressed() ? default : ExecutionContext.SuppressFlow();
        _timer = new(_ => Save(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    public IReadOnlyList<IrcUnreadEntry> Snapshot() { lock (_gate) return _entries.Values.Where(e => e.Messages > 0).ToArray(); }
    public IrcUnreadEntry? Get(string target) { lock (_gate) return _entries.GetValueOrDefault(target); }
    public IrcUnreadMarker? Record(string target, bool highlight, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(target) || target == "*") return null;
        lock (_gate)
        {
            if (!_entries.TryGetValue(target, out var row))
            {
                if (_entries.Count >= _capacity) _entries.Remove(_entries.Values.MinBy(e => e.LastUtc)!.Target);
                row = new(target, 0, 0, nowUtc, Guid.NewGuid().ToString("N"));
            }
            row = row with { Messages = Saturate(row.Messages),
                Highlights = highlight ? Saturate(row.Highlights) : row.Highlights, LastUtc = nowUtc,
                Received = row.Received == long.MaxValue ? long.MaxValue : row.Received + 1,
                ReceivedHighlights = highlight && row.ReceivedHighlights < long.MaxValue ? row.ReceivedHighlights + 1 : row.ReceivedHighlights };
            _entries[target] = row;
            _revision++;
            return new(row.Generation, row.Received, row.ReceivedHighlights);
        }
    }
    public void ReadThrough(string target, IrcUnreadMarker marker)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(target, out var row) || row.Generation != marker.Generation ||
                marker.Received < 0 || marker.Received > row.Received || marker.ReceivedHighlights < 0 ||
                marker.ReceivedHighlights > marker.Received || marker.ReceivedHighlights > row.ReceivedHighlights) return;
            var messages = (int)Math.Min(row.Messages, row.Received - marker.Received);
            var highlights = (int)Math.Min(row.Highlights, row.ReceivedHighlights - marker.ReceivedHighlights);
            if (messages == row.Messages && highlights == row.Highlights) return;
            _entries[target] = row with { Messages = messages, Highlights = Math.Min(messages, highlights) };
            _revision++;
        }
    }
    public void MarkRead(string target)
    {
        lock (_gate) if (_entries.Remove(target)) _revision++;
    }
    private static int Saturate(int value) => value == int.MaxValue ? value : value + 1;
    public void Save()
    {
        lock (_saveGate)
        {
            IrcUnreadEntry[] rows; long revision;
            lock (_gate) { revision = _revision; if (revision == _savedRevision) return; rows = _entries.Values.ToArray(); }
            try
            {
                var bytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(rows), null, DataProtectionScope.CurrentUser);
                _write(_path, bytes);
                lock (_gate) _savedRevision = revision;
            }
            catch { Warn(); }
        }
    }
    private void Warn()
    {
        lock (_gate) { if (_warned) return; _warned = true; }
        Log.Warning("IRC unread persistence unavailable; tracking continues in memory");
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _timer.Dispose(); Save();
    }
}
