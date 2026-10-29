using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using GlDrive.Util;

namespace GlDrive.Services.Automation;

public sealed record AutomationLedgerEntry(string EventId, DateTimeOffset Time, string CallId, string ClientId,
    string Tool, JsonElement Arguments, string Outcome, long DurationMilliseconds, string? JobId);

internal sealed record AutomationLedgerLimits(int QueueCapacity = 256, int MaxRowBytes = 4096,
    int SegmentBytes = 1024 * 1024, int SegmentCount = 4, int MinimumCapacity = 100);

/// <summary>Passive, append-only call events. Capacity evicts old evidence, never denies an operation.</summary>
public sealed class AutomationCallLedger : IDisposable, IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly HashSet<string> Outcomes = ["accepted", "completed", "failed", "denied", "cancelled"];
    private static readonly HashSet<string> Markers = ["[string]", "[number]", "[boolean]", "[null]", "[omitted]", "[not supplied]"];
    private readonly string _directory;
    private readonly Func<bool> _enabled;
    private readonly Func<int> _capacity;
    private readonly Func<string, bool> _knownTool;
    private readonly Action<string>? _warning;
    private readonly Action? _beforeWrite;
    private readonly AutomationLedgerLimits _limits;
    private readonly Channel<AutomationLedgerEntry> _queue;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly object _memoryLock = new(), _flushLock = new();
    private readonly LinkedList<Stored> _memory = [];
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _worker;
    private TaskCompletionSource? _flush;
    private long _flushVersion, _sequence, _persistedSequence, _segmentIndex, _segmentBytes, _dropped;
    private int _disposed, _warned;
    private volatile bool _persistenceHealthy = true;
    private sealed record Stored(long Sequence, AutomationLedgerEntry Entry, string Line);

    public AutomationCallLedger(string appDataRoot, Func<bool> enabled, Func<int> capacity, Func<string, bool> knownTool)
        : this(appDataRoot, enabled, capacity, knownTool, new()) { }

    internal AutomationCallLedger(string appDataRoot, Func<bool> enabled, Func<int> capacity, Func<string, bool> knownTool,
        AutomationLedgerLimits limits, Action<string>? warning = null, Action? beforeWrite = null)
    {
        if (limits.QueueCapacity is < 1 or > 1024 || limits.MaxRowBytes is < 512 or > 8192 ||
            limits.SegmentBytes < limits.MaxRowBytes + 1 || limits.SegmentBytes > 4 * 1024 * 1024 ||
            limits.SegmentCount is < 1 or > 8 || limits.MinimumCapacity is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(limits));
        _directory = Path.Combine(Path.GetFullPath(appDataRoot), "automation-ledger");
        _enabled = enabled; _capacity = capacity; _knownTool = knownTool; _limits = limits;
        _warning = warning; _beforeWrite = beforeWrite;
        _queue = Channel.CreateBounded<AutomationLedgerEntry>(new BoundedChannelOptions(limits.QueueCapacity)
        { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest, AllowSynchronousContinuations = false },
            _ => Interlocked.Increment(ref _dropped));
        using var flow = ExecutionContext.IsFlowSuppressed() ? default : ExecutionContext.SuppressFlow();
        _worker = Task.Run(RunAsync);
    }

    public Task Ready => _ready.Task;
    public Task Completion => _worker;
    public bool PersistenceHealthy => _persistenceHealthy;
    public long Dropped => Interlocked.Read(ref _dropped);

    public void Record(AutomationCallAudit audit)
    {
        // No raw argument graph, label, result body or exception enters the worker queue.
        try
        {
            if (Volatile.Read(ref _disposed) != 0 || !_enabled()) return;
            var entry = new AutomationLedgerEntry(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
                SafeId(audit.CallId), ClientIdentity(audit.ClientId), SafeTool(audit.Tool), ArgumentShape(audit.Arguments),
                Outcomes.Contains(audit.Outcome) ? audit.Outcome : "failed", Math.Clamp(audit.DurationMilliseconds, 0, 86_400_000),
                audit.JobId == null ? null : SafeId(audit.JobId));
            if (_queue.Writer.TryWrite(entry)) Wake();
        }
        catch { WarnOnce(); }
    }

    public IReadOnlyList<AutomationLedgerEntry> Snapshot(int limit = 200)
    {
        limit = Math.Clamp(limit, 1, 500);
        lock (_memoryLock) return _memory.Reverse().Take(limit).Select(row => row.Entry).ToArray();
    }

    public Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0) return _worker.WaitAsync(cancellationToken);
        Task task;
        lock (_flushLock)
        {
            if (Volatile.Read(ref _disposed) != 0) return _worker.WaitAsync(cancellationToken);
            if (_flush == null || _flush.Task.IsCompleted) _flush = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _flushVersion++;
            task = _flush.Task;
        }
        Wake();
        return task.WaitAsync(cancellationToken);
    }

    private async Task RunAsync()
    {
        try { Load(); } catch { WarnOnce(); }
        _ready.TrySetResult();
        try
        {
            while (true)
            {
                await _wake.WaitAsync().ConfigureAwait(false);
                while (true)
                {
                    long version;
                    lock (_flushLock) version = _flushVersion;
                    for (var count = 0; count < _limits.QueueCapacity && _queue.Reader.TryRead(out var entry); count++) Add(entry);
                    try { PersistPending(); } catch { WarnOnce(); }
                    lock (_flushLock) if (version == _flushVersion) _flush?.TrySetResult();
                    if (Volatile.Read(ref _disposed) != 0 && !_queue.Reader.TryPeek(out _)) return;
                    if (_queue.Reader.TryPeek(out _)) continue;
                    lock (_flushLock) if (version != _flushVersion) continue;
                    break;
                }
            }
        }
        finally { lock (_flushLock) _flush?.TrySetResult(); }
    }

    private void Add(AutomationLedgerEntry entry)
    {
        var line = JsonSerializer.Serialize(entry, Json) + "\n";
        if (Utf8.GetByteCount(line) > _limits.MaxRowBytes)
        {
            entry = entry with { Arguments = JsonSerializer.SerializeToElement("[omitted]") };
            line = JsonSerializer.Serialize(entry, Json) + "\n";
        }
        var capacity = 2000;
        try { capacity = Math.Clamp(_capacity(), _limits.MinimumCapacity, 10_000); } catch { }
        lock (_memoryLock)
        {
            _memory.AddLast(new Stored(++_sequence, entry, line));
            while (_memory.Count > capacity) _memory.RemoveFirst();
        }
    }

    private void Load()
    {
        if (!Directory.Exists(_directory)) return;
        RejectLink(_directory);
        var files = LatestSegments();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            _segmentIndex = Math.Max(_segmentIndex, file.Index);
            var damaged = false;
            try
            {
                RejectLink(file.Path);
                var bytes = ReadBounded(file.Path, _limits.SegmentBytes);
                using var reader = new StringReader(Utf8.GetString(bytes));
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        if (Utf8.GetByteCount(line) > _limits.MaxRowBytes) throw new InvalidDataException();
                        var entry = JsonSerializer.Deserialize<AutomationLedgerEntry>(line, Json) ?? throw new JsonException();
                        if (!Guid.TryParseExact(entry.EventId, "N", out _) || !Outcomes.Contains(entry.Outcome)) throw new JsonException();
                        var budget = 128;
                        entry = entry with
                        {
                            CallId = SafeId(entry.CallId), ClientId = LoadedClientIdentity(entry.ClientId), Tool = SafeTool(entry.Tool),
                            Arguments = ValidShape(entry.Arguments, 0, ref budget) ? entry.Arguments.Clone() : JsonSerializer.SerializeToElement("[omitted]"),
                            DurationMilliseconds = Math.Clamp(entry.DurationMilliseconds, 0, 86_400_000),
                            JobId = entry.JobId == null ? null : SafeId(entry.JobId)
                        };
                        if (seen.Add(entry.EventId)) Add(entry);
                    }
                    catch { damaged = true; }
                }
            }
            catch { damaged = true; }
            if (damaged) Quarantine(file.Path);
        }
        _persistedSequence = _sequence;
        // A new segment avoids appending to an incomplete final line after a crash.
        _segmentIndex++;
        _segmentBytes = 0;
    }

    private void PersistPending()
    {
        Stored[] pending;
        lock (_memoryLock) pending = _memory.Where(row => row.Sequence > _persistedSequence).ToArray();
        if (pending.Length == 0) return;
        Directory.CreateDirectory(_directory);
        RejectLink(_directory);
        _beforeWrite?.Invoke();
        foreach (var row in pending)
        {
            var bytes = Utf8.GetBytes("\n" + row.Line);
            if (_segmentIndex == 0) _segmentIndex = 1;
            var existingPath = SegmentPath(_segmentIndex);
            if (File.Exists(existingPath)) { RejectLink(existingPath); _segmentBytes = new FileInfo(existingPath).Length; }
            if (_segmentBytes + bytes.Length > _limits.SegmentBytes) { _segmentIndex++; _segmentBytes = 0; }
            var path = SegmentPath(_segmentIndex);
            if (File.Exists(path)) RejectLink(path);
            using (var stream = RestrictedStream(path))
            {
                stream.Seek(0, SeekOrigin.End);
                stream.Write(bytes);
                stream.Flush(true);
                _segmentBytes = stream.Length;
            }
            _persistedSequence = row.Sequence;
            EvictOldSegments();
        }
        _persistenceHealthy = true;
    }

    private void EvictOldSegments()
    {
        var oldestRetained = LatestSegments()[0].Index;
        foreach (var file in Segments().Where(file => file.Index < oldestRetained))
        {
            RejectLink(file.Path);
            File.Delete(file.Path);
            var corrupt = file.Path + ".corrupt";
            if (File.Exists(corrupt)) { RejectLink(corrupt); File.Delete(corrupt); }
        }
    }

    private (string Path, long Index)[] LatestSegments()
    {
        var latest = new PriorityQueue<(string Path, long Index), long>();
        foreach (var file in Segments())
        {
            latest.Enqueue(file, file.Index);
            if (latest.Count > _limits.SegmentCount) latest.Dequeue();
        }
        return latest.UnorderedItems.Select(item => item.Element).OrderBy(file => file.Index).ToArray();
    }

    private IEnumerable<(string Path, long Index)> Segments()
    {
        foreach (var path in Directory.EnumerateFiles(_directory, "ledger-*.jsonl", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (name.Length == 27 && long.TryParse(name.AsSpan(7), out var number) && number > 0 && number < long.MaxValue)
                yield return (path, number);
        }
    }

    private string SegmentPath(long index) => Path.Combine(_directory, "ledger-" + index.ToString("D20") + ".jsonl");
    private void Quarantine(string path)
    {
        try
        {
            var copy = path + ".corrupt";
            if (File.Exists(copy)) RejectLink(copy);
            SecureFile.WriteAllBytesRestricted(copy, ReadBounded(path, _limits.SegmentBytes));
        }
        catch { }
        WarnOnce();
    }

    private static byte[] ReadBounded(string path, int maximum)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > maximum) throw new InvalidDataException();
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var count = stream.Read(buffer, 0, Math.Min(buffer.Length, Math.Max(1, maximum - (int)output.Length + 1)));
            if (count == 0) return output.ToArray();
            if (output.Length + count > maximum) throw new InvalidDataException();
            output.Write(buffer, 0, count);
        }
    }

    private static FileStream RestrictedStream(string path)
    {
        var acl = new FileSecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
        return FileSystemAclExtensions.Create(new FileInfo(path), FileMode.OpenOrCreate,
            FileSystemRights.ReadData | FileSystemRights.WriteData, FileShare.Read, 4096, FileOptions.None, acl);
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked ledger paths are unsupported");
    }

    private string SafeTool(string? tool)
    {
        try { if (tool is { Length: > 0 and <= 128 } && _knownTool(tool)) return tool; } catch { }
        return "unregistered";
    }
    private static string SafeId(string? id) => Guid.TryParse(id, out var guid) ? guid.ToString("N") : Hash(id ?? "");
    public static string ClientIdentity(string? id) => "client:" + Hash(id ?? "");
    private static string LoadedClientIdentity(string? id) => id is { Length: 39 } && id.StartsWith("client:", StringComparison.Ordinal) &&
        id.AsSpan(7).ToArray().All(char.IsAsciiHexDigit) ? id : ClientIdentity(id);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(value))).ToLowerInvariant()[..32];

    internal static JsonElement ArgumentShape(JsonElement arguments)
    {
        var remaining = 64;
        return JsonSerializer.SerializeToElement(Shape(arguments, 0, ref remaining));
    }
    private static object Shape(JsonElement value, int depth, ref int remaining)
    {
        if (--remaining < 0 || depth > 4) return "[omitted]";
        if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            var children = new List<object>();
            if (value.ValueKind == JsonValueKind.Object)
                foreach (var field in value.EnumerateObject())
                {
                    if (children.Count == 16 || remaining <= 0) { children.Add("[omitted]"); break; }
                    children.Add(Shape(field.Value, depth + 1, ref remaining));
                }
            else foreach (var item in value.EnumerateArray())
                {
                    if (children.Count == 16 || remaining <= 0) { children.Add("[omitted]"); break; }
                    children.Add(Shape(item, depth + 1, ref remaining));
                }
            return new Dictionary<string, object> { [value.ValueKind == JsonValueKind.Object ? "object" : "array"] = children };
        }
        return value.ValueKind switch
        {
            JsonValueKind.String => "[string]", JsonValueKind.Number => "[number]",
            JsonValueKind.True or JsonValueKind.False => "[boolean]", JsonValueKind.Null => "[null]", _ => "[not supplied]"
        };
    }
    private static bool ValidShape(JsonElement shape, int depth, ref int remaining)
    {
        if (--remaining < 0 || depth > 12) return false;
        if (shape.ValueKind == JsonValueKind.String) return Markers.Contains(shape.GetString() ?? "");
        if (shape.ValueKind == JsonValueKind.Array)
        {
            var count = 0;
            foreach (var item in shape.EnumerateArray()) if (++count > 17 || !ValidShape(item, depth + 1, ref remaining)) return false;
            return true;
        }
        if (shape.ValueKind != JsonValueKind.Object) return false;
        var fields = shape.EnumerateObject().ToArray();
        return fields.Length == 1 && fields[0].Name is "object" or "array" && fields[0].Value.ValueKind == JsonValueKind.Array &&
            ValidShape(fields[0].Value, depth + 1, ref remaining);
    }

    private void WarnOnce()
    {
        _persistenceHealthy = false;
        if (Interlocked.Exchange(ref _warned, 1) != 0) return;
        const string message = "Automation ledger storage is unavailable; recent calls remain in bounded memory.";
        try { if (_warning != null) _warning(message); else Serilog.Log.Warning(message); } catch { }
    }
    private void Wake() { try { _wake.Release(); } catch (SemaphoreFullException) { } }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.Writer.TryComplete(); Wake();
    }
    public async ValueTask DisposeAsync() { Dispose(); await _worker.ConfigureAwait(false); }
}
