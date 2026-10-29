using System.Diagnostics;
using System.Text.Json;

namespace GlDrive.Services.Statistics;

/// <summary>Reserves work for every stream; unused capacity passes to later streams.</summary>
internal sealed class TelemetryReportReader(string root, TelemetryReadLimits limits, int streams)
{
    private readonly List<(TelemetryReadCoverage Coverage, TelemetryStreamCoverage Stream)> _read = [];
    private readonly HashSet<string> _reasons = new(StringComparer.Ordinal);
    private long _remainingBytes = limits.MaxBytes;
    private int _remainingRows = limits.MaxRows, _remainingFiles = limits.MaxFiles;
    private int _remainingMilliseconds = limits.MaxMilliseconds, _remainingStreams = streams;
    private BoundedTelemetryReader? _active;

    internal void MarkPartial(string reason)
    {
        if (_active != null) _active.MarkPartial(reason);
        else _reasons.Add(reason);
    }

    internal void ReadStream(string stream, DateTimeOffset from, DateTimeOffset to, Action<JsonElement> observe,
        CancellationToken ct, string? releasePrefilter = null)
    {
        if (_remainingStreams <= 0) throw new InvalidOperationException("Unexpected telemetry stream");
        if (_remainingBytes <= 0 || _remainingRows <= 0 || _remainingFiles <= 0 || _remainingMilliseconds <= 0)
        {
            var reason = _remainingBytes <= 0 ? "byte-limit" : _remainingRows <= 0 ? "row-limit" : _remainingFiles <= 0 ? "file-limit" : "time-limit";
            _read.Add((new(0, 0, 0, 0, 0, 0, true, [reason]), new(stream, 0, 0, 0, null, null, true, [reason])));
            _remainingStreams--;
            return;
        }
        var share = new TelemetryReadLimits(Math.Max(1, _remainingBytes / _remainingStreams),
            Math.Max(1, _remainingRows / _remainingStreams), Math.Max(1, _remainingFiles / _remainingStreams),
            limits.MaxLineBytes, Math.Max(1, _remainingMilliseconds / _remainingStreams));
        var reader = new BoundedTelemetryReader(root, share);
        _active = reader;
        var watch = Stopwatch.StartNew();
        DateTimeOffset? oldest = null, newest = null;
        try
        {
            reader.ReadStream(stream, from, to, row =>
            {
                var time = TelemetryValue.Timestamp(row, "ts")!.Value;
                if (oldest == null || time < oldest) oldest = time;
                if (newest == null || time > newest) newest = time;
                observe(row);
            }, ct, releasePrefilter);
        }
        finally { _active = null; }
        var coverage = reader.Coverage;
        _read.Add((coverage, new(stream, coverage.BytesRead, coverage.RowsRead, coverage.RowsInWindow,
            oldest, newest, coverage.Partial, coverage.Limits)));
        _remainingBytes -= coverage.BytesRead;
        _remainingRows -= coverage.RowsRead;
        _remainingFiles -= coverage.FilesRead;
        _remainingMilliseconds -= (int)Math.Min(watch.ElapsedMilliseconds, share.MaxMilliseconds);
        _remainingStreams--;
    }

    internal TelemetryReadCoverage Coverage => new(_read.Sum(r => r.Coverage.BytesRead), _read.Sum(r => r.Coverage.FilesRead),
        _read.Sum(r => r.Coverage.RowsRead), _read.Sum(r => r.Coverage.RowsInWindow), _read.Sum(r => r.Coverage.InvalidRows),
        _read.Sum(r => r.Coverage.OversizedRows), _reasons.Count != 0 || _read.Any(r => r.Coverage.Partial),
        _reasons.Concat(_read.SelectMany(r => r.Coverage.Limits)).Distinct().ToArray())
        { Streams = _read.Select(r => r.Stream).ToArray() };
}
