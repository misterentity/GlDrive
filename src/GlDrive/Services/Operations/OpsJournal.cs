using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using GlDrive.Services.Automation;

namespace GlDrive.Services.Operations;

/// <summary>Passive local facts. Producers never wait for storage, credentials, redaction or readers.</summary>
public sealed class OpsJournal : IDisposable, IAsyncDisposable
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly string _root, _path, _stage, _corrupt;
    private readonly Func<Func<string, string>> _redactor;
    private readonly Func<DateTimeOffset> _now;
    private readonly OpsJournalLimits _limits;
    private readonly Action<string>? _beforeWrite;
    private readonly Channel<OpsEvent> _queue;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly object _memoryGate = new(), _flushGate = new(), _coalesceGate = new();
    private readonly Dictionary<string, (string Value, DateTimeOffset Time)> _coalesced = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _entries = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _worker;
    private readonly Stopwatch _sincePersist = Stopwatch.StartNew();
    private TaskCompletionSource? _flush;
    private long _flushVersion, _sequence, _persistedSequence, _diskBytes, _dropped;
    private int _eventBytes, _disposed, _warned, _invalidRows;
    private bool _rewrite, _capacityCompaction, _dirty, _redactionDeferred;
    private volatile bool _persistenceHealthy = true;
    private volatile bool _retentionLimited;

    public Task Ready => _ready.Task;
    public Task Completion => _worker;
    public bool PersistenceHealthy => _persistenceHealthy;
    public long Dropped => Interlocked.Read(ref _dropped);

    public OpsJournal(string appDataRoot, Func<Func<string, string>> redactor)
        : this(appDataRoot, redactor, () => DateTimeOffset.UtcNow, new()) { }

    internal OpsJournal(string appDataRoot, Func<Func<string, string>> redactor, Func<DateTimeOffset> now,
        OpsJournalLimits limits, Action<string>? beforeWrite = null)
    {
        if (limits.MaxEvents is < 2 or > 10_000 || limits.MaxBytes is < 4096 or > 8 * 1024 * 1024 ||
            limits.QueueCapacity is < 1 or > 512 || limits.MaxEventBytes is < 512 or > 16 * 1024 || limits.MaxEventBytes > limits.MaxBytes)
            throw new ArgumentOutOfRangeException(nameof(limits));
        _root = Path.GetFullPath(appDataRoot);
        _path = Path.Combine(_root, "ops-journal.jsonl");
        _stage = _path + ".tmp";
        _corrupt = _path + ".corrupt";
        _redactor = redactor; _now = now; _limits = limits; _beforeWrite = beforeWrite;
        _queue = Channel.CreateBounded<OpsEvent>(new BoundedChannelOptions(limits.QueueCapacity)
        { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest, AllowSynchronousContinuations = false },
            _ => Interlocked.Increment(ref _dropped));
        using var flow = ExecutionContext.IsFlowSuppressed() ? default : ExecutionContext.SuppressFlow();
        _worker = Task.Run(RunAsync);
    }

    public bool TryRecord(OpsEvent fact)
    {
        if (Volatile.Read(ref _disposed) != 0 || fact == null) return false;
        if (fact.Kind is "auto-race-verdict" or "auto-race-site" or "auto-race-destination" or "race-admission" or "race-ended") return false;
        // Only bounded scalar facts enter the pending queue. Oversized fields are omitted whole.
        var queued = Bound(fact);
        if (!IsNewEpisode(queued)) return true;
        var accepted = _queue.Writer.TryWrite(queued);
        if (accepted) Wake();
        return accepted;
    }

    private bool IsNewEpisode(OpsEvent fact)
    {
        var transition = fact.Kind is "mount-state" or "irc-state" or "health-episode";
        if (!transition && fact.Kind is not ("login-throttle" or "authentication-hold" or "alert" or "update-deferred")) return true;
        // Keep only hashes of bounded scalar facts. State repeats remain coalesced
        // until the state changes; repeated warnings have a ten-minute quiet window.
        var key = Hash(fact.Kind + "\n" + fact.ServerId + (transition ? "" : "\n" + fact.Subject));
        var value = Hash(fact.Severity + "\n" + fact.Detail);
        lock (_coalesceGate)
        {
            if (_coalesced.TryGetValue(key, out var previous) && previous.Value == value &&
                (transition || fact.Time - previous.Time < TimeSpan.FromMinutes(10))) return false;
            if (_coalesced.Count >= 1024 && !_coalesced.ContainsKey(key))
                _coalesced.Remove(_coalesced.MinBy(pair => pair.Value.Time).Key);
            _coalesced[key] = (value, fact.Time);
        }
        return true;
    }

    public Task FlushAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _disposed) != 0) return _worker.WaitAsync(cancellationToken);
        Task task;
        lock (_flushGate)
        {
            if (_flush == null || _flush.Task.IsCompleted) _flush = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _flushVersion++;
            task = _flush.Task;
        }
        Wake();
        return task.WaitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<OpsEvent>> ReadTimelineAsync(string? serverId = null, int days = 7, int limit = 200,
        CancellationToken cancellationToken = default)
    {
        ValidateQuery(serverId, days, limit, 500);
        await Ready.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await Detached(() =>
        {
            return ReadEvents(ReadEntries(serverId, days).OrderByDescending(entry => entry.Event.Time), limit, cancellationToken);
        }, cancellationToken).ConfigureAwait(false);
    }

    // The dossier calls this on its worker. Hash identity remains exact even when redaction
    // replaces two distinct release names with the same display marker.
    internal IReadOnlyList<OpsEvent> SnapshotForSubject(string subject, int limit = 500)
    {
        if (string.IsNullOrEmpty(subject) || subject.Length > 1024 || limit is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(subject));
        var identity = SubjectIdentity(subject);
        Entry[] selected;
        lock (_memoryGate) selected = _entries.Where(entry => entry.SubjectScope == identity).ToArray();
        return ReadEvents(selected.OrderByDescending(entry => entry.Event.Time), limit, default);
    }

    private IReadOnlyList<OpsEvent> ReadEvents(IEnumerable<Entry> entries, int limit, CancellationToken cancellationToken)
    {
        var budget = new ReadBudget(cancellationToken);
        var redact = SafeRedactor();
        var rows = new List<OpsEvent>();
        var limits = RetentionLimits();
        try
        {
            foreach (var entry in entries)
            {
                budget.Check();
                if (rows.Count == limit) { limits.Add("row limit"); break; }
                rows.Add(Redact(entry.Event, redact, budget));
            }
        }
        catch (ReadLimitException) { limits.Add("read time limit"); }
        return new OpsReadResult<OpsEvent>(rows, limits);
    }

    public async Task<IReadOnlyList<OpsIncident>> ReadIncidentsAsync(string? serverId = null, int days = 7, int limit = 50,
        CancellationToken cancellationToken = default)
    {
        ValidateQuery(serverId, days, limit, 100);
        await Ready.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await Detached(() =>
        {
            var budget = new ReadBudget(cancellationToken);
            var groups = new List<List<Entry>>();
            var active = new Dictionary<string, List<Entry>>(StringComparer.Ordinal);
            var retained = ReadEntries(null, Math.Min(90, days + 1));
            var requested = ReadEntries(serverId, days);
            foreach (var entry in requested.OrderBy(entry => entry.Event.Time))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!active.TryGetValue(entry.Scope, out var group) || entry.Event.Time - group[^1].Event.Time > TimeSpan.FromMinutes(10))
                { active[entry.Scope] = group = []; groups.Add(group); }
                group.Add(entry);
            }
            var redact = SafeRedactor();
            var incidents = new List<OpsIncident>();
            var limits = RetentionLimits();
            try
            {
                foreach (var group in groups.Where(group => group.Any(entry => entry.Issue))
                    .OrderByDescending(group => group[^1].Event.Time))
                {
                    budget.Check();
                    if (incidents.Count == limit) { limits.Add("incident limit"); break; }
                    var lastIssue = group.FindLastIndex(entry => entry.Issue);
                    var context = group.Skip(Math.Max(0, lastIssue - 10)).Take(20).Select(entry => Redact(entry.Event, redact, budget)).ToArray();
                    var start = group.First(entry => entry.Issue).Event.Time;
                    var global = Hash("");
                    var preceding = retained.Where(entry => entry.Change && entry.Event.Time >= start.AddHours(-2) && entry.Event.Time < start &&
                        (entry.Scope == global || entry.Scope == group[0].Scope)).OrderByDescending(entry => entry.Event.Time).Take(10)
                        .Select(entry => Redact(entry.Event, redact, budget)).Reverse().ToArray();
                    incidents.Add(new OpsIncident(Hash(group[0].Scope + "\n" + group[0].Event.Time.ToString("O")), group[0].Event.Time,
                        group[^1].Event.Time, context[0].ServerId, group.Count, context) { PrecedingChanges = preceding });
                }
            }
            catch (ReadLimitException) { limits.Add("read time limit"); }
            return (IReadOnlyList<OpsIncident>)new OpsReadResult<OpsIncident>(incidents, limits);
        }, cancellationToken).ConfigureAwait(false);
    }

    private Entry[] ReadEntries(string? serverId, int days)
    {
        var now = _now(); var oldest = now.AddDays(-days);
        var scope = string.IsNullOrEmpty(serverId) ? null : Hash(serverId);
        lock (_memoryGate) return _entries.Where(entry => entry.Event.Time >= oldest && entry.Event.Time <= now &&
            (scope == null || entry.Scope == scope)).ToArray();
    }

    private async Task RunAsync()
    {
        try { Load(); } catch { WarnOnce(); }
        _ready.TrySetResult();
        try
        {
            while (true)
            {
                // Ordinary facts cause at most one durable batch a second. Explicit flush
                // and shutdown bypass this interval, and never depend on a UI dispatcher.
                bool flushRequested;
                lock (_flushGate) flushRequested = _flush is { Task.IsCompleted: false };
                var remainingBatchDelay = TimeSpan.FromSeconds(1) - _sincePersist.Elapsed;
                if (!flushRequested && Volatile.Read(ref _disposed) == 0 && remainingBatchDelay > TimeSpan.Zero)
                {
                    await _wake.WaitAsync(remainingBatchDelay).ConfigureAwait(false);
                    continue;
                }
                long flushVersion;
                lock (_flushGate) flushVersion = _flushVersion;
                try
                {
                    Func<string, string>? redact = null;
                    for (var count = 0; count < _limits.QueueCapacity && _queue.Reader.TryRead(out var fact); count++)
                    {
                        redact ??= SafeRedactor();
                        Add(Redact(fact, redact), Hash(fact.ServerId), SubjectIdentity(fact.Subject), IsIncident(fact), IsChange(fact));
                    }
                    TrimExpired();
                    Persist();
                }
                catch { _rewrite = true; _dirty = true; WarnOnce(); }
                _sincePersist.Restart();
                lock (_flushGate)
                    if (flushVersion == _flushVersion) _flush?.TrySetResult();
                if (Volatile.Read(ref _disposed) != 0 && !_queue.Reader.TryPeek(out _)) break;
                if (_queue.Reader.TryPeek(out _)) continue;
                lock (_flushGate) if (flushVersion != _flushVersion) continue;
                await _wake.WaitAsync(TimeSpan.FromMinutes(1)).ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_flushGate) _flush?.TrySetResult();
        }
    }

    private void Load()
    {
        var redact = AvailableRedactor();
        try
        {
            EnsurePaths();
            if (!File.Exists(_path)) return;
            var bytes = ReadTailBounded(_path, _limits.MaxBytes, out var limited);
            _retentionLimited |= limited;
            _diskBytes = new FileInfo(_path).Length;
            using var input = new StringReader(Utf8.GetString(bytes));
            string? line;
            while ((line = input.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (Utf8.GetByteCount(line) + 1 > _limits.MaxEventBytes)
                { _retentionLimited = true; continue; }
                try
                {
                    using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 16 });
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("event", out var item) || item.ValueKind != JsonValueKind.Object ||
                        !item.TryGetProperty("time", out var timestamp) || timestamp.ValueKind != JsonValueKind.String || !timestamp.TryGetDateTimeOffset(out var time))
                        throw new InvalidDataException("Journal event is unreadable");
                    string Text(string key) => item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
                    var fact = new OpsEvent(time, Text("kind"), Text("severity"), Text("serverId"), Text("subject"), Text("detail"), Text("actor"), Text("correlationId"));
                    var scope = root.TryGetProperty("scope", out var key) && key.ValueKind == JsonValueKind.String ? key.GetString() : null;
                    if (scope is not { Length: 64 } || scope.Any(c => !char.IsAsciiHexDigit(c))) scope = Hash(fact.ServerId);
                    var subject = root.TryGetProperty("subjectScope", out key) && key.ValueKind == JsonValueKind.String ? key.GetString() : null;
                    if (subject is not { Length: 64 } || subject.Any(c => !char.IsAsciiHexDigit(c))) subject = SubjectIdentity(fact.Subject);
                    bool Flag(string name, bool fallback) => root.TryGetProperty(name, out var flag) &&
                        flag.ValueKind is JsonValueKind.True or JsonValueKind.False ? flag.GetBoolean() : fallback;
                    // Stored rows were already redacted before their first write. Keep them
                    // intact internally if the current inventory is temporarily unavailable.
                    var storedFact = Bound(fact);
                    if (redact == null || !TryRedactStored(storedFact, redact, out storedFact)) _redactionDeferred = true;
                    Add(storedFact, scope, subject, Flag("issue", IsIncident(fact)), Flag("change", IsChange(fact)));
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException)
                { _invalidRows++; }
            }
            if (_invalidRows != 0) BackupCorrupt(redact ?? (_ => "[redaction unavailable]"));
            _redactionDeferred |= redact == null;
            // Rewrite once after startup to persist current redaction and discard expired entries.
            _rewrite = true; _dirty = true;
        }
        catch
        {
            BackupCorrupt(redact ?? (_ => "[redaction unavailable]"));
            lock (_memoryGate) { _entries.Clear(); _eventBytes = 0; }
            _rewrite = true; _dirty = true;
            WarnOnce();
        }
    }

    private void Add(OpsEvent fact, string scope, string subjectScope, bool issue, bool change)
    {
        var line = JsonSerializer.Serialize(new Stored(fact, scope, subjectScope, issue, change), Json) + "\n";
        if (Utf8.GetByteCount(line) > _limits.MaxEventBytes)
        {
            fact = fact with { Detail = "", Subject = "", Actor = "", CorrelationId = "", ServerId = "" };
            line = JsonSerializer.Serialize(new Stored(fact, scope, subjectScope, issue, change), Json) + "\n";
            if (Utf8.GetByteCount(line) > _limits.MaxEventBytes)
            { fact = fact with { Kind = "", Severity = "" }; line = JsonSerializer.Serialize(new Stored(fact, scope, subjectScope, issue, change), Json) + "\n"; }
        }
        // UTF-8 bytes bound storage; the event cap separately bounds object overhead.
        var entry = new Entry(++_sequence, fact, scope, subjectScope, issue, change, line, Utf8.GetByteCount(line));
        lock (_memoryGate)
        {
            _entries.AddLast(entry); _eventBytes += entry.Bytes;
            _dirty = true;
            while (_entries.Count > _limits.MaxEvents || _eventBytes > _limits.MaxBytes)
            { RemoveOldest(); _capacityCompaction = _rewrite = _retentionLimited = true; }
        }
    }

    private void TrimExpired()
    {
        var oldest = _now().AddDays(-90);
        lock (_memoryGate)
        {
            for (var node = _entries.First; node != null;)
            {
                var next = node.Next;
                if (node.Value.Event.Time < oldest)
                { Remove(node); _rewrite = _dirty = true; }
                node = next;
            }
        }
    }

    private void Persist()
    {
        if (!_dirty) return;
        if (_redactionDeferred)
        {
            var redact = AvailableRedactor();
            if (redact == null) return; // Never replace good stored evidence with a temporary mask.
            Entry[] retained;
            lock (_memoryGate) retained = _entries.ToArray();
            var prepared = new List<OpsEvent>(retained.Length);
            foreach (var entry in retained)
            {
                if (!TryRedactStored(entry.Event, redact, out var safe)) return;
                prepared.Add(safe);
            }
            lock (_memoryGate) { _entries.Clear(); _eventBytes = 0; }
            for (var i = 0; i < retained.Length; i++)
                Add(prepared[i], retained[i].Scope, retained[i].SubjectScope, retained[i].Issue, retained[i].Change);
            _redactionDeferred = false;
            _rewrite = true;
        }
        EnsurePaths();
        Entry[] snapshot;
        lock (_memoryGate) snapshot = _entries.ToArray();
        var pending = snapshot.Where(entry => entry.Sequence > _persistedSequence).ToArray();
        if (!_rewrite && (!File.Exists(_path) || new FileInfo(_path).Length != _diskBytes)) _rewrite = true;
        if (_diskBytes + pending.Sum(entry => entry.Bytes) > _limits.MaxBytes) _capacityCompaction = _rewrite = _retentionLimited = true;
        if (_capacityCompaction)
        {
            // Reserve headroom so a full journal is not rewritten for every later event.
            lock (_memoryGate)
            {
                while (_entries.Count > Math.Max(1, _limits.MaxEvents * 3 / 4) ||
                    _eventBytes > _limits.MaxBytes * 3 / 4)
                    RemoveOldest();
                snapshot = _entries.ToArray();
            }
        }
        _beforeWrite?.Invoke(_path);
        if (_rewrite)
        {
            var content = string.Concat(snapshot.Select(entry => entry.Line));
            AtomicWrite(_path, content);
            _diskBytes = Utf8.GetByteCount(content);
        }
        else if (pending.Length != 0)
        {
            using var stream = RestrictedStream(_path, FileMode.OpenOrCreate);
            stream.Seek(0, SeekOrigin.End);
            using var writer = new StreamWriter(stream, Utf8, 4096, leaveOpen: true);
            foreach (var entry in pending) writer.Write(entry.Line);
            writer.Flush(); stream.Flush(true);
            _diskBytes = stream.Length;
        }
        _persistedSequence = _sequence;
        _rewrite = _dirty = _capacityCompaction = false;
        _persistenceHealthy = true;
    }

    private void BackupCorrupt(Func<string, string> redact)
    {
        try
        {
            EnsurePaths();
            if (!File.Exists(_path)) return;
            var safe = redact(Utf8.GetString(ReadBounded(_path, _limits.MaxBytes)));
            if (Utf8.GetByteCount(safe) > _limits.MaxBytes) safe = "";
            AtomicWrite(_corrupt, safe);
        }
        catch { /* A bounded backup is best effort; never hold operational work. */ }
    }

    private void AtomicWrite(string destination, string text)
    {
        try
        {
            using (var stream = RestrictedStream(_stage, FileMode.Create))
            using (var writer = new StreamWriter(stream, Utf8))
            { writer.Write(text); writer.Flush(); stream.Flush(true); }
            File.Move(_stage, destination, true);
        }
        finally { try { File.Delete(_stage); } catch { } }
    }

    private static FileStream RestrictedStream(string path, FileMode mode)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
        return FileSystemAclExtensions.Create(new FileInfo(path), mode, FileSystemRights.ReadData | FileSystemRights.WriteData,
            FileShare.Read, 4096, FileOptions.None, security);
    }

    private void EnsurePaths()
    {
        new DiagnosticAutomation(new(), _root).EnsureDirectory(_root);
        Directory.CreateDirectory(_root);
        foreach (var path in new[] { _path, _stage, _corrupt })
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked journal files are unsupported");
        foreach (var auxiliary in new[] { _stage, _corrupt })
            if (File.Exists(auxiliary) && new FileInfo(auxiliary).Length > _limits.MaxBytes)
                File.Delete(auxiliary);
    }

    private static byte[] ReadBounded(string path, int maximum)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > maximum) throw new InvalidDataException("Journal byte bound exceeded");
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = stream.Read(buffer, 0, Math.Min(buffer.Length, Math.Max(1, maximum - (int)output.Length + 1)));
            if (count == 0) return output.ToArray();
            if (output.Length + count > maximum) throw new InvalidDataException("Journal byte bound exceeded");
            output.Write(buffer, 0, count);
        }
    }

    private static byte[] ReadTailBounded(string path, int maximum, out bool limited)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var available = stream.Length;
        var start = Math.Max(0, available - maximum);
        limited = start != 0;
        stream.Seek(start, SeekOrigin.Begin);
        var bytes = new byte[(int)Math.Min(available, maximum)];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = stream.Read(bytes, count, bytes.Length - count);
            if (read == 0) break;
            count += read;
        }
        var offset = 0;
        // A partial oldest row is never parsed or exposed; its suffix could be a secret.
        if (limited)
        {
            while (offset < count && bytes[offset] != (byte)'\n') offset++;
            if (offset < count) offset++;
        }
        return bytes.AsSpan(offset, count - offset).ToArray();
    }

    private void RemoveOldest() { if (_entries.First is { } first) Remove(first); }
    private void Remove(LinkedListNode<Entry> node)
    { _eventBytes -= node.Value.Bytes; _entries.Remove(node); }
    private Func<string, string> SafeRedactor()
    { try { return _redactor(); } catch { return _ => ""; } }
    private Func<string, string>? AvailableRedactor()
    {
        try { var redact = _redactor(); return DiagnosticAutomation.IsRedactionUnavailable(redact) ? null : redact; }
        catch { return null; }
    }
    private static bool TryRedactStored(OpsEvent fact, Func<string, string> redact, out OpsEvent safe)
    {
        safe = fact;
        try
        {
            string Field(string value)
            {
                var result = redact(value);
                if (result == "[redaction unavailable]" && result != value) throw new InvalidOperationException();
                return result;
            }
            safe = Bound(fact with { Kind = Field(fact.Kind), Severity = Field(fact.Severity), ServerId = Field(fact.ServerId),
                Subject = Field(fact.Subject), Detail = Field(fact.Detail), Actor = Field(fact.Actor), CorrelationId = Field(fact.CorrelationId) });
            return true;
        }
        catch { return false; }
    }
    private static OpsEvent Bound(OpsEvent fact) => fact with
    {
        Kind = Fit(fact.Kind, 64), Severity = Fit(fact.Severity, 32), ServerId = Fit(fact.ServerId, 256),
        Subject = Fit(fact.Subject, 1024), Detail = Fit(fact.Detail, 4096), Actor = Fit(fact.Actor, 256), CorrelationId = Fit(fact.CorrelationId, 256)
    };
    private static string Fit(string? value, int maximum) => value is { } text && text.Length <= maximum ? text : "";
    private static OpsEvent Redact(OpsEvent fact, Func<string, string> redact, ReadBudget? budget = null)
    {
        try
        {
            string Field(string value) { budget?.Check(); return redact(value); }
            return Bound(fact with { Kind = Field(fact.Kind), Severity = Field(fact.Severity), ServerId = Field(fact.ServerId),
                Subject = Field(fact.Subject), Detail = Field(fact.Detail), Actor = Field(fact.Actor), CorrelationId = Field(fact.CorrelationId) });
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ReadLimitException)
        { return fact with { Kind = "", Severity = "", ServerId = "", Subject = "", Detail = "", Actor = "", CorrelationId = "" }; }
    }
    private static bool IsIncident(OpsEvent fact) => fact.Severity.Equals("Warning", StringComparison.OrdinalIgnoreCase) ||
        fact.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase) || fact.Severity.Equals("Fatal", StringComparison.OrdinalIgnoreCase);
    private static bool IsChange(OpsEvent fact) => fact.Kind is "config-saved" or "config-change-set" or "config-remount" or "agent-run" or "update-available" or
        "update-deferred" or "update-restart-requested";
    private List<string> RetentionLimits()
    {
        var limits = new List<string>();
        if (_retentionLimited) limits.Add("retained history limit");
        if (_invalidRows > 0) limits.Add("invalid journal rows skipped: " + _invalidRows);
        if (Volatile.Read(ref _redactionDeferred)) limits.Add("stored redaction refresh pending");
        if (!Ready.IsCompleted) limits.Add("journal loading");
        return limits;
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(value)));
    private static string SubjectIdentity(string subject) => Hash(subject.ToUpperInvariant());
    private static void ValidateQuery(string? server, int days, int limit, int maximum)
    {
        if (server?.Length > 256 || days is < 1 or > 90 || limit < 1 || limit > maximum) throw new ArgumentOutOfRangeException(nameof(limit));
    }
    private void WarnOnce()
    {
        _persistenceHealthy = false;
        if (Interlocked.Exchange(ref _warned, 1) == 0) Serilog.Log.Warning("Ops journal persistence unavailable; recent facts remain in bounded memory");
    }
    private void Wake() { try { _wake.Release(); } catch (SemaphoreFullException) { } }
    private static Task<T> Detached<T>(Func<T> action, CancellationToken ct)
    { using var flow = ExecutionContext.IsFlowSuppressed() ? default : ExecutionContext.SuppressFlow(); return Task.Run(action, ct); }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.Writer.TryComplete(); Wake();
        try { _worker.Wait(TimeSpan.FromSeconds(2)); } catch { }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) { _queue.Writer.TryComplete(); Wake(); }
        try { await _worker.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
    }
    private sealed record Stored(OpsEvent Event, string Scope, string SubjectScope, bool Issue, bool Change);
    private sealed record Entry(long Sequence, OpsEvent Event, string Scope, string SubjectScope, bool Issue, bool Change, string Line, int Bytes);
    private sealed class ReadLimitException : Exception;
    private sealed class ReadBudget(CancellationToken cancellation)
    {
        private readonly Stopwatch _elapsed = Stopwatch.StartNew();
        public void Check()
        {
            cancellation.ThrowIfCancellationRequested();
            if (_elapsed.Elapsed > TimeSpan.FromSeconds(3)) throw new ReadLimitException();
        }
    }
}
