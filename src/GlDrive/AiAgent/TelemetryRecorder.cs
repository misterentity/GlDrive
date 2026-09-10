using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Serilog;

namespace GlDrive.AiAgent;

public sealed class TelemetryRecorder : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private readonly string _root;
    // Retained for forward-compat; actual enforcement lives in TelemetryRetention (not here).
    private readonly int _maxFileMB;
    private readonly Dictionary<TelemetryStream, StreamWriterTask> _writers = new();
    private readonly Dictionary<TelemetryStream, int> _drops = new();
    private readonly object _dropLock = new();
    private readonly CancellationTokenSource _cts = new();
    private int _disposed;
    private DateTime _lastDropWarnUtc = DateTime.MinValue;

    public TelemetryRecorder(string appDataRoot, int maxFileMB)
        : this(appDataRoot, maxFileMB,
            (path, text, ct) => File.AppendAllTextAsync(path, text, FileEncoding, ct))
    {
    }

    internal TelemetryRecorder(string appDataRoot, int maxFileMB,
        Func<string, string, CancellationToken, Task> append, int queueCapacity = 2048)
    {
        _root = Path.Combine(appDataRoot, "ai-data");
        Directory.CreateDirectory(_root);
        _maxFileMB = maxFileMB;
        foreach (TelemetryStream s in Enum.GetValues<TelemetryStream>())
        {
            _drops[s] = 0;
            _writers[s] = new StreamWriterTask(s, _root, append, queueCapacity,
                count => RecordDrops(s, count), _cts.Token);
        }
    }

    /// <summary>
    /// Per-event ceiling. A telemetry event is an aggregate row — ids, section keys, counters —
    /// so 256 KB is far above anything organic while still catching pathology.
    ///
    /// On 2026-08-14 a race was recorded with a 2,000,000-char section. It was written as a 2 MB
    /// single-line row, copied forward into section-activity by SectionActivityRollup, and then
    /// serialized straight into the agent prompt: ~1.5M tokens, refused by every model, 40+
    /// consecutive dead runs. Bounding the prompt heals the read side; this stops the write side
    /// producing another one.
    /// </summary>
    internal const int MaxEventBytes = 256 * 1024;

    /// <summary>
    /// UTF-8 with no byte-order mark. These files are JSON Lines: every line must stand alone as
    /// valid JSON, including the first one.
    ///
    /// <c>Encoding.UTF8</c> is NOT this — it is UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
    /// and File.AppendAllText writes that preamble whenever it creates the file. A new file is
    /// created per stream per day, so passing Encoding.UTF8 here put a BOM on the first row of
    /// every stream every day. It stayed invisible only because StreamReader strips BOMs by
    /// default; readers over raw bytes (Utf8JsonReader, jq, python) fail on line 1.
    /// </summary>
    internal static readonly Encoding FileEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    internal static bool IsAcceptableSize(string json) =>
        json.Length <= MaxEventBytes && FileEncoding.GetByteCount(json) <= MaxEventBytes;

    public void Record<T>(TelemetryStream stream, T evt) where T : TelemetryEnvelope
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try
        {
            var json = JsonSerializer.Serialize(evt, evt.GetType(), JsonOpts);
            if (!IsAcceptableSize(json))
            {
                // Loud, not silent: an event this size means something upstream is broken, and a
                // Debug line would be invisible (the Serilog sink runs at Information).
                Log.Warning("Telemetry event dropped: {Stream} serialized to {Bytes} bytes, over the {Max}-byte cap. "
                          + "This indicates a pathological field value upstream.",
                    stream, FileEncoding.GetByteCount(json), MaxEventBytes);
                RecordDrops(stream, 1);
                return;
            }
            if (!_writers[stream].TryEnqueue(json))
            {
                RecordDrops(stream, 1);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "TelemetryRecorder serialize failed for {Stream}", stream);
        }
    }

    private void RecordDrops(TelemetryStream stream, int count)
    {
        string summary;
        lock (_dropLock)
        {
            _drops[stream] += count;
            var now = DateTime.UtcNow;
            if ((now - _lastDropWarnUtc).TotalMinutes < 5) return;
            _lastDropWarnUtc = now;
            summary = string.Join(",", _drops.Select(kv => $"{kv.Key}={kv.Value}"));
        }
        Log.Warning("Telemetry drops: {Drops}", summary);
    }

    public Dictionary<TelemetryStream, int> GetDropCounts()
    {
        lock (_dropLock) return new(_drops);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // Complete every stream first, then allow all queues to drain in parallel.
        // Cancelling first used to discard accepted events during a normal exit.
        foreach (var w in _writers.Values) w.Complete();
        var drain = Task.WhenAll(_writers.Values.Select(w => w.Completion));
        try
        {
            if (!drain.Wait(TimeSpan.FromSeconds(2)))
            {
                Log.Warning("Telemetry shutdown drain timed out; cancelling remaining writes");
                _cts.Cancel();
            }
        }
        catch (Exception ex) { Log.Warning(ex, "Telemetry shutdown drain failed"); }
        // A timed-out write can still be using the token. Dispose its source only
        // after the pumps finish, without extending the shutdown deadline.
        _ = drain.ContinueWith(_ => _cts.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private sealed class StreamWriterTask
    {
        private readonly Channel<string> _channel;
        private readonly Task _pump;
        private readonly CancellationToken _ct;
        private readonly Func<string, string, CancellationToken, Task> _append;
        private readonly Action<int> _recordDrops;
        private readonly TelemetryStream _stream;
        private readonly string _root;

        public StreamWriterTask(TelemetryStream stream, string root,
            Func<string, string, CancellationToken, Task> append, int queueCapacity,
            Action<int> recordDrops, CancellationToken ct)
        {
            _stream = stream; _root = root;
            _append = append;
            _recordDrops = recordDrops;
            _ct = ct;
            // TryWrite stays non-blocking and returns false at capacity. DropNewest
            // silently evicts a previously accepted row while returning true.
            _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(queueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true
            });
            _pump = Task.Run(PumpAsync);
        }

        public bool TryEnqueue(string line) => _channel.Writer.TryWrite(line);
        public Task Completion => _pump;
        public void Complete() => _channel.Writer.TryComplete();

        private async Task PumpAsync()
        {
            try
            {
                var batch = new StringBuilder();
                while (await _channel.Reader.WaitToReadAsync(_ct))
                {
                    _ct.ThrowIfCancellationRequested();
                    batch.Clear();
                    var count = 0;
                    // One open/append/close per bounded batch, rather than per row.
                    while (count < 64 && batch.Length < MaxEventBytes && _channel.Reader.TryRead(out var line))
                    {
                        batch.Append(line).Append('\n');
                        count++;
                    }
                    try
                    {
                        var path = Path.Combine(_root, FileName(DateTime.Now));
                        await _append(path, batch.ToString(), _ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _recordDrops(count);
                        Log.Debug(ex, "telemetry write fail {Stream}", _stream);
                        if (_ct.IsCancellationRequested) break;
                    }
                }
            }
            catch (OperationCanceledException) { /* shutdown */ }
            finally
            {
                var abandoned = 0;
                while (_channel.Reader.TryRead(out _)) abandoned++;
                if (abandoned > 0) _recordDrops(abandoned);
            }
        }

        private string FileName(DateTime d)
        {
            var prefix = _stream switch
            {
                TelemetryStream.Races => "races",
                TelemetryStream.Nukes => "nukes",
                TelemetryStream.SiteHealth => "site-health",
                TelemetryStream.AnnouncesNoMatch => "announces-nomatch",
                TelemetryStream.WishlistAttempts => "wishlist-attempts",
                TelemetryStream.Overrides => "overrides",
                TelemetryStream.Downloads => "downloads",
                TelemetryStream.Transfers => "transfers",
                TelemetryStream.SectionActivity => "section-activity",
                TelemetryStream.Errors => "errors",
                // Section→folder learning: matched-announces-{date}.jsonl prefix for the new positive-match stream.
                TelemetryStream.MatchedAnnounces => "matched-announces",
                _ => "unknown"
            };
            return $"{prefix}-{d:yyyyMMdd}.jsonl";
        }

    }
}
