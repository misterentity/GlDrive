using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Serilog;

namespace GlDrive.AiAgent;

public sealed class LogDigester
{
    private readonly string _aiDataRoot;

    public LogDigester(string aiDataRoot) { _aiDataRoot = aiDataRoot; }

    public DigestBundle Build(int windowDays, DateTime? nowOverride = null)
    {
        var now = nowOverride ?? DateTime.Now;
        var windowStart = now.AddDays(-windowDays).Date;
        var bundle = new DigestBundle
        {
            WindowStart = windowStart.ToString("O"),
            WindowEnd = now.ToString("O")
        };

        // Per-stream digest assembly lives here. Tasks 4.3 and 4.4 each wire one
        // sub-digester into this method. Today the bundle returns with empty
        // per-stream sections — still useful for evidence pointers + debug.

        bundle.EvidencePointers = new Dictionary<string, string>
        {
            ["races"]             = $"races-{now:yyyyMMdd}.jsonl",
            ["nukes"]             = $"nukes-{now:yyyyMMdd}.jsonl",
            ["announcesNoMatch"]  = $"announces-nomatch-{now:yyyyMMdd}.jsonl",
            ["wishlistAttempts"]  = $"wishlist-attempts-{now:yyyyMMdd}.jsonl"
        };

        return bundle;
    }

    /// <summary>
    /// Reads all records of type T from {prefix}-{date}.jsonl[.gz] files for each day
    /// in the window [fromInclusive, toInclusive]. Used by per-stream digesters.
    /// </summary>
    public IEnumerable<T> ReadStream<T>(string prefix, DateTime fromInclusive, DateTime toInclusive)
    {
        for (var d = fromInclusive.Date; d <= toInclusive.Date; d = d.AddDays(1))
        {
            foreach (var ev in ReadFile<T>(Path.Combine(_aiDataRoot, $"{prefix}-{d:yyyyMMdd}.jsonl")))
                yield return ev;
            foreach (var ev in ReadFile<T>(Path.Combine(_aiDataRoot, $"{prefix}-{d:yyyyMMdd}.jsonl.gz")))
                yield return ev;
        }
    }

    private static IEnumerable<T> ReadFile<T>(string path)
    {
        if (!File.Exists(path)) yield break;
        Stream? raw = null;
        StreamReader? reader = null;
        try
        {
            raw = File.OpenRead(path);
            Stream stream = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
                ? new GZipStream(raw, CompressionMode.Decompress)
                : raw;
            reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                T? v;
                try { v = JsonSerializer.Deserialize<T>(line); }
                catch (Exception ex) { Log.Debug(ex, "digester parse skip {Path}", path); continue; }
                if (v != null) yield return v;
            }
        }
        finally
        {
            reader?.Dispose();
            raw?.Dispose();
        }
    }
}
