using System.IO;
using System.Text;
using Serilog;

namespace GlDrive.Util;

/// <summary>
/// Optional persistence protection for cold, non-authoritative JSON state. The primary
/// file keeps its existing format. A rejected or failed write never rolls back memory.
/// Callers must supply their own schema validation and identify intentional removals.
/// </summary>
public sealed class GuardedJsonFile
{
    private readonly string _path;
    private readonly Func<string, int> _validateAndCount;
    private readonly int _maximumBytes;
    private readonly Func<bool> _enabled;
    private readonly Action<string> _warning;
    private readonly Func<DateTime> _utcNow;
    private readonly object _pendingLock = new();
    private string? _pendingGeneration;
    private Task _worker = Task.CompletedTask;
    private bool _running;
    private int _warned;
    private DateTime _lastGenerationUtc = DateTime.MinValue;

    public GuardedJsonFile(string path, Func<string, int> validateAndCount, int maximumBytes,
        Func<bool>? enabled = null, Action<string>? warning = null, Func<DateTime>? utcNow = null)
    {
        _path = Path.GetFullPath(path);
        _validateAndCount = validateAndCount;
        _maximumBytes = maximumBytes > 0 ? maximumBytes : throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        _enabled = enabled ?? (() => false);
        _warning = warning ?? (message => Log.Warning("Guarded preferences: {Reason}", message));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public string? Read()
    {
        try { return File.Exists(_path) ? ReadValidated(_path) : null; }
        catch
        {
            Quarantine();
            WarnOnce("Unreadable state was preserved where possible; continuing with empty state.");
            return null;
        }
    }

    /// <summary>
    /// Preserve the caller's existing synchronous primary-save contract. Extra recovery
    /// generation I/O runs in a bounded background worker and never delays the primary.
    /// </summary>
    public void Write(string json, bool allowShrink = false)
        => Persist(json, allowShrink, _enabled());

    private void QueueGeneration(string json)
    {
        lock (_pendingLock)
        {
            _pendingGeneration = json;
            if (_running) return;
            _running = true;
            if (ExecutionContext.IsFlowSuppressed()) _worker = Task.Run(Drain);
            else
            {
                using (ExecutionContext.SuppressFlow()) _worker = Task.Run(Drain);
            }
        }
    }

    public Task FlushPendingWritesAsync() { lock (_pendingLock) return _worker; }

    private void Drain()
    {
        while (true)
        {
            string next;
            lock (_pendingLock)
            {
                if (_pendingGeneration == null) { _running = false; return; }
                next = _pendingGeneration;
                _pendingGeneration = null;
            }
            try { PreserveGeneration(next); }
            catch { WarnOnce("A recovery generation could not be saved; ordinary persistence continues."); }
        }
    }

    private void Persist(string json, bool allowShrink, bool guardEnabled)
    {
        try
        {
            if (Encoding.UTF8.GetByteCount(json) > _maximumBytes) throw new InvalidDataException();
            var nextCount = guardEnabled ? _validateAndCount(json) : 0;
            SecureFile.WithPathLock(_path, () =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                string? generation = null;
                if (guardEnabled)
                {
                    string? previous = null;
                    try { if (File.Exists(_path)) previous = ReadValidated(_path); }
                    catch { Quarantine(); }
                    if (previous != null)
                    {
                        var previousCount = _validateAndCount(previous);
                        if (!allowShrink && previousCount > 0 && (long)nextCount * 2 < previousCount)
                        {
                            // Keep a single bounded candidate for explicit local recovery.
                            SecureFile.WriteAllTextRestricted(_path + ".shrink-rejected", json);
                            WarnOnce("A suspiciously smaller save was preserved separately; changes remain in memory.");
                            return;
                        }
                        if (_utcNow() - _lastGenerationUtc >= TimeSpan.FromMinutes(1))
                            generation = previous;
                    }
                    else if (_lastGenerationUtc == DateTime.MinValue && !File.Exists(GenerationPath(1)))
                        generation = json;
                }
                SecureFile.WriteAllTextRestricted(_path, json);
                if (generation != null)
                {
                    QueueGeneration(generation);
                    _lastGenerationUtc = _utcNow();
                }
            });
        }
        catch { WarnOnce("State could not be saved; changes remain in memory and later writes will retry."); }
    }

    private void PreserveGeneration(string previous)
    {
        for (var generation = 2; generation >= 1; generation--)
        {
            var source = GenerationPath(generation);
            if (!File.Exists(source)) continue;
            string json;
            try { json = ReadValidated(source); }
            catch { continue; }
            SecureFile.WriteAllTextRestricted(GenerationPath(generation + 1), json);
        }
        var newest = GenerationPath(1);
        SecureFile.WriteAllTextRestricted(newest, previous);
        if (!string.Equals(previous, ReadValidated(newest), StringComparison.Ordinal))
            throw new IOException("Recovery generation verification failed");
    }

    private string GenerationPath(int generation) => _path + ".last-good." + generation;

    private string ReadValidated(string path)
    {
        if (new FileInfo(path).Length > _maximumBytes) throw new InvalidDataException();
        var json = File.ReadAllText(path);
        if (_validateAndCount(json) < 0) throw new InvalidDataException();
        return json;
    }

    private void Quarantine()
    {
        try
        {
            File.Copy(_path, _path + ".corrupt", overwrite: true);
            SecureFile.RestrictFilePermissions(_path + ".corrupt");
        }
        catch { }
    }

    private void WarnOnce(string message)
    {
        if (Interlocked.Exchange(ref _warned, 1) == 0)
            try { _warning(message); } catch { }
    }
}
