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

        bundle.Races           = new RacesDigester().Build(ReadStream<RaceOutcomeEvent>("races", windowStart, now));
        bundle.Nukes           = new NukesDigester().Build(ReadStream<NukeDetectedEvent>("nukes", windowStart, now));
        bundle.SiteHealth      = new SiteHealthDigester().Build(ReadStream<SiteHealthEvent>("site-health", windowStart, now));
        bundle.Announces       = new AnnouncesDigester().Build(ReadStream<AnnounceNoMatchEvent>("announces-nomatch", windowStart, now));
        bundle.Wishlist        = new WishlistDigester().Build(ReadStream<WishlistAttemptEvent>("wishlist-attempts", windowStart, now));
        bundle.Overrides       = new OverridesDigester().Build(ReadStream<ConfigOverrideEvent>("overrides", windowStart, now));
        bundle.Downloads       = new DownloadsDigester().Build(ReadStream<DownloadOutcomeEvent>("downloads", windowStart, now));
        bundle.Transfers       = new TransfersDigester().Build(ReadStream<FileTransferEvent>("transfers", windowStart, now));
        bundle.SectionActivity = new SectionActivityDigester().Build(ReadStream<SectionActivityEvent>("section-activity", windowStart, now));
        bundle.Errors          = new ErrorsDigester().Build(ReadStream<ErrorSignatureEvent>("errors", windowStart, now));

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
