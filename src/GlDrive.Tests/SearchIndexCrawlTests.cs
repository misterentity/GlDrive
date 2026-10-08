using System.IO;
using FluentFTP;
using GlDrive.Downloads;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// The hourly search-index crawl lists every release directory under each search
/// root. One failed listing (a release the site moved between the parent listing
/// and the child listing, or a denied directory) threw out of the recursion and
/// aborted the WHOLE search root — and the failure was logged at Debug, which the
/// Information sink never writes. A failed subdirectory must cost only its own
/// subtree, and the build must report how many listings it lost.
/// </summary>
public sealed class SearchIndexCrawlTests
{
    private static FtpListItem Dir(string fullName) => new()
    {
        Name = fullName[(fullName.LastIndexOf('/') + 1)..],
        FullName = fullName,
        Type = FtpObjectType.Directory
    };

    private static FtpSearchService.DirectoryLister Lister(Dictionary<string, FtpListItem[]> tree, params string[] failing) =>
        (path, _) =>
        {
            if (failing.Contains(path)) throw new IOException($"LIST failed: 550 {path}: No such file or directory.");
            return Task.FromResult(tree.TryGetValue(path, out var items) ? items : []);
        };

    [Fact]
    public async Task A_failed_subdirectory_costs_only_its_own_subtree()
    {
        var tree = new Dictionary<string, FtpListItem[]>
        {
            ["/TV"] = [Dir("/TV/Show.A"), Dir("/TV/Show.B"), Dir("/TV/Show.C")],
            ["/TV/Show.A"] = [Dir("/TV/Show.A/Sample")],
            ["/TV/Show.C"] = [Dir("/TV/Show.C/Subs")],
        };
        var entries = new List<FtpSearchService.IndexEntry>();
        var report = new FtpSearchService.IndexCrawlReport();

        await FtpSearchService.CrawlForIndex(Lister(tree, "/TV/Show.B"), "/TV", "/TV", 0, 2, entries, report, CancellationToken.None);

        Assert.Equal(["/TV/Show.A", "/TV/Show.A/Sample", "/TV/Show.B", "/TV/Show.C", "/TV/Show.C/Subs"],
            entries.Select(e => e.Path).Order().ToArray());
        Assert.Equal(1, report.Failed);
        Assert.Equal("/TV/Show.B", report.FirstFailedPath);
        Assert.Contains("550", report.FirstError);
    }

    [Fact]
    public async Task A_failed_root_is_recorded_not_thrown()
    {
        var entries = new List<FtpSearchService.IndexEntry>();
        var report = new FtpSearchService.IndexCrawlReport();

        await FtpSearchService.CrawlForIndex(Lister([], "/archive"), "/archive", "/archive", 0, 2, entries, report, CancellationToken.None);

        Assert.Empty(entries);
        Assert.Equal(1, report.Failed);
        Assert.Equal("/archive", report.FirstFailedPath);
    }

