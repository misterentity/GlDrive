using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Serilog;

namespace GlDrive.AiAgent;

public sealed class LogDigester
{
    private readonly string _aiDataRoot;
    private readonly Func<string, Stream> _openRead;
    private int _parseSkips;

    public LogDigester(string aiDataRoot) : this(aiDataRoot, File.OpenRead) { }

    internal LogDigester(string aiDataRoot, Func<string, Stream> openRead)
    {
        _aiDataRoot = aiDataRoot;
        _openRead = openRead;
    }

    /// <summary>
    /// Telemetry rows dropped because they would not deserialize, counted across every stream read
    /// so far. A skipped row is lost evidence, and this used to be recorded with Log.Debug against
    /// a sink that runs at Information — so it left no trace anywhere. Build() surfaces a non-zero
    /// count at Warning; the property exists so the loss is also assertable in tests.
    /// </summary>
    public int ParseSkips => _parseSkips;

    public Task<DigestBundle> BuildAsync(int windowDays, CancellationToken cancellationToken = default)
        => Task.Run(() => Build(windowDays, cancellationToken: cancellationToken), cancellationToken);

    public DigestBundle Build(int windowDays, DateTime? nowOverride = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = nowOverride ?? DateTime.Now;
        var windowStart = now.AddDays(-windowDays).Date;
        _parseSkips = 0;
        var bundle = new DigestBundle
        {
            WindowStart = windowStart.ToString("O"),
            WindowEnd = now.ToString("O")
        };

        bundle.Races           = new RacesDigester().Build(ReadStream<RaceOutcomeEvent>("races", windowStart, now, cancellationToken));
        bundle.Nukes           = new NukesDigester().Build(ReadStream<NukeDetectedEvent>("nukes", windowStart, now, cancellationToken));
        bundle.SiteHealth      = new SiteHealthDigester().Build(ReadStream<SiteHealthEvent>("site-health", windowStart, now, cancellationToken));
        bundle.Announces       = new AnnouncesDigester().Build(ReadStream<AnnounceNoMatchEvent>("announces-nomatch", windowStart, now, cancellationToken));
        bundle.Wishlist        = new WishlistDigester().Build(ReadStream<WishlistAttemptEvent>("wishlist-attempts", windowStart, now, cancellationToken));
        bundle.Overrides       = new OverridesDigester().Build(ReadStream<ConfigOverrideEvent>("overrides", windowStart, now, cancellationToken));
        bundle.Downloads       = new DownloadsDigester().Build(ReadStream<DownloadOutcomeEvent>("downloads", windowStart, now, cancellationToken));
        bundle.Transfers       = new TransfersDigester().Build(ReadStream<FileTransferEvent>("transfers", windowStart, now, cancellationToken), cancellationToken);
        bundle.SectionActivity = new SectionActivityDigester().Build(ReadStream<SectionActivityEvent>("section-activity", windowStart, now, cancellationToken));
        // section→folder learning: correlate matched announces with race destinations (re-reading "races" yields a fresh enumerable)
        bundle.SectionFolder   = new SectionFolderDigester().Build(ReadStream<MatchedAnnounceEvent>("matched-announces", windowStart, now, cancellationToken), ReadStream<RaceOutcomeEvent>("races", windowStart, now, cancellationToken));
        bundle.Errors          = new ErrorsDigester().Build(ReadStream<ErrorSignatureEvent>("errors", windowStart, now, cancellationToken), windowStart, now);

        bundle.EvidencePointers = new Dictionary<string, string>
        {
            ["races"]             = $"races-{now:yyyyMMdd}.jsonl",
            ["nukes"]             = $"nukes-{now:yyyyMMdd}.jsonl",
            ["announcesNoMatch"]  = $"announces-nomatch-{now:yyyyMMdd}.jsonl",
            ["wishlistAttempts"]  = $"wishlist-attempts-{now:yyyyMMdd}.jsonl",
            // evidence pointer for the section→folder learning rows so the AiAgent audit trail can cite source events
            ["matchedAnnounces"]  = $"matched-announces-{now:yyyyMMdd}.jsonl"
        };

        if (_parseSkips > 0)
            Log.Warning("Digest dropped {Count} unparseable telemetry row(s) from {Root} over the "
                      + "{Days}-day window — the agent is reasoning on incomplete evidence.",
                _parseSkips, _aiDataRoot, windowDays);

        return bundle;
    }

    /// <summary>
    /// Reads all records of type T from {prefix}-{date}.jsonl[.gz] files for each day
    /// in the window [fromInclusive, toInclusive]. Used by per-stream digesters.
    /// </summary>
    public IEnumerable<T> ReadStream<T>(string prefix, DateTime fromInclusive, DateTime toInclusive,
        CancellationToken cancellationToken = default)
    {
        for (var d = fromInclusive.Date; d <= toInclusive.Date; d = d.AddDays(1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var ev in ReadFile<T>(Path.Combine(_aiDataRoot, $"{prefix}-{d:yyyyMMdd}.jsonl"), cancellationToken))
                yield return ev;
            foreach (var ev in ReadFile<T>(Path.Combine(_aiDataRoot, $"{prefix}-{d:yyyyMMdd}.jsonl.gz"), cancellationToken))
                yield return ev;
        }
    }

    private IEnumerable<T> ReadFile<T>(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) yield break;
        Stream? raw = null;
        StreamReader? reader = null;
        try
        {
            raw = _openRead(path);
            Stream stream = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
                ? new GZipStream(raw, CompressionMode.Decompress)
                : raw;
            reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(line)) continue;
                T? v;
                try { v = JsonSerializer.Deserialize<T>(line); }
                catch (Exception ex)
                {
                    // Skip the row, not the stream — one bad line must not cost the whole file.
                    // But count it: Build() reports a non-zero total at Warning, because a
                    // silently-dropped row is exactly how the 2026-08-14 poison row stayed
                    // invisible for 40+ agent runs.
                    _parseSkips++;
                    Log.Debug(ex, "digester parse skip {Path}", path);
                    continue;
                }
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
