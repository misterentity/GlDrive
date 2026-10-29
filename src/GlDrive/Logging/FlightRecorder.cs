using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using GlDrive.Config;
using GlDrive.Services.Automation;
using GlDrive.Util;
using Serilog.Core;
using Serilog.Events;

namespace GlDrive.Logging;

public sealed record DiagnosticBundleResult(int MaskedRows, int SkippedFiles);

/// <summary>Passive diagnostics. Emission never waits for disk, credentials, or the UI.</summary>
public sealed class FlightRecorder : ILogEventSink, IDisposable
{
    internal const int QueueCapacity = 512;
    internal const int MaxEntryChars = 16 * 1024;
    internal const int CapsuleBytes = 1024 * 1024;
    private readonly Channel<RecordedLog> _queue;
    private readonly LogRing _ring = new();
    private readonly LogSignatureRules _rules;
    private readonly Func<Func<string, string>> _redactor;
    private readonly Func<bool> _captureEnabled;
    private readonly Func<DateTimeOffset> _now;
    private readonly string _logRoot;
    private readonly Task _worker;
    private readonly SemaphoreSlim _fileGate = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly Dictionary<string, Dictionary<string, long>> _census = new();
    private readonly Queue<LogSignatureHit> _hits = new();
    private readonly object _hitGate = new();
    private DateTimeOffset _lastCapture = DateTimeOffset.MinValue;
    private DateTimeOffset _lastNotice = DateTimeOffset.MinValue;
    private bool _captureHistoryLoaded;
    private DateTimeOffset _lastCensusSave;
    private long _dropped;
    private int _disposed, _warning;
    public long Dropped => Interlocked.Read(ref _dropped);
    public event Action<string, string>? SignatureDetected;

    internal FlightRecorder(string appDataRoot, Func<Func<string, string>> redactor, Func<bool>? captureEnabled = null,
        Func<DateTimeOffset>? now = null, LogSignatureRules? rules = null)
    {
        _logRoot = Path.Combine(appDataRoot, "logs");
        _redactor = redactor; _captureEnabled = captureEnabled ?? (() => true); _now = now ?? (() => DateTimeOffset.UtcNow);
        _rules = rules ?? LogSignatureRules.Shipped();
        _queue = Channel.CreateBounded<RecordedLog>(new BoundedChannelOptions(QueueCapacity)
        { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest, AllowSynchronousContinuations = false }, _ => Interlocked.Increment(ref _dropped));
        using var flow = ExecutionContext.IsFlowSuppressed() ? default : ExecutionContext.SuppressFlow();
        _worker = Task.Run(RunAsync);
    }

