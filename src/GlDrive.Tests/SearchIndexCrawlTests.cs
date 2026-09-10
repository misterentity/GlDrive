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
}