    [Fact]
    public async Task Cancellation_still_propagates()
    {
        var entries = new List<FtpSearchService.IndexEntry>();
        var report = new FtpSearchService.IndexCrawlReport();
        FtpSearchService.DirectoryLister lister = (_, _) => throw new OperationCanceledException();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            FtpSearchService.CrawlForIndex(lister, "/TV", "/TV", 0, 2, entries, report, CancellationToken.None));
        Assert.Equal(0, report.Failed);
    }

    [Fact]
    public void Index_build_reports_lost_listings_at_Information_not_Debug()
    {
        var source = CpsvDataCommandRejectionTests.ReadSource("Downloads", "FtpSearchService.cs");
        var refresh = source.IndexOf("public async Task RefreshIndex(", StringComparison.Ordinal);
        var crawl = source.IndexOf("CrawlForIndex(", refresh + 30, StringComparison.Ordinal);
        var body = source[refresh..source.IndexOf("#endregion", crawl, StringComparison.Ordinal)];
        Assert.DoesNotContain("Log.Debug(ex, \"Index crawl failed", body);
        Assert.Contains("report.Failed", body);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Status_directories_are_neither_results_nor_crawl_targets(bool live)
    {
        string[] markers = [
            "[Z] - ( 1591M 17F - COMPLETE ) - [Z]",
            "[###:::::::::::] - 27% Complete - [site]",
            "[ Incomplete ]", "[site] - ( IN-COMPLETE ) - [site]",
            "[NUKED]-Show.S01E02.1080p-GRP", "[ nuked ] - Show.S01E03-GRP", "[ NUKED ]"
        ];
        string[] real = ["Show.S01.COMPLETE.1080p-GRP", "[Group] Show.S01", "Complete", "Sample", "Subs",
            "Nuked.2026.1080p-GRP", "Show.NUKED.S01-GRP", "[NUKEDGroup]-Show.S01-GRP"];
        var tree = new Dictionary<string, FtpListItem[]>
        {
            ["/TV"] = markers.Concat(real).Select(n => Dir("/TV/" + n)).ToArray()
        };
        var visited = new List<string>();
        FtpSearchService.DirectoryLister list = (path, _) =>
        {
            visited.Add(path);
            if (markers.Any(n => path == "/TV/" + n))
                throw new IOException("LIST failed: 550 Bad directory components");
            return Task.FromResult(tree.TryGetValue(path, out var items) ? items : []);
        };
        string[] found;
        if (live)
        {
            var results = new List<SearchResult>();
            await FtpSearchService.SearchRecursive(list, "/TV", "", 0, 2, results, null, CancellationToken.None);
            found = results.Select(r => r.ReleaseName).ToArray();
        }
        else
        {
            var entries = new List<FtpSearchService.IndexEntry>();
            var report = new FtpSearchService.IndexCrawlReport();
            await FtpSearchService.CrawlForIndex(list, "/TV", "/TV", 0, 2, entries, report, CancellationToken.None);
            Assert.Equal(0, report.Failed);
            found = entries.Select(e => e.Name).ToArray();
        }
        Assert.Equal(real, found);
        Assert.Equal(new[] { "/TV" }.Concat(real.Select(n => "/TV/" + n)), visited);
    }

    private static FtpSearchService.IndexEntry Entry(string path) => new() { Name = path[(path.LastIndexOf('/') + 1)..], Path = path };

    [Fact]
    public async Task Consecutive_transport_failures_abort_the_crawl_instead_of_hammering_the_pool()
    {
        // 2026-10-07: local port exhaustion failed every LIST; the crawl made 1151 attempts,
        // the pool's re-logins hit zephyr's login cap and tripped a 90s BNC cooldown.
        var tree = new Dictionary<string, FtpListItem[]>
        {
            ["/TV"] = Enumerable.Range(0, 50).Select(i => Dir($"/TV/Show.{i:D2}")).ToArray()
        };
        var calls = 0;
        FtpSearchService.DirectoryLister list = (path, _) =>
        {
            calls++;
            if (path != "/TV") throw new IOException("Only one usage of each socket address is normally permitted.");
            return Task.FromResult(tree[path]);
        };
        var entries = new List<FtpSearchService.IndexEntry>();
        var report = new FtpSearchService.IndexCrawlReport();

        await FtpSearchService.CrawlForIndex(list, "/TV", "/TV", 0, 2, entries, report, CancellationToken.None);

        Assert.True(report.Aborted);
        Assert.Equal(1 + FtpSearchService.IndexCrawlReport.MaxConsecutiveFailures, calls);
        Assert.Equal("/TV/Show.04", report.LastFailedPath);
    }

    [Fact]
    public async Task Sporadic_failures_separated_by_successes_do_not_abort()
    {
        var names = Enumerable.Range(0, 20).Select(i => $"/TV/Show.{i:D2}").ToArray();
        var tree = new Dictionary<string, FtpListItem[]> { ["/TV"] = names.Select(Dir).ToArray() };
        var failing = names.Where((_, i) => i % 2 == 0).ToArray();
        var entries = new List<FtpSearchService.IndexEntry>();
        var report = new FtpSearchService.IndexCrawlReport();

        await FtpSearchService.CrawlForIndex(Lister(tree, failing), "/TV", "/TV", 0, 2, entries, report, CancellationToken.None);

        Assert.False(report.Aborted);
        Assert.Equal(10, report.Failed);
        Assert.Equal(20, entries.Count);
    }

    [Fact]
    public void Aborted_crawl_keeps_the_previous_index_whole()
    {
        var previous = new List<FtpSearchService.IndexEntry> { Entry("/TV/A"), Entry("/TV/A/Sample"), Entry("/TV/B") };
        var report = new FtpSearchService.IndexCrawlReport();
        for (var i = 0; i < FtpSearchService.IndexCrawlReport.MaxConsecutiveFailures; i++)
            report.Record($"/TV/X{i}", new IOException("down"));

        var merged = FtpSearchService.MergeAfterCrawl([Entry("/TV/A")], previous, report);

        Assert.Same(previous, merged);
    }

    [Fact]
    public void Aborted_first_build_still_publishes_what_it_found()
    {
        var report = new FtpSearchService.IndexCrawlReport();
        for (var i = 0; i < FtpSearchService.IndexCrawlReport.MaxConsecutiveFailures; i++)
            report.Record($"/TV/X{i}", new IOException("down"));
        var fresh = new List<FtpSearchService.IndexEntry> { Entry("/TV/A") };

        Assert.Same(fresh, FtpSearchService.MergeAfterCrawl(fresh, [], report));
    }

    [Fact]
    public void Failed_subtree_carries_previous_descendants_forward_without_duplicates()
    {
        var previous = new List<FtpSearchService.IndexEntry>
        {
            Entry("/TV/A"), Entry("/TV/A/Sample"), Entry("/TV/A/Subs/Eng"),
            Entry("/TV/AB/Sample"), Entry("/TV/B"), Entry("/TV/B/Sample"), Entry("/TV/Gone")
        };
        var fresh = new List<FtpSearchService.IndexEntry> { Entry("/TV/A"), Entry("/TV/AB"), Entry("/TV/B") };
        var report = new FtpSearchService.IndexCrawlReport();
        report.Record("/TV/A", new IOException("550"));
        report.RecordSuccess();

        var merged = FtpSearchService.MergeAfterCrawl(fresh, previous, report);

        // /TV/AB/Sample is not under /TV/A; /TV/B/Sample was listed fresh (and is now gone);
        // /TV/Gone vanished from a successful listing.
        Assert.Equal(["/TV/A", "/TV/A/Sample", "/TV/A/Subs/Eng", "/TV/AB", "/TV/B"],
            merged.Select(e => e.Path).Order().ToArray());
    }

    [Theory]
    [InlineData("/", "/")]
    [InlineData("///", "/")]
    [InlineData("", "/")]
    [InlineData("/TV/", "/TV")]
    [InlineData("/TV", "/TV")]
    [InlineData("/tv///", "/tv")]
    public void Normalize_search_root_preserves_the_filesystem_root_and_path_case(string configured, string expected)
        => Assert.Equal(expected, FtpSearchService.NormalizeSearchRoot(configured));

    [Theory]
    [InlineData("/")]
    [InlineData("///")]
    public async Task Failed_filesystem_root_preserves_previous_top_level_entries_and_descendants(string configuredRoot)
    {
        // Use the same normalization as RefreshIndex and live search. LIST must
        // target the filesystem root, regardless of the connection's current dir.
        var root = FtpSearchService.NormalizeSearchRoot(configuredRoot);
        Assert.Equal("/", root);
        var previous = new List<FtpSearchService.IndexEntry>
        {
            Entry("/TV"), Entry("/TV/Show.A"), Entry("/MOVIES/Film.A")
        };
        var fresh = new List<FtpSearchService.IndexEntry>();
        var report = new FtpSearchService.IndexCrawlReport();

        await FtpSearchService.CrawlForIndex(Lister([], root), root, root, 0, 2, fresh, report, CancellationToken.None);
        var merged = FtpSearchService.MergeAfterCrawl(fresh, previous, report);

        Assert.Equal(1, report.Failed);
        Assert.Equal("/", report.FirstFailedPath);
        Assert.False(report.Aborted);
        Assert.Equal(previous.Select(e => e.Path), merged.Select(e => e.Path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    public void Failed_filesystem_root_retains_fresh_entries_without_duplicates(string failedRoot)
    {
        var previous = new List<FtpSearchService.IndexEntry>
        {
            Entry("/TV"), Entry("/TV/Show.A"), Entry("/MOVIES/Film.A")
        };
        var current = Entry("/TV/Show.A");
        current.Size = 123;
        var fresh = new List<FtpSearchService.IndexEntry> { current, Entry("/TV/Show.B") };
        var report = new FtpSearchService.IndexCrawlReport();
        report.Record(failedRoot, new IOException("root listing unavailable"));

        var merged = FtpSearchService.MergeAfterCrawl(fresh, previous, report);

        Assert.Equal(4, merged.Count);
        Assert.Same(current, Assert.Single(merged, e => e.Path == "/TV/Show.A"));
        Assert.Contains(merged, e => e.Path == "/TV/Show.B");
        Assert.Contains(merged, e => e.Path == "/TV");
        Assert.Contains(merged, e => e.Path == "/MOVIES/Film.A");
    }

    [Fact]
    public void Clean_crawl_publishes_fresh_entries_only()
    {
        var fresh = new List<FtpSearchService.IndexEntry> { Entry("/TV/A") };
        Assert.Same(fresh, FtpSearchService.MergeAfterCrawl(fresh, [Entry("/TV/Old")], new FtpSearchService.IndexCrawlReport()));
    }

    [Fact]
    public void RefreshIndex_publishes_the_merged_index()
    {
        var source = CpsvDataCommandRejectionTests.ReadSource("Downloads", "FtpSearchService.cs");
        var refresh = source[source.IndexOf("public async Task RefreshIndex(", StringComparison.Ordinal)..];
        refresh = refresh[..refresh.IndexOf("internal static List<IndexEntry> MergeAfterCrawl", StringComparison.Ordinal)];
        Assert.Contains("MergeAfterCrawl(entries, previous, report)", refresh);
        Assert.Contains("_index = merged;", refresh);
        Assert.DoesNotContain("_index = entries;", refresh);
        Assert.Contains("if (report.Aborted) break;", refresh);
    }

    [Fact]
    public void Site_search_excludes_nuked_status_paths_but_preserves_real_titles()
    {
        const string response = "200-/TV/[NUKED]-Show.S01E02-GRP (3F/1G/1h)\n" +
            "200-/TV/Nuked.2026.1080p-GRP (3F/1G/1h)\n" +
            "200-/TV/[NUKEDGroup]-Show.S01-GRP (3F/1G/1h)";

        var results = FtpSearchService.ParseSiteSearchResponse(response);

        Assert.Equal(["Nuked.2026.1080p-GRP", "[NUKEDGroup]-Show.S01-GRP"],
            results.Select(result => result.ReleaseName));
    }
}
