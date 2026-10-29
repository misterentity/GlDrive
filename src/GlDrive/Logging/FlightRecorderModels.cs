using System.IO;
using System.Text;
using Serilog.Events;

namespace GlDrive.Logging;

public sealed record RecordedLog(DateTimeOffset Time, LogEventLevel Level, string Component, string Correlation, string Template, string Text);

/// <summary>Evicts oldest diagnostics; its bounds never participate in engine admission.</summary>
internal sealed class LogRing(int maxBytes = 4 * 1024 * 1024, int maxEntries = 8192, TimeSpan? maxAge = null)
{
    private readonly Queue<(RecordedLog Row, int Bytes)> _rows = new();
    private readonly object _gate = new();
    private readonly TimeSpan _maxAge = maxAge ?? TimeSpan.FromMinutes(20);
    private int _bytes;
    public int Bytes { get { lock (_gate) return _bytes; } }
    public void Add(RecordedLog row)
    {
        var bytes = 2 * (row.Text.Length + row.Template.Length + row.Component.Length + row.Correlation.Length) + 128;
        lock (_gate)
        {
            if (bytes > maxBytes) return;
            _rows.Enqueue((row, bytes)); _bytes += bytes;
            Trim(row.Time);
        }
    }
    private void Trim(DateTimeOffset now)
    {
        while (_rows.Count > 0 && (_bytes > maxBytes || _rows.Count > maxEntries || _rows.Peek().Row.Time < now - _maxAge))
            _bytes -= _rows.Dequeue().Bytes;
    }
    public RecordedLog[] Snapshot(DateTimeOffset now, LogEventLevel minimum = LogEventLevel.Verbose, string component = "", string text = "", int limit = 500)
    {
        lock (_gate)
        {
            Trim(now);
            return _rows.Select(x => x.Row).Where(x => x.Level >= minimum &&
                (component.Length == 0 || x.Component.Equals(component, StringComparison.OrdinalIgnoreCase)) &&
                (text.Length == 0 || x.Text.Contains(text, StringComparison.OrdinalIgnoreCase) || x.Correlation.Contains(text, StringComparison.OrdinalIgnoreCase)))
                .TakeLast(Math.Clamp(limit, 1, 8192)).ToArray();
        }
    }
}

public sealed class ComponentTraceSwitches
{
    public static readonly string[] Components = ["FTP", "IRC", "Spread", "TLS", "General"];
    private readonly Dictionary<string, DateTimeOffset> _until = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    public DateTimeOffset Enable(string component, TimeSpan duration, DateTimeOffset now)
    {
        if (!Components.Contains(component, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("Unknown trace component");
        var expires = now + TimeSpan.FromSeconds(Math.Clamp(duration.TotalSeconds, 1, 7200));
        lock (_gate) _until[component] = expires;
        return expires;
    }
    public bool IsEnabled(string component, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_until.TryGetValue(component, out var until)) return false;
            if (until > now) return true;
            _until.Remove(component); return false;
        }
    }
    internal static string Component(LogEvent e)
    {
        var template = e.MessageTemplate.Text;
        if (template.StartsWith("[FTP]", StringComparison.Ordinal) || template.StartsWith("FTP", StringComparison.Ordinal)) return "FTP";
        if (template.Contains("IRC", StringComparison.OrdinalIgnoreCase)) return "IRC";
        if (template.Contains("GnuTLS", StringComparison.OrdinalIgnoreCase) || template.Contains("CPSV", StringComparison.OrdinalIgnoreCase)) return "TLS";
        if (template.StartsWith("Spread", StringComparison.OrdinalIgnoreCase) || template.StartsWith("FXP", StringComparison.OrdinalIgnoreCase)) return "Spread";
        return "General";
    }
}

/// <summary>Never retain an arbitrary LogEvent graph or a partly cut credential.</summary>
internal sealed class BoundedLogWriter(int capacity) : TextWriter
{
    private readonly StringBuilder _text = new();
    public override Encoding Encoding => Encoding.UTF8;
    public override void Write(char value) { Check(1); _text.Append(value); }
    public override void Write(string? value) { if (value != null) { Check(value.Length); _text.Append(value); } }
    public override void Write(char[] buffer, int index, int count) { Check(count); _text.Append(buffer, index, count); }
    private void Check(int count) { if (count > capacity - _text.Length) throw new InvalidDataException("Log entry exceeds recorder bound"); }
    public override string ToString() => _text.ToString();
}
