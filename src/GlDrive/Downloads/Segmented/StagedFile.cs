using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Serilog;

namespace GlDrive.Downloads.Segmented;

/// <summary>
/// On-disk staging for one <see cref="TransferFile"/>: bytes land in <c>&lt;LocalPath&gt;.gdpart</c>
/// via positional writes (safe for concurrent lanes at different offsets), done ranges persist
/// in <c>&lt;LocalPath&gt;.gdpart.json</c>, and <see cref="CommitAsync"/> renames into place.
/// </summary>
public sealed class StagedFile : IAsyncDisposable
{
    public const string StagingSuffix = ".gdpart";
    public const string SidecarSuffix = ".gdpart.json";

    private static readonly TimeSpan PersistInterval = TimeSpan.FromSeconds(2);
    private const long PersistBytes = 16L * 1024 * 1024;

    private readonly object _lock = new();
    private SafeFileHandle? _handle;
    private bool _committed;
    private bool _closed;
    private long _bytesSincePersist;
    private readonly Stopwatch _sincePersist = Stopwatch.StartNew();

    private StagedFile(TransferFile file, ByteRangeSet done, bool isComplete)
    {
        File = file;
        Done = done;
        IsComplete = isComplete;
    }

    public TransferFile File { get; }
    public ByteRangeSet Done { get; }

    /// <summary>The final file already existed whole before this transfer started.</summary>
    public bool IsComplete { get; }

    public string StagingPath => File.LocalPath + StagingSuffix;
    public string SidecarPath => File.LocalPath + SidecarSuffix;

    /// <summary>Test seam: return an exception to fail the write at the given offset as a disk error would.</summary>
    internal Func<long, Exception?>? WriteFault { get; set; }

    private sealed record Sidecar(long Size, string? Validator, long[][] Ranges);

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static StagedFile Open(TransferFile f)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(f.LocalPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        string staging = f.LocalPath + StagingSuffix, sidecar = f.LocalPath + SidecarSuffix;

        if (f.Size > 0 && System.IO.File.Exists(f.LocalPath) && new FileInfo(f.LocalPath).Length >= f.Size)
        {
            DeleteQuietly(staging);
            DeleteQuietly(sidecar);
            return new StagedFile(f, new ByteRangeSet([new ByteRange(0, f.Size)]), isComplete: true);
        }

        if (System.IO.File.Exists(staging))
        {
            string? why = f.Size < 0 ? "remote size unknown" : null;
            Sidecar? saved = null;
            if (why == null)
            {
                saved = TryReadSidecar(sidecar, out why);
                if (saved != null && saved.Size != f.Size)
                    why = $"remote file changed (size {saved.Size} -> {f.Size})";
                else if (saved != null && f.Validator != null && saved.Validator != f.Validator)
                    why = $"remote file changed (validator {saved.Validator} -> {f.Validator})";
            }

            if (why == null && saved != null)
            {
                var done = new ByteRangeSet(saved.Ranges
                    .Where(r => r.Length == 2)
                    .Select(r => new ByteRange(Math.Max(0, r[0]), Math.Min(f.Size, r[1]))));
                Log.Information("Resuming staged download {Path}: {Done}/{Size} bytes done", f.LocalPath, done.TotalLength, f.Size);
                var resumed = new StagedFile(f, done, false);
                resumed.OpenHandle(FileMode.OpenOrCreate);
                return resumed;
            }

            Log.Information("Restarting staged download {Path}: {Reason}", f.LocalPath, why);
            DeleteQuietly(staging);
            DeleteQuietly(sidecar);
        }
        else
        {
            DeleteQuietly(sidecar);

            if (f.Size > 0 && System.IO.File.Exists(f.LocalPath))
            {
                long len = new FileInfo(f.LocalPath).Length;
                if (len > 0)
                {
                    // Older versions appended straight to the final name; adopt that prefix.
                    System.IO.File.Move(f.LocalPath, staging);
                    Log.Information("Adopting legacy partial download {Path}: {Len}/{Size} bytes", f.LocalPath, len, f.Size);
                    var adopted = new StagedFile(f, new ByteRangeSet([new ByteRange(0, len)]), false);
                    adopted.OpenHandle(FileMode.Open);
                    adopted.FlushState();
                    return adopted;
                }
            }
        }

        var fresh = new StagedFile(f, new ByteRangeSet(), false);
        fresh.OpenHandle(FileMode.Create);
        if (f.Size >= 0) fresh.FlushState();
        return fresh;
    }

