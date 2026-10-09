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

    // 2026-10-09 release smoke "FAIL: WinFsp delete" (also 2026-10-07): a LIST that started
    // before DELE finished after the delete's invalidation and re-cached the deleted file, so
    // File.Exists saw it for the whole TTL. A listing older than an invalidation must not be stored.
    [Fact]
    public void Listing_fetched_before_an_invalidation_is_not_stored()
    {
        var cache = new DirectoryCache();
        var epoch = cache.BeginFetch();
        var stale = new FtpListItem { Name = "deleted.bin" };

        cache.InvalidateParent("/dir/deleted.bin");
        Assert.False(cache.Set("/dir", [stale], epoch));

        Assert.False(cache.TryGet("/dir", out _));
        Assert.Null(cache.FindItem("/dir/deleted.bin"));
    }

    [Fact]
    public void Direct_invalidation_and_clear_also_reject_older_listings()
    {
        var cache = new DirectoryCache();
        var e1 = cache.BeginFetch();
        cache.Invalidate("/dir");
        Assert.False(cache.Set("/dir", [], e1));

        var e2 = cache.BeginFetch();
        cache.Clear();
        Assert.False(cache.Set("/other", [], e2));
    }

    [Fact]
    public void Listing_fetched_after_the_invalidation_is_stored()
    {
        var cache = new DirectoryCache();
        cache.InvalidateParent("/dir/deleted.bin");
        var epoch = cache.BeginFetch();

        Assert.True(cache.Set("/dir", [new FtpListItem { Name = "kept.bin" }], epoch));
        Assert.NotNull(cache.FindItem("/dir/kept.bin"));
    }

    [Fact]
    public void Invalidation_of_a_different_directory_does_not_block_storing()
    {
        var cache = new DirectoryCache();
        var epoch = cache.BeginFetch();
        cache.InvalidateParent("/elsewhere/x.bin");

        Assert.True(cache.Set("/dir", [], epoch));
    }

    [Fact]
    public async Task Background_refresh_started_before_a_delete_does_not_resurrect_the_file()
    {
        var cache = new DirectoryCache(ttlSeconds: 0);
        var fetchStarted = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        cache.BackgroundRefresh = _ => { fetchStarted.TrySetResult(cache.BeginFetch()); return Task.CompletedTask; };
        cache.Set("/dir", [new FtpListItem { Name = "deleted.bin" }]);
        Assert.True(cache.TryGet("/dir", out _)); // stale hit schedules a refresh
        var refreshEpoch = await fetchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cache.InvalidateParent("/dir/deleted.bin");
        Assert.False(cache.Set("/dir", [new FtpListItem { Name = "deleted.bin" }], refreshEpoch));
    }

    [Fact]
    public void Invalidation_tracking_stays_bounded_and_conservative()
    {
        var cache = new DirectoryCache();
        var epoch = cache.BeginFetch();
        for (var i = 0; i < 10_000; i++) cache.Invalidate($"/d{i}");

        Assert.InRange(cache.TrackedInvalidations, 0, DirectoryCache.MaxTrackedInvalidations);
        Assert.False(cache.Set("/d1", [], epoch)); // pruning must never let an old listing through
    }
}
