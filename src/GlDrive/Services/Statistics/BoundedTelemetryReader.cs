using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace GlDrive.Services.Statistics;

internal sealed record TelemetryReadLimits(long MaxBytes = 64 * 1024 * 1024, int MaxRows = 250_000,
    int MaxFiles = 640, int MaxLineBytes = 128 * 1024, int MaxMilliseconds = 5000);

/// <summary>
/// A bounded local reader. Calls on one instance share byte, time and row budgets;
/// reports reserve a separate instance for each stream through TelemetryReportReader.
/// Callbacks borrow the JSON document only for their duration. No input is rewritten or quarantined by a reader.
/// </summary>
public sealed class BoundedTelemetryReader
{
    private readonly string _root;
    private readonly TelemetryReadLimits _limits;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly HashSet<string> _reasons = new(StringComparer.Ordinal);
    private long _bytes;
    private int _files, _rows, _selected, _invalid, _oversized;
    private bool _stopped;
    private string? _releasePrefilter;

    public BoundedTelemetryReader(string aiDataRoot) : this(aiDataRoot, new()) { }
    internal BoundedTelemetryReader(string aiDataRoot, TelemetryReadLimits limits)
    {
        _root = Path.GetFullPath(aiDataRoot);
        _limits = limits;
        if (limits.MaxBytes < 1 || limits.MaxRows < 1 || limits.MaxFiles < 1 ||
            limits.MaxLineBytes is < 16 or > 256 * 1024 || limits.MaxMilliseconds < 1)
            throw new ArgumentOutOfRangeException(nameof(limits));
    }

    public TelemetryReadCoverage Coverage => new(_bytes, _files, _rows, _selected, _invalid, _oversized,
        _reasons.Count != 0, _reasons.OrderBy(x => x, StringComparer.Ordinal).ToArray());

    public void MarkPartial(string reason)
    {
        // Reasons are fixed application labels, never telemetry text.
        if (_reasons.Count < 16) _reasons.Add(reason);
    }

