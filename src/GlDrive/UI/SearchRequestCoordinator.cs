using GlDrive.Downloads;

namespace GlDrive.UI;

internal sealed class SearchRequestCoordinator : IDisposable
{
    private SearchRequest? _current;

    internal SearchRequest Begin(string query)
    {
        var request = new SearchRequest(query.Trim());
        Interlocked.Exchange(ref _current, request)?.Cancel();
        return request;
    }

    internal bool IsCurrent(SearchRequest request) => ReferenceEquals(Volatile.Read(ref _current), request);
    internal bool CanPublish(SearchRequest request) => IsCurrent(request) && !request.IsFinished && !request.Token.IsCancellationRequested;
    internal IProgress<string> ProgressFor(SearchRequest request, Action<string> publish) => new Progress<string>(message =>
    {
        // Progress captures the UI context. Check when its queued callback runs,
        // not when the worker reports, because a new query may have started meanwhile.
        if (CanPublish(request)) publish(message);
    });
    internal void Cancel() => Volatile.Read(ref _current)?.Cancel();
    public void Dispose() => Interlocked.Exchange(ref _current, null)?.Cancel();

    internal static async Task<SearchSiteResult> SearchSiteAsync(SearchRequest request, string serverName,
        Func<string, IProgress<string>?, CancellationToken, Task<SearchOutcome>> search,
        IProgress<string>? progress = null)
    {
        try
        {
            var outcome = await search(request.Query, progress, request.Token);
            request.Token.ThrowIfCancellationRequested();
            return new SearchSiteResult(serverName, outcome, null);
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested)
        {
            return new SearchSiteResult(serverName, null, null);
        }
        catch (Exception ex)
        {
            // One unavailable site must not discard matches returned by other sites.
            return new SearchSiteResult(serverName, null, ex.Message);
        }
    }

    internal static string Describe(IReadOnlyList<SearchSiteResult> sites, int serverCount, DateTime nowUtc, bool cancelled = false)
    {
        var count = sites.Sum(site => site.Outcome?.Results.Count ?? 0);
        var incomplete = sites.Count(site => site.Error != null || site.Outcome is { IsPartial: true } or { TimedOut: true });
        var summary = cancelled ? $"Search cancelled — {count} result(s) retained"
            : sites.Count < serverCount ? $"{count} result(s) — {sites.Count}/{serverCount} servers checked"
            : incomplete > 0 ? $"{count} result(s) — {incomplete}/{serverCount} server(s) unavailable or partially searched"
            : $"{count} result(s) across {serverCount} server(s)";
        var details = new List<string>();
        foreach (var site in sites)
        {
            if (site.Error != null) { details.Add($"{site.ServerName}: unavailable — {site.Error}"); continue; }
            if (site.Outcome is not { } result) continue;
            var notes = new List<string>();
            if (result.TimedOut) notes.Add("timed out");
            if (result.IsPartial) notes.Add(result.Failures.Count > 0
                ? $"partial search, {result.Failures.Count} listing failure(s)" : "partial search");
            if (result.Failures.Count > 0) notes.Add(result.Failures[0].Message);
            if (result.Truncated) notes.Add("result limit reached");
            if (result.IndexBuiltAtUtc is { } built)
            {
                var minutes = Math.Max(0, (nowUtc - built).TotalMinutes);
                notes.Add(minutes < 1 ? "cached index under 1m old" : $"cached index {(int)minutes}m old");
            }
            if (notes.Count > 0) details.Add($"{site.ServerName}: {string.Join(", ", notes)}");
        }
        return details.Count == 0 ? summary : summary + " — " + string.Join("; ", details);
    }

    internal static string DescribeMedia(int tmdbCount, int torrentCount, string ftpSummary)
    {
        var parts = new List<string>();
        if (tmdbCount > 0) parts.Add($"{tmdbCount} TMDb");
        if (torrentCount > 0) parts.Add($"{torrentCount} torrent");
        parts.Add("FTP: " + ftpSummary);
        return string.Join("; ", parts);
    }
}

internal sealed class SearchRequest : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private int _finished;
    internal string Query { get; }
    internal CancellationToken Token { get; }
    internal bool IsFinished => Volatile.Read(ref _finished) != 0;
    internal SearchRequest(string query) { Query = query; Token = _cts.Token; }
    internal void Cancel()
    {
        try { _cts.Cancel(); }
        catch (ObjectDisposedException) { }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _finished, 1) == 0) _cts.Dispose();
    }
}

internal sealed record SearchSiteResult(string ServerName, SearchOutcome? Outcome, string? Error);
