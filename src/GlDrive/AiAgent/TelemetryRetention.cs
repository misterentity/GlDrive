using System.IO;
using System.IO.Compression;
using Serilog;

namespace GlDrive.AiAgent;

public sealed class TelemetryRetention : IDisposable
{
    private readonly string _root;
    private readonly int _gzipAfterDays;
    private readonly int _deleteAfterDays;
    private readonly Timer _timer;

    public TelemetryRetention(string aiDataRoot, int gzipAfterDays, int deleteAfterDays)
    {
        _root = aiDataRoot;
        _gzipAfterDays = gzipAfterDays;
        _deleteAfterDays = deleteAfterDays;

        // Daily sweep; first fire at next midnight + 5 min (after SectionActivityRollup)
        var nextMidnight = DateTime.Today.AddDays(1) - DateTime.Now + TimeSpan.FromMinutes(5);
        _timer = new Timer(_ => Sweep(), null, nextMidnight, TimeSpan.FromDays(1));
    }

    public void Sweep()
    {
        try
        {
            if (!Directory.Exists(_root)) return;
            var now = DateTime.Now;

            // Uncompressed jsonl: gzip after N days, delete after M
            foreach (var path in Directory.GetFiles(_root, "*-*.jsonl"))
            {
                var d = ParseDate(path);
                if (!d.HasValue) continue;
                var age = (now.Date - d.Value.Date).TotalDays;
                if (age >= _deleteAfterDays) { TryDelete(path); continue; }
                if (age >= _gzipAfterDays) TryGzip(path);
            }

            // Already-compressed: delete after M
            foreach (var path in Directory.GetFiles(_root, "*-*.jsonl.gz"))
            {
                var d = ParseDate(path);
                if (!d.HasValue) continue;
                if ((now.Date - d.Value.Date).TotalDays >= _deleteAfterDays)
                    TryDelete(path);
            }
        }
        catch (Exception ex) { Log.Warning(ex, "TelemetryRetention sweep failed"); }
    }

    private static DateTime? ParseDate(string path)
    {
        // Expected: {prefix}-YYYYMMDD.jsonl  or  {prefix}-YYYYMMDD.jsonl.gz
        var name = Path.GetFileName(path);
        // Strip .gz if present
        if (name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            name = name[..^3];
        // Strip .jsonl
        if (name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
            name = name[..^6];
        var idx = name.LastIndexOf('-');
        if (idx < 0) return null;
        var datePart = name[(idx + 1)..];
        return DateTime.TryParseExact(datePart, "yyyyMMdd",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var d) ? d : null;
    }

    private static void TryGzip(string path)
    {
        try
        {
            var gz = path + ".gz";
            using (var fs = File.OpenRead(path))
            using (var outFs = File.Create(gz))
            using (var gzStream = new GZipStream(outFs, CompressionLevel.SmallestSize))
                fs.CopyTo(gzStream);
            File.Delete(path);
        }
        catch (Exception ex) { Log.Debug(ex, "gzip failed for {Path}", path); }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) { Log.Debug(ex, "delete failed for {Path}", path); }
    }

    public void Dispose() => _timer.Dispose();
}