    public void Emit(LogEvent logEvent)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try
        {
            using var writer = new BoundedLogWriter(MaxEntryChars);
            string text;
            try
            {
                logEvent.RenderMessage(writer);
                if (logEvent.Exception is { } exception)
                {
                    writer.Write("\n" + exception.GetType().Name + ": ");
                    writer.Write(exception.Message);
                    writer.Write("\n"); writer.Write(exception.StackTrace);
                }
                text = writer.ToString();
            }
            catch { text = "[oversized or unrenderable log entry omitted]"; }
            var template = logEvent.MessageTemplate.Text;
            if (template.Length > 1024) template = "[oversized message template omitted]";
            var correlation = "";
            foreach (var name in new[] { "ServerId", "ServerName", "Server", "Release" })
                if (logEvent.Properties.TryGetValue(name, out var value) && value is ScalarValue { Value: string key } && key.Length <= 256)
                { correlation = key; break; }
            _queue.Writer.TryWrite(new(logEvent.Timestamp, logEvent.Level, ComponentTraceSwitches.Component(logEvent), correlation, template, text));
            if (logEvent.Level >= LogEventLevel.Error) Wake();
        }
        catch { Interlocked.Increment(ref _dropped); }
    }

    private async Task RunAsync()
    {
        do
        {
            try
            {
                var redact = _queue.Reader.TryPeek(out _) ? SafeRedactor() : null;
                var count = 0;
                while (redact != null && count++ < QueueCapacity && _queue.Reader.TryRead(out var row))
                {
                    var safe = Redact(row, redact);
                    _ring.Add(safe);
                    Count(safe);
                    foreach (var hit in _rules.Observe(safe)) ObserveHit(hit);
                    if (safe.Level >= LogEventLevel.Error && _captureEnabled()) await CaptureCoreAsync("error", redact).ConfigureAwait(false);
                }
                foreach (var hit in _rules.Tick(_now())) ObserveHit(hit);
                if (_now() - _lastCensusSave >= TimeSpan.FromMinutes(1))
                { _lastCensusSave = _now(); await SaveCensusAsync().ConfigureAwait(false); }
            }
            catch { WarnOnce(); }
            if (Volatile.Read(ref _disposed) != 0)
            {
                if (_queue.Reader.TryPeek(out _)) continue;
                break;
            }
            await _wake.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        } while (true);
        try { await SaveCensusAsync().ConfigureAwait(false); } catch { WarnOnce(); }
    }

    private void ObserveHit(LogSignatureHit hit)
    {
        lock (_hitGate)
        {
            _hits.Enqueue(hit);
            while (_hits.Count > 100) _hits.Dequeue();
        }
        _ring.Add(new(hit.Time, LogEventLevel.Warning, "General", hit.Correlation, "Tell: {Signature}", "Tell: " + hit.Id));
        if (hit.Time - _lastNotice >= TimeSpan.FromMinutes(5))
        {
            _lastNotice = hit.Time;
            try { SignatureDetected?.Invoke(hit.Id, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hit.Correlation)))) ; } catch { }
        }
        if (_captureEnabled()) CaptureCoreAsync(hit.Id, SafeRedactor()).GetAwaiter().GetResult();
    }

    public RecordedLog[] Snapshot(LogEventLevel minimum = LogEventLevel.Verbose, string component = "", string text = "", int limit = 500)
        => _ring.Snapshot(_now(), minimum, component, text, limit);
    internal LogSignatureHit[] SignatureSnapshot() { lock (_hitGate) return _hits.ToArray(); }
    public Task<RecordedLog[]> RedactSnapshotAsync(IEnumerable<RecordedLog> rows) => Detached(() =>
    {
        var redact = SafeRedactor(); return rows.Take(8192).Select(row => Redact(row, redact)).ToArray();
    });
    private static RecordedLog Redact(RecordedLog row, Func<string, string> redact)
    {
        try
        {
            var correlation = redact(row.Correlation); var template = redact(row.Template); var text = redact(row.Text);
            return row with { Correlation = correlation.Length <= 256 ? correlation : "", Template = template.Length <= 1024 ? template : "[expanded template omitted]",
                Text = text.Length <= MaxEntryChars ? text : "[expanded log entry omitted]" };
        }
        catch { return row with { Correlation = "", Template = "[redaction unavailable]", Text = "[redaction unavailable]" }; }
    }
    private Func<string, string> SafeRedactor()
    {
        try { return _redactor(); } catch { return _ => "[redaction unavailable]"; }
    }

    public Task<string?> CaptureAsync(string reason) => Detached(() => CaptureCoreAsync("manual", SafeRedactor()));
    private async Task<string?> CaptureCoreAsync(string reason, Func<string, string> redact)
    {
        await _fileGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var now = _now();
            var directory = Path.Combine(_logRoot, "capsules");
            EnsureSafeDirectory(directory);
            if (!_captureHistoryLoaded)
            {
                _captureHistoryLoaded = true;
                _lastCapture = new DirectoryInfo(directory).EnumerateFiles("incident-*.log")
                    .Where(f => (f.Attributes & FileAttributes.ReparsePoint) == 0).Select(f => (DateTimeOffset)f.LastWriteTimeUtc)
                    .DefaultIfEmpty(DateTimeOffset.MinValue).Max();
                if (_lastCapture > now) _lastCapture = now;
            }
            if (now - _lastCapture < TimeSpan.FromMinutes(5)) return null;
            _lastCapture = now;
            var path = Path.Combine(directory, $"incident-{now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");
            var text = CapsuleText(reason, redact);
            if (!MakeRoom(directory, "incident-*.log", 32, 32L * CapsuleBytes, Encoding.UTF8.GetByteCount(text)))
            { WarnOnce(); return null; }
            WriteDiagnostic(path, text);
            Prune(directory, "incident-*.log", 32, 32L * CapsuleBytes, now.AddDays(-30));
            return path;
        }
        catch { WarnOnce(); return null; }
        finally { _fileGate.Release(); }
    }

    private string CapsuleText(string reason, Func<string, string> redact, Action? maskedRow = null)
    {
        var lines = new List<string>();
        var bytes = 0;
        foreach (var row in Snapshot(limit: 8192).Reverse())
        {
            var safe = Redact(row, redact);
            var line = $"{safe.Time:O} [{safe.Level}] [{safe.Component}] {safe.Text}\n";
            var size = Encoding.UTF8.GetByteCount(line);
            if (bytes + size > CapsuleBytes - 1024) break;
            if (IsMaskedText(safe.Text)) maskedRow?.Invoke();
            lines.Add(line); bytes += size;
        }
        lines.Reverse();
        return $"Incident: {reason}\nCapture UTC: {_now():O}\nDropped recorder entries: {Dropped}\n" + string.Concat(lines);
    }

    private void Count(RecordedLog row)
    {
        var hour = row.Time.UtcDateTime.ToString("yyyyMMdd-HH");
        if (!_census.TryGetValue(hour, out var counts))
        {
            if (_census.Count >= 24) _census.Remove(_census.Keys.Min(StringComparer.Ordinal)!);
            _census[hour] = counts = LoadCensus(hour);
        }
        var key = row.Level + "|" + row.Component + "|" + row.Template;
        if (!counts.ContainsKey(key) && counts.Count >= 256) key = "Other|overflow";
        counts[key] = counts.GetValueOrDefault(key) + 1;
    }
    private Dictionary<string, long> LoadCensus(string hour)
    {
        var path = Path.Combine(_logRoot, "census", $"census-{hour}.json");
        try
        {
            if (!File.Exists(path)) return new(StringComparer.Ordinal);
            EnsureSafeDirectory(Path.GetDirectoryName(path)!);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > CapsuleBytes)
                throw new InvalidDataException();
            var counts = JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(path)) ?? throw new InvalidDataException();
            if (counts.Count > 257 || counts.Any(p => p.Key.Length > 1100 || p.Value < 0)) throw new InvalidDataException();
            return counts;
        }
        catch
        {
            try
            {
                var file = new FileInfo(path);
                if (file.Exists && (file.Attributes & FileAttributes.ReparsePoint) == 0 && file.Length <= CapsuleBytes &&
                    MakeRoom(Path.GetDirectoryName(path)!, "census-*.json.corrupt", 16, 16L * CapsuleBytes, file.Length, path + ".corrupt"))
                    File.Copy(path, path + ".corrupt", true);
            }
            catch { }
            try { Prune(Path.GetDirectoryName(path)!, "census-*.json.corrupt", 16, 16L * CapsuleBytes, _now().AddDays(-90)); } catch { }
            WarnOnce(); return new(StringComparer.Ordinal);
        }
    }
    private async Task SaveCensusAsync()
    {
        await _fileGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var directory = Path.Combine(_logRoot, "census");
            EnsureSafeDirectory(directory);
            var saved = new HashSet<string>();
            foreach (var pair in _census)
            {
                var path = Path.Combine(directory, $"census-{pair.Key}.json");
                var text = JsonSerializer.Serialize(pair.Value);
                if (Encoding.UTF8.GetByteCount(text) > CapsuleBytes) { WarnOnce(); continue; }
                if (!MakeRoom(directory, "census-*.json", 2160, 128L * 1024 * 1024, Encoding.UTF8.GetByteCount(text), path))
                { WarnOnce(); continue; }
                WriteDiagnostic(path, text); saved.Add(pair.Key);
            }
            var current = _now().UtcDateTime.ToString("yyyyMMdd-HH");
            foreach (var hour in _census.Keys.Where(h => string.CompareOrdinal(h, current) < 0 && saved.Contains(h)).ToArray()) _census.Remove(hour);
            Prune(directory, "census-*.json", 2160, 128L * 1024 * 1024, _now().AddDays(-90));
        }
        catch { WarnOnce(); }
        finally { _fileGate.Release(); }
    }

    private static bool IsMaskedText(string text) => text is "" or "[redacted]" or "[hidden]" or "█" or
        "[redaction unavailable]" or "[expanded log entry omitted]" or "[oversized or unrenderable log entry omitted]";

    public Task<DiagnosticBundleResult> ExportBundleAsync(string destination, AppConfig config) => Detached(async () =>
    {
        var redact = SafeRedactor();
        await _fileGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // CreateNew prevents accidental replacement; the chooser must select a fresh path.
            using var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
            var manifest = new Dictionary<string, object>();
            var maskedRows = 0;
            var skippedFiles = 0;
            void Add(string name, string text, bool alreadyRedacted = false)
            {
                if (!alreadyRedacted) text = string.Join('\n', text.Split('\n').Select(line =>
                {
                    var safe = redact(line);
                    if (line.Length > 0 && IsMaskedText(safe)) maskedRows++;
                    return safe;
                }));
                var bytes = Encoding.UTF8.GetBytes(text);
                if (bytes.Length > CapsuleBytes) { skippedFiles++; return; }
                using var entry = archive.CreateEntry(name, CompressionLevel.Fastest).Open(); entry.Write(bytes);
                manifest[name] = Convert.ToHexString(SHA256.HashData(bytes));
            }
            Add("recorder.log", CapsuleText("diagnostic-export", redact, () => maskedRows++), alreadyRedacted: true);
            var node = JsonSerializer.SerializeToNode(config, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;
            ConfigurationAutomation.Redact(node, false);
            Add("settings.json", node.ToJsonString());
            foreach (var kind in new[] { ("capsules", "incident-*.log"), ("census", "census-*.json") })
            {
                var directory = Path.Combine(_logRoot, kind.Item1);
                if (!Directory.Exists(directory)) continue;
                EnsureSafeDirectory(directory);
                foreach (var file in new DirectoryInfo(directory).EnumerateFiles(kind.Item2)
                    .Where(f => (f.Attributes & FileAttributes.ReparsePoint) == 0 && f.Length <= CapsuleBytes)
                    .OrderByDescending(f => f.LastWriteTimeUtc).Take(8))
                    Add(kind.Item1 + "/" + file.Name, DiagnosticAutomation.ReadTail(file.FullName, 8192, CapsuleBytes).Text);
            }
            var result = new DiagnosticBundleResult(maskedRows, skippedFiles);
            Add("manifest.json", JsonSerializer.Serialize(new { files = manifest, maskedRows, skippedFiles }), alreadyRedacted: true);
            return result;
        }
        finally { _fileGate.Release(); }
    });

    private void EnsureSafeDirectory(string directory)
    {
        new DiagnosticAutomation(new(), Path.GetDirectoryName(_logRoot)!).EnsureDirectory(directory);
        Directory.CreateDirectory(directory);
    }
    private static void WriteDiagnostic(string path, string text)
    {
        // The recorder owns _fileGate; timestamped files must not populate SecureFile's
        // process-lifetime path-lock registry. One staging name also bounds failed stages.
        var stage = Path.Combine(Path.GetDirectoryName(path)!, "recorder-write.tmp");
        if (File.Exists(stage) && (File.GetAttributes(stage) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked diagnostic stage");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
        try
        {
            using (var stream = FileSystemAclExtensions.Create(new FileInfo(stage), FileMode.Create,
                FileSystemRights.WriteData | FileSystemRights.ReadData, FileShare.None, 4096, FileOptions.None, security))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            { writer.Write(text); writer.Flush(); stream.Flush(true); }
            File.Move(stage, path, true);
        }
        finally { try { File.Delete(stage); } catch { } }
    }
    internal static void Prune(string directory, string pattern, int count, long bytes, DateTimeOffset oldest)
    {
        var files = new DirectoryInfo(directory).EnumerateFiles(pattern).Where(f => (f.Attributes & FileAttributes.ReparsePoint) == 0)
            .OrderByDescending(f => f.LastWriteTimeUtc).ToArray();
        long keptBytes = 0; var keptCount = 0;
        foreach (var file in files)
        {
            if (file.LastWriteTimeUtc < oldest.UtcDateTime || keptCount >= count || keptBytes + file.Length > bytes)
            { try { file.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            else { keptCount++; keptBytes += file.Length; }
        }
    }
    internal static bool MakeRoom(string directory, string pattern, int maxCount, long maxBytes, long incomingBytes, string? replacing = null, Action<string>? delete = null)
    {
        delete ??= File.Delete;
        var files = new DirectoryInfo(directory).EnumerateFiles(pattern).Where(f => (f.Attributes & FileAttributes.ReparsePoint) == 0 &&
            !string.Equals(f.FullName, replacing, StringComparison.OrdinalIgnoreCase)).OrderBy(f => f.LastWriteTimeUtc).ToArray();
        var bytes = files.Sum(f => f.Length); var count = files.Length;
        foreach (var file in files)
        {
            if (count < maxCount && bytes + incomingBytes <= maxBytes) break;
            try { delete(file.FullName); bytes -= file.Length; count--; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return count < maxCount && bytes + incomingBytes <= maxBytes;
    }
    private void WarnOnce()
    {
        if (Interlocked.Exchange(ref _warning, 1) == 0)
            Serilog.Log.Warning("Flight recorder persistence unavailable; bounded diagnostics remain in memory");
    }
    internal Task Completion => _worker;
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.Writer.TryComplete(); Wake();
        // Only logger teardown waits, with a fixed deadline. Emit/console paths never wait.
        try { _worker.Wait(TimeSpan.FromSeconds(2)); } catch { }
    }
    private void Wake() { try { if (_wake.CurrentCount == 0) _wake.Release(); } catch (SemaphoreFullException) { } }
    private static Task<T> Detached<T>(Func<T> work)
    {
        using var flow = ExecutionContext.IsFlowSuppressed() ? default : ExecutionContext.SuppressFlow();
        return Task.Run(work);
    }
    private static Task<T> Detached<T>(Func<Task<T>> work)
    {
        using var flow = ExecutionContext.IsFlowSuppressed() ? default : ExecutionContext.SuppressFlow();
        return Task.Run(work);
    }
    private static Task Detached(Func<Task> work)
    {
        using var flow = ExecutionContext.IsFlowSuppressed() ? default : ExecutionContext.SuppressFlow();
        return Task.Run(work);
    }
}
