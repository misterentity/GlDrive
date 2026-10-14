using System.IO;
using FluentFTP;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

public class ManualFxpDirectoryTests
{
    [Fact]
    public async Task Nested_listing_finishes_with_one_listing_slot_and_preserves_relative_paths()
    {
        var tree = new Dictionary<string, FtpListItem[]>
        {
            ["/release"] = [Item("/release/CD1", true), Item("/release/a.nfo")],
            ["/release/CD1"] = [Item("/release/CD1/Sample", true), Item("/release/CD1/a.r00")],
            ["/release/CD1/Sample"] = [Item("/release/CD1/Sample/sample.mkv")]
        };
        using var slot = new SemaphoreSlim(1, 1);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var calls = new List<string>();
        async Task<FtpListItem[]> List(string path, CancellationToken ct)
        {
            await slot.WaitAsync(ct);
            try
            {
                calls.Add(path);
                await Task.Yield();
                return tree[path];
            }
            finally { slot.Release(); }
        }

        var files = new List<(string relative, string full)>();
        await SpreadManager.EnumerateDirectoryRecursive(List, "/release", "/release", files, 0, timeout.Token);

        Assert.Equal(3, calls.Count);
        Assert.Equal(["CD1/Sample/sample.mkv", "CD1/a.r00", "a.nfo"], files.Select(f => f.relative));
        Assert.Equal(1, slot.CurrentCount);
    }

    [Fact]
    public async Task Failed_child_listing_propagates_instead_of_returning_a_partial_tree()
    {
        Task<FtpListItem[]> List(string path, CancellationToken _) => path == "/release"
            ? Task.FromResult(new[] { Item("/release/CD1", true) })
            : Task.FromException<FtpListItem[]>(new IOException("LIST rejected"));
        var error = await Assert.ThrowsAsync<IOException>(() => SpreadManager.EnumerateDirectoryRecursive(
            List, "/release", "/release", [], 0, default));
        Assert.Equal("LIST rejected", error.Message);
    }

    [Fact]
    public async Task Rejected_file_fails_the_directory_copy_and_stops_later_transfers()
    {
        var attempted = new List<string>();
        var files = new[] { ("a.r00", "/rel/a.r00"), ("a.r01", "/rel/a.r01"), ("a.r02", "/rel/a.r02") };
        var error = await Assert.ThrowsAsync<IOException>(() => SpreadManager.TransferDirectoryFiles(files,
            (relative, full) =>
            {
                attempted.Add(relative);
                return Task.FromResult<string?>(relative == "a.r01" ? "553 Permission denied" : null);
            }));
        Assert.Equal(["a.r00", "a.r01"], attempted);
        Assert.Contains("/rel/a.r01", error.Message);
        Assert.Contains("553 Permission denied", error.Message);
    }

    [Fact]
    public async Task Successful_directory_copy_visits_every_file()
    {
        var attempted = new List<string>();
        await SpreadManager.TransferDirectoryFiles([("a", "/rel/a"), ("CD1/b", "/rel/CD1/b")],
            (relative, full) => { attempted.Add(full); return Task.FromResult<string?>(null); });
        Assert.Equal(["/rel/a", "/rel/CD1/b"], attempted);
    }

    private static FtpListItem Item(string path, bool directory = false) => new()
    {
        Name = path[(path.LastIndexOf('/') + 1)..], FullName = path,
        Type = directory ? FtpObjectType.Directory : FtpObjectType.File
    };
}