    private void OpenHandle(FileMode mode)
    {
        _handle = System.IO.File.OpenHandle(StagingPath, mode, FileAccess.ReadWrite, FileShare.Read, FileOptions.Asynchronous);
        if (File.Size > 0) RandomAccess.SetLength(_handle, File.Size);
    }

    private static Sidecar? TryReadSidecar(string path, out string? why)
    {
        why = null;
        if (!System.IO.File.Exists(path)) { why = "no sidecar"; return null; }
        try
        {
            var s = JsonSerializer.Deserialize<Sidecar>(System.IO.File.ReadAllText(path), JsonOptions);
            if (s?.Ranges == null) { why = "sidecar unreadable"; return null; }
            return s;
        }
        catch (Exception ex)
        {
            why = $"sidecar unreadable ({ex.Message})";
            return null;
        }
    }

    public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (WriteFault?.Invoke(offset) is { } fault) throw fault;
        var handle = _handle ?? throw new InvalidOperationException($"{StagingPath} is not open for writing");
        return RandomAccess.WriteAsync(handle, data, offset, ct);
    }

    public void MarkDone(ByteRange range)
    {
        lock (_lock)
        {
            if (_committed || _closed) return;
            _bytesSincePersist += Done.Add(range);
            if (_bytesSincePersist >= PersistBytes || _sincePersist.Elapsed >= PersistInterval)
                PersistLocked();
        }
    }

    /// <summary>
    /// Forget every done range (the bytes on disk stay and get overwritten). Used when a file's
    /// completion could not be confirmed, or the server refused to resume it mid-file.
    /// </summary>
    public void ResetDone()
    {
        lock (_lock)
        {
            if (_committed || _closed) return;
            Done.Clear();
            PersistLocked();
        }
    }

    public void FlushState()
    {
        lock (_lock)
        {
            if (_committed || IsComplete) return;
            PersistLocked();
        }
    }

    private void PersistLocked()
    {
        if (File.Size < 0) return; // unknown size never resumes
        var state = new Sidecar(File.Size, File.Validator, Done.Ranges.Select(r => new[] { r.Start, r.End }).ToArray());
        var tmp = SidecarPath + ".tmp";
        System.IO.File.WriteAllText(tmp, JsonSerializer.Serialize(state, JsonOptions));
        System.IO.File.Move(tmp, SidecarPath, overwrite: true);
        _bytesSincePersist = 0;
        _sincePersist.Restart();
    }

    /// <param name="finalLength">Truncate to this length first (unknown-size downloads).</param>
    public Task CommitAsync(long? finalLength = null)
    {
        lock (_lock)
        {
            if (_committed || IsComplete) return Task.CompletedTask;
            if (finalLength is { } len && _handle != null) RandomAccess.SetLength(_handle, len);
            _handle?.Dispose();
            _handle = null;
            _closed = true;
            System.IO.File.Move(StagingPath, File.LocalPath, overwrite: true);
            DeleteQuietly(SidecarPath);
            _committed = true;
        }
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            if (!_committed && !_closed && !IsComplete)
            {
                try { PersistLocked(); }
                catch (Exception ex) { Log.Warning(ex, "Could not save download state for {Path}", File.LocalPath); }
            }
            _closed = true;
            _handle?.Dispose();
            _handle = null;
        }
        return ValueTask.CompletedTask;
    }

    private static void DeleteQuietly(string path)
    {
        try { System.IO.File.Delete(path); }
        catch (Exception ex) { Log.Debug(ex, "Could not delete {Path}", path); }
    }
}
