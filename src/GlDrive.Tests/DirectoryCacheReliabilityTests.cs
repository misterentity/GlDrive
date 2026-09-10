using FluentFTP;
using GlDrive.Filesystem;
using Xunit;

namespace GlDrive.Tests;

public class DirectoryCacheReliabilityTests
{
    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("\\")]
    public void Root_entries_can_be_found_and_invalidated(string root)
    {
        var cache = new DirectoryCache();
        var item = new FtpListItem { Name = "file.txt" };
        cache.Set(root, [item]);
        Assert.Same(item, cache.FindItem("/file.txt"));
        Assert.True(cache.TryGet("/", out _));

        cache.InvalidateParent("/file.txt");
        Assert.False(cache.TryGet(root, out _));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Small_caches_never_exceed_capacity(int capacity)
    {
        var cache = new DirectoryCache(maxEntries: capacity);
        for (var i = 0; i < 20; i++)
            cache.Set($"/{i}", []);

        var retained = Enumerable.Range(0, 20).Count(i => cache.TryGet($"/{i}", out _));
        Assert.InRange(retained, 1, capacity);
        Assert.True(cache.GetMetrics().Evictions >= 20 - capacity);
    }

    [Fact]
    public void Expired_entries_without_a_refresher_are_misses()
    {
        var cache = new DirectoryCache(ttlSeconds: 0);
        cache.Set("/dir", []);
        Assert.False(cache.TryGet("/dir", out _));
        Assert.Equal(1, cache.GetMetrics().Misses);
    }

    [Fact]
    public void Duplicate_listing_names_do_not_break_all_metadata_lookups()
    {
        var cache = new DirectoryCache();
        var first = new FtpListItem { Name = "file.txt" };
        var differentCase = new FtpListItem { Name = "FILE.txt" };
        cache.Set("/dir", [first, new FtpListItem { Name = "file.txt" }, differentCase]);

        Assert.Same(first, cache.FindItem("/dir/file.txt"));
        Assert.Same(differentCase, cache.FindItem("/dir/FILE.txt"));
    }

    [Fact]
    public async Task Concurrent_stale_reads_start_only_one_refresh()
    {
        var cache = new DirectoryCache(ttlSeconds: 0);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        cache.BackgroundRefresh = async _ =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            await release.Task;
        };
        cache.Set("/dir", []);
        try
        {
            Assert.True(cache.TryGet("/dir", out _));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Parallel.For(0, 100, i => Assert.True(cache.TryGet("/dir", out _)));
            Assert.Equal(1, calls);
        }
        finally { release.TrySetResult(); }
    }
}
