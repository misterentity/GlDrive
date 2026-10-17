using System.IO;
using System.Text.RegularExpressions;
using FluentFTP;
using FluentFTP.Exceptions;
using GlDrive.Config;
using GlDrive.Ftp;
using Serilog;

namespace GlDrive.Downloads;

public sealed record SearchListingFailure(string Path, string Message);

public sealed record SearchOutcome(
    IReadOnlyList<SearchResult> Results, string Method, DateTime? IndexBuiltAtUtc,
    bool IsPartial, bool TimedOut, bool Truncated, IReadOnlyList<SearchListingFailure> Failures)
{
    public int FailedListings => Failures.Count;
}

public class FtpSearchService : IDisposable
{
    private readonly FtpConnectionPool _pool;
    private readonly SearchConfig _searchConfig;
    private const int MaxResults = 200;
    private static readonly TimeSpan BorrowTimeout = TimeSpan.FromSeconds(15);
    private readonly TimeSpan _searchDeadline = TimeSpan.FromSeconds(90);
    private readonly TimeSpan _listingRetryDelay = TimeSpan.FromMilliseconds(500);
    private readonly DirectoryLister? _listOverride;
    private readonly Func<string, CancellationToken, Task<FtpReply>>? _siteSearchOverride;

    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".", "..", ".banner", ".message"
    };

    // Zipscript status entries can be advertised as directories even though LIST
    // rejects them. Require both bracket decoration and a standalone status word:
    // real releases such as Show.S01.COMPLETE and [Group] Show remain searchable.
    private static readonly Regex StatusDirectory = new(
        @"(?:^|[\s(\-])(?:in[\s_\-]*)?complete(?:$|[\s)\]\-])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    // Nuked releases may remain in the parent listing while access to their
    // contents is revoked. Recognize the site's explicit status prefix, not
    // ordinary titles containing NUKED or bracketed release-group names.
    private static readonly Regex NukedDirectory = new(
        @"^\[\s*NUKED\s*\](?:\s*-|\s*$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    // A scene release directory: no whitespace, a trailing -GROUP tag of word characters that
    // contains a letter, and at least three ./_/- separated tokens. Its children are the release's
    // own Sample/Subs/CD1 folders, never further releases, so crawls index it without listing it.
    // Containers stay crawlable: categories (tv-hd, xxx-paysite) have two tokens, boxsets
    // (007.Boxset) no group tag, date dirs (2026-10-16) no letter. Two-token releases
    // (Cairn-RUNE) and digit-only groups (-403) are still listed, as before 3.11.19.
    internal static bool IsReleaseDirectory(string name)
    {
        var dash = name.LastIndexOf('-');
        if (dash <= 0 || name.Length - dash - 1 < 2 || name.Any(char.IsWhiteSpace)) return false;
        var group = name.AsSpan(dash + 1);
        var groupHasLetter = false;
        foreach (var c in group)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_') return false;
            groupHasLetter |= char.IsAsciiLetter(c);
        }
        return groupHasLetter && name.Split(['.', '_', '-'], StringSplitOptions.RemoveEmptyEntries).Length >= 3;
    }

    internal static bool SkipDirectory(string name) => SkipDirs.Contains(name) ||
        NukedDirectory.IsMatch(name) ||
        (name.StartsWith('[') && name.EndsWith(']') && StatusDirectory.IsMatch(name));

    // SITE SEARCH support: null = not probed yet, true/false = probed
    private bool? _siteSearchSupported;

    // Cached index
    private List<IndexEntry> _index = [];
    private DateTime? _indexBuiltAtUtc;
    private List<SearchListingFailure> _indexFailures = [];
    private readonly Lock _indexLock = new();
    private CancellationTokenSource? _indexerCts;
    private Task? _indexerTask;

    public FtpSearchService(FtpConnectionPool pool, SearchConfig searchConfig)
    {
        _pool = pool;
        _searchConfig = searchConfig;
    }

    internal FtpSearchService(FtpConnectionPool pool, SearchConfig searchConfig, DirectoryLister list,
        Func<string, CancellationToken, Task<FtpReply>> siteSearch,
        TimeSpan? deadline = null, TimeSpan? retryDelay = null) : this(pool, searchConfig)
    {
        _listOverride = list;
        _siteSearchOverride = siteSearch;
        _searchDeadline = deadline ?? _searchDeadline;
        _listingRetryDelay = retryDelay ?? _listingRetryDelay;
    }

    private static string Normalize(string s) => s.Replace('.', ' ').Replace('_', ' ').Replace('-', ' ');

    internal static bool MatchesQuery(string name, string query)
    {
        return MatchesNormalizedName(Normalize(name), QueryTerms(query));
    }

    private static string[] QueryTerms(string query) =>
        Normalize(query).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static bool MatchesNormalizedName(string normalized, IReadOnlyList<string> terms)
    {
        for (var index = 0; index < terms.Count; index++)
            if (!normalized.Contains(terms[index], StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private sealed class SearchRun
    {
        // Ending a race stops traversal/borrowing, but must not tear an FTP
        // command already on the wire. Caller cancellation and deadlines still do.
        public CancellationToken OperationToken;
        public readonly List<SearchResult> Results = [];
        public string Method = "Search";
        public DateTime? IndexBuiltAtUtc;
        public bool Partial;
        public bool TimedOut;
        public bool Truncated;
        public readonly List<SearchListingFailure> Failures = [];
        public SearchOutcome Snapshot() => new(Results.AsReadOnly(), Method, IndexBuiltAtUtc,
            Partial || TimedOut || Truncated || Failures.Count > 0, TimedOut, Truncated, Failures.AsReadOnly());
    }

    internal static string NormalizeSearchRoot(string path)
    {
        var root = path.TrimEnd('/');
        return root.Length == 0 ? "/" : root;
    }

    private static string SanitizeFtpInput(string input) =>
        input.Replace("\r", "").Replace("\n", "").Replace("\0", "");

    /// <summary>
    /// Compatibility entry point for callers that only consume matches. Interactive
    /// callers use SearchDetailed so a failed listing cannot masquerade as no matches.
    /// </summary>
    public async Task<List<SearchResult>> Search(string keyword, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        return (await SearchDetailed(keyword, progress, ct)).Results.ToList();
    }

    public Task<SearchOutcome> SearchDetailed(string keyword, IProgress<string>? progress = null, CancellationToken ct = default)
        => SearchDetailed(keyword, progress, ct, CancellationToken.None);

    /// <summary>Stop before the next command while allowing the current command to drain.</summary>
    public async Task<SearchOutcome> SearchDetailed(string keyword, IProgress<string>? progress,
        CancellationToken ct, CancellationToken stopToken)
    {
        ct.ThrowIfCancellationRequested();
        stopToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_searchDeadline);
        using var traversal = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, stopToken);
        var run = new SearchRun { OperationToken = deadline.Token };
        try
        {
            await SearchOnce(keyword, progress, run, traversal.Token);
            traversal.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !stopToken.IsCancellationRequested)
        {
            run.TimedOut = true;
            progress?.Report($"Search time limit reached — {run.Results.Count} partial result(s)");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            run.Partial = true;
            run.Failures.Add(new("Search", ex.Message));
            Log.Warning(ex, "Search incomplete for {Keyword}", keyword);
        }
        ct.ThrowIfCancellationRequested();
        stopToken.ThrowIfCancellationRequested();
        return run.Snapshot();
    }

    private async Task SearchOnce(string keyword, IProgress<string>? progress, SearchRun run, CancellationToken ct)
    {
        var method = _searchConfig.Method;

        if (method == SearchMethod.SiteSearch)
        {
            await RunSiteSearch(keyword, progress, run, ct);
            return;
        }

        if (method == SearchMethod.CachedIndex)
        {
            RunIndexSearch(keyword, progress, run);
            return;
        }

        if (method == SearchMethod.LiveCrawl)
        {
            await RunLiveCrawl(keyword, progress, run, ct);
            return;
        }

        // Auto mode: try SITE SEARCH → Cached Index → Live Crawl
        await RunAutoSearch(keyword, progress, run, ct);
    }

    private async Task RunAutoSearch(string keyword, IProgress<string>? progress, SearchRun run, CancellationToken ct)
    {
        // A populated cache needs no login. This fast path matters when downloads
        // occupy the sole main-pool session; do not spend 15s probing SITE first.
        if (RunIndexSearch(keyword, progress, run) > 0) return;
        run.IndexBuiltAtUtc = null;
        run.Failures.Clear();
        run.Partial = false;

        // 1. Try SITE SEARCH if not known to be unsupported
        if (_siteSearchSupported != false)
        {
            run.Method = "SITE SEARCH";
            var site = await TrySiteSearch(keyword, progress, ct, run.OperationToken);
            if (site is { Results.Count: > 0 })
            {
                run.Method = "SITE SEARCH";
                run.Results.AddRange(site.Results);
                run.Truncated = site.Truncated;
                if (!site.Truncated) return;
            }
        }

        // Empty, unrecognized or truncated server replies need live evidence.
        await RunLiveCrawl(keyword, progress, run, ct);
    }

    #region SITE SEARCH

    private static readonly Regex SiteSearchLineRegex = new(
        @"^(?<path>/\S+)\s+\((?<files>\d+)F/(?<size>[\d.]+)(?<unit>[KMG]?)/(?<age>[^)]+)\)",
        RegexOptions.Compiled);

    private async Task RunSiteSearch(string keyword, IProgress<string>? progress, SearchRun run, CancellationToken ct)
    {
        run.Method = "SITE SEARCH";
        var site = await TrySiteSearch(keyword, progress, ct, run.OperationToken);
        if (site is { Results.Count: > 0 })
        {
            run.Results.AddRange(site.Results);
            run.Truncated = site.Truncated;
            if (!site.Truncated) return;
        }

        progress?.Report("Checking live directories for additional matches...");
        await RunLiveCrawl(keyword, progress, run, ct);
    }

    private sealed record SiteSearchMatches(List<SearchResult> Results, bool Truncated);

    /// <summary>
    /// Tries SITE SEARCH. Returns null if the server doesn't support it.
    /// Only marks unsupported on explicit server rejection (5xx), not on transient errors.
    /// </summary>
    private async Task<SiteSearchMatches?> TrySiteSearch(string keyword, IProgress<string>? progress,
        CancellationToken ct, CancellationToken operationToken)
    {
        try
        {
            progress?.Report("Trying SITE SEARCH...");
            // glftpd search scripts disagree about multiword/phrase semantics. Ask
            // for one discriminating term and apply the same AND matcher locally.
            var query = Normalize(keyword).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .OrderByDescending(term => term.Length).FirstOrDefault() ?? "";
            var reply = await ExecuteSiteSearch(SanitizeFtpInput(query), ct, operationToken);
            ct.ThrowIfCancellationRequested();

            if (!reply.Success)
            {
                Log.Debug("SITE SEARCH returned {Code}: {Message}", reply.Code, reply.Message);
                // Only permanently disable on 5xx (command not recognized) — not transient errors
                if (reply.Code is "500" or "502")
                    _siteSearchSupported = false;
                return null;
            }

            _siteSearchSupported = true;
            var raw = reply.InfoMessages ?? reply.Message ?? "";
            Log.Debug("SITE SEARCH raw response ({Length} chars): {Response}", raw.Length, raw);
            var results = ParseSiteSearchResponse(raw);
            var truncated = results.Count >= MaxResults;
            var terms = QueryTerms(keyword);
            results = results.Where(r => MatchesNormalizedName(Normalize(r.ReleaseName), terms)).ToList();
            progress?.Report($"{results.Count} result(s) from SITE SEARCH");
            return new(results, truncated);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Borrow timed out — pool is busy, don't block search
            Log.Debug("SITE SEARCH skipped — pool borrow timed out");
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Connection errors are transient — don't permanently disable SITE SEARCH
            Log.Debug(ex, "SITE SEARCH failed (transient), will retry next search");
            return null;
        }
    }

    private async Task<FtpReply> ExecuteSiteSearch(string query, CancellationToken ct, CancellationToken operationToken)
    {
        ct.ThrowIfCancellationRequested();
        if (_siteSearchOverride != null) return await _siteSearchOverride(query, operationToken);
        using var borrowCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        borrowCts.CancelAfter(BorrowTimeout);
        await using var conn = await _pool.Borrow(borrowCts.Token);
        ct.ThrowIfCancellationRequested();
        try
        {
            var reply = await conn.Client.Execute($"SITE SEARCH {query}", operationToken);
            if (reply.Code == "421") conn.Poison("SITE SEARCH server closed control connection (421)");
            return reply;
        }
        catch (OperationCanceledException)
        {
            conn.Poison("SITE SEARCH cancelled after command started");
            throw;
        }
        catch (Exception ex) when (!IsCleanCommandRejection(ex))
        {
            conn.Poison($"SITE SEARCH transport failure: {ex.GetType().Name}");
            throw;
        }
    }

    internal static List<SearchResult> ParseSiteSearchResponse(string response)
    {
        var results = new List<SearchResult>();
        foreach (var line in response.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // Strip FTP continuation prefix (e.g. "200-")
            var text = line.Trim();
            if (text.Length > 4 && text[3] == '-' && char.IsDigit(text[0]))
                text = text[4..].Trim();

            var match = SiteSearchLineRegex.Match(text);
            if (!match.Success) continue;

            var path = match.Groups["path"].Value;
            var name = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;
            if (SkipDirectory(name)) continue;
            var category = ExtractCategoryFromFullPath(path);

            var sizeVal = double.TryParse(match.Groups["size"].Value, out var sv) ? sv : 0;
            var unit = match.Groups["unit"].Value;
            long sizeBytes = unit switch
            {
                "K" => (long)(sizeVal * 1024),
                "M" => (long)(sizeVal * 1024 * 1024),
                "G" => (long)(sizeVal * 1024 * 1024 * 1024),
                _ => (long)sizeVal
            };

            results.Add(new SearchResult
            {
                ReleaseName = name,
                Category = category,
                RemotePath = path,
                Size = sizeBytes
            });

            if (results.Count >= MaxResults) break;
        }
        return results;
    }

    private static string ExtractCategoryFromFullPath(string fullPath)
    {
        // "/site/TV/Some.Show" → "TV"
        var parts = fullPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 ? parts[^2] : parts.Length >= 2 ? parts[0] : "";
    }

    #endregion

    #region Cached Index

    private int RunIndexSearch(string keyword, IProgress<string>? progress, SearchRun run)
    {
        List<IndexEntry> snapshot;
        lock (_indexLock)
        {
            snapshot = _index;
            run.IndexBuiltAtUtc = _indexBuiltAtUtc;
            run.Failures.AddRange(_indexFailures);
        }
        run.Method = "Cached index";

        if (snapshot.Count == 0)
        {
            progress?.Report("Index is empty — building in background...");
            run.Partial = run.IndexBuiltAtUtc == null || run.Failures.Count > 0;
            return 0;
        }

        progress?.Report("Searching cached index...");
        var results = FilterIndex(snapshot, keyword, out var truncated);
        run.Results.AddRange(results);
        run.Truncated = truncated;
        run.Partial = run.Failures.Count > 0;
        progress?.Report($"{results.Count} result(s) from cached index");
        return results.Count;
    }

    private static List<SearchResult> FilterIndex(List<IndexEntry> index, string keyword, out bool truncated)
    {
        truncated = false;
        var results = new List<SearchResult>();
        var terms = QueryTerms(keyword);

        foreach (var entry in index)
        {
            if (MatchesNormalizedName(entry.NormalizedName, terms))
            {
                if (results.Count >= MaxResults) { truncated = true; break; }
                results.Add(new SearchResult
                {
                    ReleaseName = entry.Name,
                    Category = entry.Category,
                    RemotePath = entry.Path,
                    Size = entry.Size,
                    Modified = entry.Modified
                });
            }
        }

        return results;
    }

    /// <summary>
    /// Fully rebuilds the in-memory index by crawling all search paths.
    /// Borrows a connection per-listing to avoid holding pool connections for long crawls.
    /// </summary>
    public async Task RefreshIndex(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var entries = new List<IndexEntry>();
        var report = new IndexCrawlReport();
        progress?.Report("Building search index...");

        foreach (var searchPath in _searchConfig.SearchPaths)
        {
            if (report.Aborted) break;
            var root = NormalizeSearchRoot(searchPath);
            await CrawlForIndex(ListViaPool, root, root, 0, _searchConfig.MaxDepth, entries, report, ct);
        }

        List<IndexEntry> previous;
        lock (_indexLock)
            previous = _index;
        var merged = MergeAfterCrawl(entries, previous, report);
        lock (_indexLock)
        {
            _index = merged;
            if (!report.Aborted || previous.Count == 0) _indexBuiltAtUtc = DateTime.UtcNow;
            _indexFailures = report.Failures.ToList();
        }

        progress?.Report($"Index built: {merged.Count} entries");
        if (report.Aborted)
            Log.Warning("Search index crawl aborted after {Failed} consecutive listing failures, last {Path}: {Error} — " +
                        "kept the previous index ({Count} entries) instead of publishing a partial one",
                report.ConsecutiveFailures, report.LastFailedPath, report.LastError, merged.Count);
        else if (report.Failed > 0)
            Log.Information("Search index built with {Count} entries — {Failed} directory listing(s) failed, first {Path}: {Error}",
                merged.Count, report.Failed, report.FirstFailedPath, report.FirstError);
        else
            Log.Information("Search index built with {Count} entries", merged.Count);
    }

    /// <summary>
    /// A failed listing says nothing about what the directory holds, so the previous
    /// index's entries under it are carried forward rather than dropped. An aborted
    /// crawl (transport down) keeps the previous index whole. On 2026-10-07 a burst of
    /// local ephemeral-port exhaustion failed 1151 listings in one crawl and the
    /// published index lost ~1100 entries for an hour.
    /// </summary>
    internal static List<IndexEntry> MergeAfterCrawl(List<IndexEntry> fresh, List<IndexEntry> previous, IndexCrawlReport report)
    {
        if (report.Aborted && previous.Count > 0)
            return previous;
        if (report.FailedPaths.Count == 0 || previous.Count == 0)
            return fresh;

        var failed = new HashSet<string>(report.FailedPaths, StringComparer.Ordinal);
        // Accept the literal root and its previously trimmed empty spelling.
        // Neither appears in the named-ancestor walk below; both cover every
        // previous index entry.
        var rootFailed = failed.Contains("") || failed.Contains("/");
        var present = new HashSet<string>(fresh.Select(e => e.Path), StringComparer.Ordinal);
        var merged = new List<IndexEntry>(fresh);
        foreach (var entry in previous)
        {
            if (present.Contains(entry.Path)) continue;
            if (rootFailed)
            {
                merged.Add(entry);
                continue;
            }
            for (var cut = entry.Path.LastIndexOf('/'); cut > 0; cut = entry.Path.LastIndexOf('/', cut - 1))
            {
                if (!failed.Contains(entry.Path[..cut])) continue;
                merged.Add(entry);
                break;
            }
        }
        return merged;
    }

    internal delegate Task<FtpListItem[]> DirectoryLister(string path, CancellationToken ct);

    /// <summary>How many listings a crawl lost, and the first one, for the build summary line.</summary>
    internal sealed class IndexCrawlReport
    {
        /// <summary>
        /// Consecutive failed listings that mean the transport, not a directory, is
        /// broken. Each failed listing borrows (and may try to log in) again, so
        /// continuing turns a local fault into a server-side login-cap refusal.
        /// </summary>
        public const int MaxConsecutiveFailures = 5;

        public int Failed { get; private set; }
        public int ConsecutiveFailures { get; private set; }
        public bool Aborted => ConsecutiveFailures >= MaxConsecutiveFailures;
        public List<string> FailedPaths { get; } = [];
        public List<SearchListingFailure> Failures { get; } = [];
        public bool Truncated { get; set; }
        public string? FirstFailedPath { get; private set; }
        public string? FirstError { get; private set; }
        public string? LastFailedPath { get; private set; }
        public string? LastError { get; private set; }

        public void RecordSuccess() => ConsecutiveFailures = 0;

        public void Record(string path, Exception ex)
        {
            Failed++;
            ConsecutiveFailures++;
            FailedPaths.Add(path);
            Failures.Add(new(path, ex.Message));
            LastFailedPath = path;
            LastError = ex.Message;
            if (FirstFailedPath != null) return;
            FirstFailedPath = path;
            FirstError = ex.Message;
        }
    }

    private async Task<FtpListItem[]> ListViaPool(string path, CancellationToken ct)
    {
        if (_listOverride != null) return await _listOverride(path, ct);
        // Borrow per-listing so we don't hold a connection for the entire crawl
        await using var conn = await _pool.Borrow(ct);
        return await ListBorrowed(conn, path, ct);
    }

    /// <summary>
    /// Crawls <paramref name="path"/> to <paramref name="maxDepth"/>. A listing that
    /// fails (a release the site moved between the parent and child listings, a
    /// denied directory) costs only its own subtree and is recorded in
    /// <paramref name="report"/>; before v3.10.112 it aborted the whole search root,
    /// and the failure was logged at Debug against an Information sink, so the hourly
    /// pair of quarantined connections it caused had no visible explanation.
    /// Cancellation still propagates.
    /// </summary>
    internal static async Task CrawlForIndex(
        DirectoryLister list, string path, string searchRoot,
        int depth, int maxDepth, List<IndexEntry> entries, IndexCrawlReport report, CancellationToken ct)
    {
        if (report.Aborted) return;

        FtpListItem[] items;
        try
        {
            items = await list(path, ct);
            report.RecordSuccess();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            report.Record(path, ex);
            return;
        }

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            if (report.Aborted) return;
            if (item.Type != FtpObjectType.Directory) continue;
            if (SkipDirectory(item.Name)) continue;

            var category = ExtractCategory(searchRoot, item.FullName);

            entries.Add(new IndexEntry
            {
                Name = item.Name,
                NormalizedName = Normalize(item.Name),
                Path = item.FullName,
                Category = category,
                Size = item.Size,
                Modified = item.Modified
            });

            if (depth < maxDepth && !IsReleaseDirectory(item.Name))
                await CrawlForIndex(list, item.FullName, searchRoot, depth + 1, maxDepth, entries, report, ct);
        }
    }

    #endregion

    #region Indexer Lifecycle

    public void StartIndexer()
    {
        if (_searchConfig.Method == SearchMethod.LiveCrawl)
            return; // No indexer needed for pure live crawl

        _indexerCts = new CancellationTokenSource();
        _indexerTask = IndexerLoop(_indexerCts.Token);
    }

    public void StopIndexer()
    {
        _indexerCts?.Cancel();
    }

    public async Task StopIndexerAsync(TimeSpan timeout)
    {
        _indexerCts?.Cancel();
        if (_indexerTask != null)
        {
            try { await _indexerTask.WaitAsync(timeout); }
            catch { }
        }
    }

    private async Task IndexerLoop(CancellationToken ct)
    {
        // Initial build
        try
        {
            await RefreshIndex(progress: null, ct);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            Log.Warning(ex, "Initial search index build failed");
        }

        var interval = TimeSpan.FromMinutes(Math.Max(1, _searchConfig.IndexCacheMinutes));

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, ct);
                await RefreshIndex(progress: null, ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Log.Warning(ex, "Search index refresh failed");
            }
        }
    }

    #endregion

    #region Live Crawl

    private async Task RunLiveCrawl(string keyword, IProgress<string>? progress, SearchRun run, CancellationToken ct)
    {
        run.Method = run.Results.Count > 0 ? "SITE SEARCH + Live crawl" : "Live crawl";
        var results = run.Results;
        var report = new IndexCrawlReport();
        var terms = QueryTerms(keyword);

        progress?.Report("Live searching...");

        try
        {
            // Search paths sequentially to avoid borrowing multiple connections at once
            for (var rootIndex = 0; rootIndex < _searchConfig.SearchPaths.Count; rootIndex++)
            {
                var path = _searchConfig.SearchPaths[rootIndex];
                ct.ThrowIfCancellationRequested();
                if (results.Count >= MaxResults) { report.Truncated = true; break; }
                await SearchRecursive((listingPath, token) => ListForLiveSearch(listingPath, token, run.OperationToken),
                    NormalizeSearchRoot(path), keyword,
                    0, _searchConfig.MaxDepth, results, progress, ct, report, queryTerms: terms);
                if (results.Count >= MaxResults || report.Aborted)
                {
                    if (rootIndex + 1 < _searchConfig.SearchPaths.Count) report.Truncated = true;
                    break;
                }
            }

            if (results.Count > MaxResults)
                results.RemoveRange(MaxResults, results.Count - MaxResults);
        }
        finally
        {
            run.Failures.AddRange(report.Failures);
            run.Truncated |= report.Truncated;
            run.Partial |= report.Aborted;
            if (report.Failed > 0)
                Log.Warning("Live search returned partial results: {Failed} listing(s) failed; last {Path}: {Error}",
                    report.Failed, report.LastFailedPath, report.LastError);
            progress?.Report($"{results.Count} result(s) from live crawl" +
                (report.Failed > 0 ? $" ({report.Failed} directory listing(s) failed)" : ""));
        }
    }

    private Task<FtpListItem[]> ListForLiveSearch(string path, CancellationToken ct, CancellationToken operationToken) =>
        ListWithTransientRetry((listingPath, token) => ListForLiveSearchOnce(listingPath, token, operationToken),
            path, ct, _listingRetryDelay);

    private async Task<FtpListItem[]> ListForLiveSearchOnce(string path, CancellationToken ct, CancellationToken operationToken)
    {
        ct.ThrowIfCancellationRequested();
        if (_listOverride != null) return await _listOverride(path, operationToken);
        using var borrowCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        borrowCts.CancelAfter(BorrowTimeout);
        PooledConnection conn;
        try { conn = await _pool.Borrow(borrowCts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("FTP connection busy; timed out waiting for a free connection");
        }
        await using var borrowed = conn;
        ct.ThrowIfCancellationRequested();
        return await ListBorrowed(conn, path, operationToken);
    }

    internal static async Task<FtpListItem[]> ListWithTransientRetry(
        DirectoryLister list, string path, CancellationToken ct, TimeSpan retryDelay)
    {
        try { return await list(path, ct); }
        catch (Exception ex) when (!ct.IsCancellationRequested && IsTransientListingFailure(ex))
        {
            // A single fair second borrow lets a busy download return its login.
            // Clean 550/permission/path denials are never retried.
            await Task.Delay(retryDelay, ct);
            return await list(path, ct);
        }
    }

    private static bool IsTransientListingFailure(Exception ex) =>
        ex is OperationCanceledException or TimeoutException or System.Net.Sockets.SocketException
        || (ex is IOException && !FtpCommandRejection.IsClean(ex)
            && !Regex.IsMatch(ex.Message, @"\b5\d{2}\b", RegexOptions.CultureInvariant));

    private static bool IsCleanCommandRejection(Exception ex) => FtpCommandRejection.IsClean(ex)
        || ex is FtpCommandException command && FtpCommandRejection.LeavesChannelInSync(command.CompletionCode);

    private async Task<FtpListItem[]> ListBorrowed(PooledConnection conn, string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try { return await ListDirect(conn.Client, path, ct); }
        catch (OperationCanceledException)
        {
            // Borrow cancellation never reaches this scope. Once LIST began, a
            // cancellation can leave data/final replies unread on the control stream.
            conn.Poison($"search LIST cancelled after command started: {path}");
            throw;
        }
        catch (Exception ex) when (!IsCleanCommandRejection(ex))
        {
            conn.Poison($"search LIST transport failure: {path}: {ex.GetType().Name}");
            throw;
        }
    }

    internal static async Task SearchRecursive(
        DirectoryLister list, string path, string keyword,
        int depth, int maxDepth, List<SearchResult> results,
        IProgress<string>? progress, CancellationToken ct,
        IndexCrawlReport? report = null, string? searchRoot = null, string[]? queryTerms = null)
    {
        ct.ThrowIfCancellationRequested();
        report ??= new IndexCrawlReport();
        searchRoot ??= path;
        queryTerms ??= QueryTerms(keyword);
        if (results.Count >= MaxResults) { report.Truncated = true; return; }
        if (report.Aborted) return;

        progress?.Report($"Scanning {path}...");

        // Borrow per-listing so we don't hold a connection for the entire crawl
        FtpListItem[] items;
        try
        {
            items = await list(path, ct);
            report.RecordSuccess();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            report.Record(path, ex);
            return;
        }

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            if (results.Count >= MaxResults) { report.Truncated = true; return; }
            if (report.Aborted) return;
            if (item.Type != FtpObjectType.Directory) continue;
            if (SkipDirectory(item.Name)) continue;

            if (MatchesNormalizedName(Normalize(item.Name), queryTerms)
                && !results.Any(r => string.Equals(r.RemotePath, item.FullName, StringComparison.Ordinal)))
            {
                var category = ExtractCategory(searchRoot, item.FullName);
                results.Add(new SearchResult
                {
                    ReleaseName = item.Name,
                    Category = category,
                    RemotePath = item.FullName,
                    Size = item.Size,
                    Modified = item.Modified
                });
            }

            if (depth < maxDepth && !IsReleaseDirectory(item.Name))
                await SearchRecursive(list, item.FullName, keyword, depth + 1, maxDepth, results, progress, ct, report, searchRoot, queryTerms);
        }
    }

    #endregion

    #region Helpers

    private static string ExtractCategory(string searchRoot, string fullPath)
    {
        var relative = fullPath;
        if (relative.StartsWith(searchRoot, StringComparison.OrdinalIgnoreCase))
            relative = relative[searchRoot.Length..];
        relative = relative.TrimStart('/');

        var slash = relative.IndexOf('/');
        return slash > 0 ? relative[..slash] : "";
    }

    private async Task<FtpListItem[]> ListDirect(AsyncFtpClient client, string remotePath, CancellationToken ct)
    {
        if (_pool.UseCpsv)
            return await CpsvDataHelper.ListDirectory(client, remotePath, _pool.ControlHost, ct);

        return await FtpListing.GetListing(client, remotePath, ct);
    }

    #endregion

    public async Task<List<FtpListItem>> GetReleaseFiles(string remotePath, CancellationToken ct = default)
    {
        await using var conn = await _pool.Borrow(ct);
        var items = await ListDirect(conn.Client, remotePath, ct);
        return items.Where(i => i.Type == FtpObjectType.File).ToList();
    }

    public void Dispose()
    {
        StopIndexer();
        _indexerCts?.Dispose();
        GC.SuppressFinalize(this);
    }

    internal sealed class IndexEntry
    {
        public string Name { get; set; } = "";
        public string NormalizedName { get; set; } = "";
        public string Path { get; set; } = "";
        public string Category { get; set; } = "";
        public long Size { get; set; }
        public DateTime Modified { get; set; }
    }
}
