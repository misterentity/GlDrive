using System.Text.RegularExpressions;
using Serilog.Core;
using Serilog.Events;

namespace GlDrive.AiAgent;

public sealed class ErrorSignatureSink : ILogEventSink
{
    private sealed record Sig(string Component, string ExceptionType, string NormalizedMessage, string StackTopFrame);

    private readonly Dictionary<Sig, (int count, DateTime first, DateTime last)> _agg = new();
    private readonly object _lock = new();
    private readonly Timer _flushTimer;

    public TelemetryRecorder? Recorder { get; set; }

    public ErrorSignatureSink()
    {
        _flushTimer = new Timer(_ => Flush(), null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
    }

    public void Emit(LogEvent ev)
    {
        if (ev.Level < LogEventLevel.Error) return;
        if (Recorder is null) return;
        var sig = Signature(ev);
        lock (_lock)
        {
            if (_agg.TryGetValue(sig, out var v))
                _agg[sig] = (v.count + 1, v.first, DateTime.UtcNow);
            else
                _agg[sig] = (1, DateTime.UtcNow, DateTime.UtcNow);
        }
    }

    public void Flush()
    {
        if (Recorder is null) return;
        Dictionary<Sig, (int count, DateTime first, DateTime last)> snapshot;
        lock (_lock) { snapshot = new(_agg); _agg.Clear(); }
        foreach (var (sig, (count, first, last)) in snapshot)
        {
            Recorder.Record(TelemetryStream.Errors, new ErrorSignatureEvent
            {
                Component = sig.Component,
                ExceptionType = sig.ExceptionType,
                NormalizedMessage = sig.NormalizedMessage,
                StackTopFrame = sig.StackTopFrame,
                Count = count,
                FirstAt = first.ToString("O"),
                LastAt = last.ToString("O")
            });
        }
    }

    private static Sig Signature(LogEvent ev)
    {
        var component = ev.Properties.TryGetValue("SourceContext", out var sc) ? sc.ToString().Trim('"') : "";
        var exType = ev.Exception?.GetType().FullName ?? "";
        var msg = Normalize(ev.Exception?.Message ?? ev.MessageTemplate.Text);
        var frame = ev.Exception?.StackTrace?.Split('\n').FirstOrDefault()?.Trim() ?? "";
        return new Sig(component, exType, msg, frame);
    }

    private static readonly Regex _reDigits = new(@"\d+", RegexOptions.Compiled);
    private static readonly Regex _rePath = new(@"[A-Z]:\\[^\s""']+", RegexOptions.Compiled);
    private static readonly Regex _reGuid = new(@"[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}", RegexOptions.Compiled);

    private static string Normalize(string s)
    {
        s = _reDigits.Replace(s, "N");
        s = _rePath.Replace(s, "<path>");
        s = _reGuid.Replace(s, "<guid>");
        return s.Length > 200 ? s[..200] : s;
    }
}