    public bool CheckBudget(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_stopped) return false;
        if (_clock.ElapsedMilliseconds >= _limits.MaxMilliseconds) { MarkPartial("time-limit"); _stopped = true; }
        if (_bytes >= _limits.MaxBytes) { MarkPartial("byte-limit"); _stopped = true; }
        if (_rows >= _limits.MaxRows) { MarkPartial("row-limit"); _stopped = true; }
        return !_stopped;
    }

    public void ReadStream(string prefix, DateTimeOffset fromUtc, DateTimeOffset toUtc,
        Action<JsonElement> observe, CancellationToken cancellationToken = default, string? releasePrefilter = null)
    {
        if (string.IsNullOrEmpty(prefix) || prefix.Length > 40 || prefix.Any(c => !char.IsAsciiLetterLower(c) && c != '-'))
            throw new ArgumentException("A telemetry stream name is required", nameof(prefix));
        if (fromUtc > toUtc || toUtc - fromUtc > TimeSpan.FromDays(91))
            throw new ArgumentOutOfRangeException(nameof(fromUtc));
        _releasePrefilter = releasePrefilter;
        // Filenames follow local dates, while event timestamps are UTC. Include both boundary dates.
        var first = fromUtc.LocalDateTime.Date;
        var last = toUtc.LocalDateTime.Date;
        for (var day = last; day >= first && CheckBudget(cancellationToken); day = day.AddDays(-1))
        {
            var raw = Path.Combine(_root, $"{prefix}-{day:yyyyMMdd}.jsonl");
            // Rotation can leave raw and compressed copies together. Never count both.
            if (!ReadFile(raw, fromUtc, toUtc, observe, cancellationToken))
                ReadFile(raw + ".gz", fromUtc, toUtc, observe, cancellationToken);
        }
    }

    private bool ReadFile(string path, DateTimeOffset from, DateTimeOffset to, Action<JsonElement> observe,
        CancellationToken cancellationToken)
    {
        if (!CheckBudget(cancellationToken)) return true;
        FileStream raw;
        try
        {
            if (!File.Exists(path)) return false;
            if (_files >= _limits.MaxFiles) { MarkPartial("file-limit"); _stopped = true; return true; }
            if (IsLinked(path)) { MarkPartial("linked-input"); return true; }
            raw = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                16 * 1024, FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { MarkPartial("unreadable-input"); return false; }

        _files++;
        using (raw)
        {
            using Stream input = path.EndsWith(".gz", StringComparison.Ordinal) ? new GZipStream(raw, CompressionMode.Decompress, leaveOpen: true) : raw;
            var block = new byte[16 * 1024];
            var line = new byte[_limits.MaxLineBytes];
            var length = 0;
            var overflow = false;
            try
            {
                while (CheckBudget(cancellationToken))
                {
                    var count = input.Read(block, 0, (int)Math.Min(block.Length, _limits.MaxBytes - _bytes));
                    if (count == 0) break;
                    _bytes += count;
                    // Check inside a block as well: callbacks count against the same deadline.
                    for (var i = 0; i < count; i++)
                    {
                        if ((i & 1023) == 0 && !CheckTimeAndRows(cancellationToken)) return true;
                        if (block[i] == (byte)'\n')
                        {
                            if (!CheckTimeAndRows(cancellationToken)) return true;
                            Process(line.AsSpan(0, length), overflow, from, to, observe);
                            length = 0; overflow = false;
                        }
                        else if (length < line.Length) line[length++] = block[i];
                        else overflow = true;
                    }
                }
                if (!_stopped && (length > 0 || overflow) && CheckTimeAndRows(cancellationToken))
                    Process(line.AsSpan(0, length), overflow, from, to, observe);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            { MarkPartial("unreadable-input"); }
        }
        // Once opened, a damaged raw file must not cause duplicate rows via its compressed twin.
        return true;
    }

    private bool CheckTimeAndRows(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_clock.ElapsedMilliseconds >= _limits.MaxMilliseconds) { MarkPartial("time-limit"); _stopped = true; }
        if (_rows >= _limits.MaxRows) { MarkPartial("row-limit"); _stopped = true; }
        return !_stopped;
    }

    private void Process(ReadOnlySpan<byte> line, bool overflow, DateTimeOffset from, DateTimeOffset to,
        Action<JsonElement> observe)
    {
        if (line.IsEmpty && !overflow) return;
        _rows++;
        if (overflow) { _oversized++; MarkPartial("oversized-row"); return; }
        if (line.Length >= 3 && line[0] == 0xEF && line[1] == 0xBB && line[2] == 0xBF) line = line[3..];
        // Unescaped ASCII names are common. Escaped rows always take the full parser,
        // so JSON escaping cannot hide a real exact match from this cheap rejection.
        if (_releasePrefilter != null && !line.Contains((byte)'\\') &&
            !Encoding.UTF8.GetString(line).Contains(_releasePrefilter, StringComparison.OrdinalIgnoreCase)) return;
        JsonDocument document;
        try { document = JsonDocument.Parse(line.ToArray(), new() { MaxDepth = 32 }); }
        catch (JsonException) { _invalid++; MarkPartial("invalid-row"); return; }
        using (document)
        {
            var row = document.RootElement;
            var timestamp = TelemetryValue.Timestamp(row, "ts");
            if (row.ValueKind != JsonValueKind.Object || timestamp == null)
            { _invalid++; MarkPartial("invalid-row"); return; }
            if (timestamp < from || timestamp > to) return;
            _selected++;
            observe(row);
        }
    }

    private static bool IsLinked(string path)
    {
        FileSystemInfo? current = new FileInfo(path);
        while (current != null)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) return true;
            current = current is FileInfo file ? file.Directory : ((DirectoryInfo)current).Parent;
        }
        return false;
    }
}

public static class TelemetryValue
{
    public static bool TryProperty(JsonElement row, string name, out JsonElement value)
    {
        value = default;
        return row.ValueKind == JsonValueKind.Object && row.TryGetProperty(name, out value);
    }
    public static string? Text(JsonElement row, string name, int maxLength = 512)
        => TryProperty(row, name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { } text && text.Length <= maxLength ? text : null;
    public static long? Integer(JsonElement row, string name)
        => TryProperty(row, name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;
    public static double? Number(JsonElement row, string name)
        => TryProperty(row, name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;
    public static DateTimeOffset? Timestamp(JsonElement row, string name)
        => Text(row, name, 64) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var date) ? date.ToUniversalTime() : null;
}
