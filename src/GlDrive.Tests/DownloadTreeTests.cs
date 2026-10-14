using System.IO;
using FluentFTP;
using GlDrive.Downloads;
using Xunit;

namespace GlDrive.Tests;

public sealed class DownloadTreeTests
{
    private readonly string _destination = Path.Combine(Path.GetTempPath(), "gldrive-tree-test", "release");

    private static FtpListItem Entry(string name, bool directory = false) => new()
    {
        Name = name, Type = directory ? FtpObjectType.Directory : FtpObjectType.File, Size = directory ? 0 : 25
    };

    private Task<DownloadTreePlan> Plan(Dictionary<string, FtpListItem[]> tree, CancellationToken ct = default) =>
        DownloadTree.BuildAsync("/release/", _destination,
            (_, _) => Task.FromResult(true),
            (_, _) => throw new InvalidOperationException("A directory must never be probed as a file"),
            (path, _) => Task.FromResult(tree[path]), ct);

    [Fact]
    public async Task Release_tree_preserves_same_named_files_in_different_directories()
    {
        var plan = await Plan(new()
        {
            ["/release"] = [Entry("release.nfo"), Entry("CD1", true), Entry("CD2", true)],
            ["/release/CD1"] = [Entry("disc.rar"), Entry("Subs", true)],
            ["/release/CD2"] = [Entry("disc.rar")],
            ["/release/CD1/Subs"] = [Entry("en.srt")]
        });

        Assert.True(plan.IsDirectory);
        Assert.Equal(4, plan.Files.Count);
        Assert.Contains(plan.Files, f => f.RemotePath == "/release/CD1/disc.rar" &&
            f.LocalPath == Path.Combine(_destination, "CD1", "disc.rar"));
        Assert.Contains(plan.Files, f => f.RemotePath == "/release/CD2/disc.rar" &&
            f.LocalPath == Path.Combine(_destination, "CD2", "disc.rar"));
        Assert.Contains(plan.Files, f => f.LocalPath == Path.Combine(_destination, "CD1", "Subs", "en.srt"));
        Assert.Equal(100, plan.Files.Sum(f => f.Size));
    }

    [Fact]
    public async Task Directory_only_release_and_empty_subdirectories_remain_directories()
    {
        var plan = await Plan(new()
        {
            ["/release"] = [Entry("Season 01", true), Entry("Empty", true)],
            ["/release/Season 01"] = [Entry("episode.mkv")],
            ["/release/Empty"] = []
        });

        Assert.True(plan.IsDirectory);
        Assert.Equal("/release/Season 01/episode.mkv", Assert.Single(plan.Files).RemotePath);
        Assert.Contains(Path.Combine(_destination, "Empty"), plan.Directories);
    }

    [Fact]
    public async Task Empty_root_does_not_become_a_file_download()
    {
        var plan = await Plan(new() { ["/release"] = [] });
        Assert.True(plan.IsDirectory);
        Assert.Empty(plan.Files);
        Assert.Equal(_destination, Assert.Single(plan.Directories));
    }

    [Fact]
    public async Task Explicit_file_probe_is_required_before_single_file_download()
    {
        var plan = await DownloadTree.BuildAsync("/video.mkv", _destination,
            (_, _) => Task.FromResult(false), (_, _) => Task.FromResult(true),
            (_, _) => throw new InvalidOperationException("Files must not be listed as directories"), default);
        Assert.False(plan.IsDirectory);
        Assert.Equal("/video.mkv", Assert.Single(plan.Files).RemotePath);
        Assert.Empty(plan.Directories);

        await Assert.ThrowsAsync<FileNotFoundException>(() => DownloadTree.BuildAsync("/missing", _destination,
            (_, _) => Task.FromResult(false), (_, _) => Task.FromResult(false),
            (_, _) => Task.FromResult(Array.Empty<FtpListItem>()), default));
    }

    [Fact]
    public async Task Failed_subdirectory_cannot_publish_a_partial_plan()
    {
        await Assert.ThrowsAsync<IOException>(() => DownloadTree.BuildAsync("/release", _destination,
            (_, _) => Task.FromResult(true), (_, _) => Task.FromResult(false),
            (path, _) => path == "/release"
                ? Task.FromResult(new[] { Entry("release.nfo"), Entry("CD1", true) })
                : throw new IOException("550 Cannot list CD1"), default));
    }

    [Theory]
    [InlineData("../outside.mkv")]
    [InlineData("..\\outside.mkv")]
    public async Task Remote_entries_cannot_escape_the_local_tree(string name)
    {
        await Assert.ThrowsAsync<IOException>(() => Plan(new() { ["/release"] = [Entry(name)] }));
    }

    [Fact]
    public async Task Sanitized_filename_collisions_fail_instead_of_overwriting_content()
    {
        await Assert.ThrowsAsync<IOException>(() => Plan(new()
        {
            ["/release"] = [Entry("movie:one.mkv"), Entry("movieone.mkv")]
        }));
    }

    [Fact]
    public async Task Dot_entries_and_zipscript_status_directories_are_not_traversed()
    {
        var plan = await Plan(new()
        {
            ["/release"] = [Entry(".", true), Entry("..", true), Entry("[ Incomplete ]", true), Entry("movie.mkv")]
        });
        Assert.Single(plan.Files);
        Assert.Single(plan.Directories);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_during_listing_does_not_publish_a_partial_plan(bool empty)
    {
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DownloadTree.BuildAsync("/release", _destination,
            (_, _) => Task.FromResult(true), (_, _) => Task.FromResult(false),
            (_, _) => { cts.Cancel(); return Task.FromResult(empty ? [] : new[] { Entry("movie.mkv") }); }, cts.Token));
    }

    [Fact]
    public async Task Cancellation_during_file_probe_does_not_publish_a_single_file_plan()
    {
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DownloadTree.BuildAsync("/video.mkv", _destination,
            (_, _) => Task.FromResult(false), (_, _) => { cts.Cancel(); return Task.FromResult(true); },
            (_, _) => throw new InvalidOperationException("Must not list a file"), cts.Token));
    }
}
