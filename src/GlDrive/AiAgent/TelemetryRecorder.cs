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
    private DateTime _lastDropWarnUtc = DateTime.MinValue;

    public TelemetryRecorder(string appDataRoot, int maxFileMB)
    {
        _root = Path.Combine(appDataRoot, "ai-data");
        Directory.CreateDirectory(_root);
        _maxFileMB = maxFileMB;
        foreach (TelemetryStream s in Enum.GetValues<TelemetryStream>())
        {
            _writers[s] = new StreamWriterTask(s, _root);
            _drops[s] = 0;
        }
    }

    public void Record<T>(TelemetryStream stream, T evt) where T : TelemetryEnvelope
    {
        try
        {
            var json = JsonSerializer.Serialize(evt, evt.GetType(), JsonOpts);
            if (!_writers[stream].TryEnqueue(json))
            {
                Interlocked.Increment(ref CollectionsMarshal_GetValueRef(_drops, stream));
                WarnDropsOnce();
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "TelemetryRecorder serialize failed for {Stream}", stream);
        }
    }

    private void WarnDropsOnce()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastDropWarnUtc).TotalMinutes < 5) return;
        _lastDropWarnUtc = now;
        Log.Warning("Telemetry drops: {Drops}", string.Join(",", _drops.Select(kv => $"{kv.Key}={kv.Value}")));
    }

    public Dictionary<TelemetryStream, int> GetDropCounts() => new(_drops);

    public void Dispose()
    {
        foreach (var w in _writers.Values) w.Dispose();
    }

    private static ref int CollectionsMarshal_GetValueRef(Dictionary<TelemetryStream, int> dict, TelemetryStream key)
        => ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(dict, key, out _);

    private sealed class StreamWriterTask : IDisposable
    {
        private readonly Channel<string> _channel = Channel.CreateBounded<string>(
            new BoundedChannelOptions(2048)
            {
                FullMode = BoundedChannelFullMode.DropNewest,
                SingleReader = true
            });
        private readonly Task _pump;
        private readonly CancellationTokenSource _cts = new();
        private readonly TelemetryStream _stream;
        private readonly string _root;

        public StreamWriterTask(TelemetryStream stream, string root)
        {
            _stream = stream; _root = root;
            _pump = Task.Run(PumpAsync);
        }

        public bool TryEnqueue(string line) => _channel.Writer.TryWrite(line);

        private async Task PumpAsync()
        {
            try
            {
                while (await _channel.Reader.WaitToReadAsync(_cts.Token))
                {
                    while (_channel.Reader.TryRead(out var line))
                    {
                        try
                        {
                            var path = Path.Combine(_root, FileName(DateTime.Now));
                            await File.AppendAllTextAsync(path, line + "\n", Encoding.UTF8, _cts.Token);
                        }
                        catch (Exception ex) { Log.Debug(ex, "telemetry write fail {Stream}", _stream); }
                    }
                }
            }
            catch (OperationCanceledException) { /* shutdown */ }
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
                _ => "unknown"
            };
            return $"{prefix}-{d:yyyyMMdd}.jsonl";
        }

        public void Dispose()
        {
            _channel.Writer.TryComplete();
            try { _cts.Cancel(); _pump.Wait(TimeSpan.FromSeconds(2)); } catch { }
            _cts.Dispose();
        }
    }
}
