using FluentFTP;
using GlDrive.Filesystem;
using Xunit;

namespace GlDrive.Tests;

public class DirectoryEnumerationReliabilityTests
{
    [Fact]
    public void Restart_on_the_same_directory_handle_reflects_deleted_and_created_entries()
    {
        var cache = new DirectoryCache();
        var fs = new GlDriveFileSystem(null!, cache, "/", "test");
        using var node = new FileNode("/dir", true);
        cache.Set("/dir", [Item("deleted.bin")]);
        Assert.Equal([".", "..", "deleted.bin"], ReadNames(fs, node));

        cache.InvalidateParent("/dir/deleted.bin");
        cache.Set("/dir", [Item("created.bin")]);

        Assert.Equal([".", "..", "created.bin"], ReadNames(fs, node));
    }

    [Fact]
    public void Marker_continuation_keeps_its_snapshot_until_the_next_restart()
    {
        var cache = new DirectoryCache();
        var fs = new GlDriveFileSystem(null!, cache, "/", "test");
        using var node = new FileNode("/dir", true);
        cache.Set("/dir", [Item("first.bin"), Item("old.bin"), Item("last.bin")]);
        Assert.Equal([".", "..", "first.bin", "old.bin", "last.bin"], ReadNames(fs, node));

        cache.Invalidate("/dir");
        cache.Set("/dir", [Item("first.bin"), Item("new.bin"), Item("last.bin")]);

        // WinFsp starts a new ReadDirectory buffer with a fresh context and a marker
        // when the previous buffer filled. Keep its ordering and entries stable.
        Assert.Equal(["old.bin", "last.bin"], ReadNames(fs, node, "first.bin"));
        Assert.Equal(["last.bin"], ReadNames(fs, node, "old.bin"));
        Assert.Equal([".", "..", "first.bin", "new.bin", "last.bin"], ReadNames(fs, node));
    }

    [Fact]
    public void Entries_in_one_read_directory_buffer_keep_the_initial_snapshot()
    {
        var cache = new DirectoryCache();
        var fs = new GlDriveFileSystem(null!, cache, "/", "test");
        using var node = new FileNode("/dir", true);
        cache.Set("/dir", [Item("old.bin")]);
        object context = null!;
        Assert.True(fs.ReadDirectoryEntry(node, node, null!, null!, ref context, out var first, out _));
        Assert.Equal(".", first);

        cache.Invalidate("/dir");
        cache.Set("/dir", [Item("new.bin")]);

        var remaining = new List<string>();
        while (fs.ReadDirectoryEntry(node, node, null!, null!, ref context, out var name, out _))
            remaining.Add(name);
        Assert.Equal(["..", "old.bin"], remaining);
        Assert.Equal([".", "..", "new.bin"], ReadNames(fs, node));
    }

    private static FtpListItem Item(string name) => new() { Name = name, Type = FtpObjectType.File };

    private static List<string> ReadNames(GlDriveFileSystem fs, FileNode node, string? marker = null)
    {
        object context = null!;
        var names = new List<string>();
        while (fs.ReadDirectoryEntry(node, node, null!, marker!, ref context, out var name, out _))
            names.Add(name);
        return names;
    }
}
