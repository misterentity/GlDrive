using System.IO;
using FluentFTP;
using GlDrive.Downloads;
using Xunit;

namespace GlDrive.Tests;

public sealed class LiveSearchFailureTests
{
    private static FtpListItem Dir(string path) => new()
    {
        Name = path[(path.LastIndexOf('/') + 1)..], FullName = path, Type = FtpObjectType.Directory
    };

    [Fact]
    public async Task Failed_child_does_not_hide_later_sibling_matches()
    {
        var results = new List<SearchResult>();
        var report = new FtpSearchService.IndexCrawlReport();
        FtpSearchService.DirectoryLister list = (path, _) => path switch
        {
            "/TV" => Task.FromResult(new[] { Dir("/TV/bad"), Dir("/TV/Wanted.Show") }),
            "/TV/bad" => throw new IOException("550 Permission denied"),
            _ => Task.FromResult(Array.Empty<FtpListItem>())
        };

        await FtpSearchService.SearchRecursive(list, "/TV", "Wanted", 0, 2, results, null, default, report);

        Assert.Equal("/TV/Wanted.Show", Assert.Single(results).RemotePath);
        Assert.Equal(1, report.Failed);
        Assert.False(report.Aborted);
    }

    [Fact]
    public async Task Repeated_transport_failures_are_bounded()
    {
        var results = new List<SearchResult>();
        var report = new FtpSearchService.IndexCrawlReport();
        var calls = 0;
        FtpSearchService.DirectoryLister list = (path, _) =>
        {
            calls++;
            return path == "/TV"
                ? Task.FromResult(Enumerable.Range(0, 50).Select(i => Dir($"/TV/Show.{i}")).ToArray())
                : throw new IOException("Network unavailable");
        };

        await FtpSearchService.SearchRecursive(list, "/TV", "Wanted", 0, 2, results, null, default, report);

        Assert.True(report.Aborted);
        Assert.Equal(1 + FtpSearchService.IndexCrawlReport.MaxConsecutiveFailures, calls);
    }

    [Fact]
    public async Task Borrow_timeout_costs_only_its_directory()
    {
        var results = new List<SearchResult>();
        FtpSearchService.DirectoryLister list = (path, _) => path switch
        {
            "/TV" => Task.FromResult(new[] { Dir("/TV/busy"), Dir("/TV/Wanted.Show") }),
            "/TV/busy" => throw new OperationCanceledException("Internal borrow deadline"),
            _ => Task.FromResult(Array.Empty<FtpListItem>())
        };
        await FtpSearchService.SearchRecursive(list, "/TV", "Wanted", 0, 2, results, null, default);
        Assert.Equal("/TV/Wanted.Show", Assert.Single(results).RemotePath);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_instead_of_returning_partial_success()
    {
        using var cts = new CancellationTokenSource();
        FtpSearchService.DirectoryLister list = (_, _) =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FtpSearchService.SearchRecursive(
            list, "/TV", "Wanted", 0, 2, [], null, cts.Token));
    }

    [Fact]
    public async Task Nested_results_keep_category_relative_to_original_search_root()
    {
        var results = new List<SearchResult>();
        FtpSearchService.DirectoryLister list = (path, _) => Task.FromResult(path switch
        {
            "/site" => new[] { Dir("/site/TV") },
            "/site/TV" => new[] { Dir("/site/TV/Wanted.Show") },
            _ => Array.Empty<FtpListItem>()
        });
        await FtpSearchService.SearchRecursive(list, "/site", "Wanted", 0, 2, results, null, default);
        Assert.Equal("TV", Assert.Single(results).Category);
    }
}
