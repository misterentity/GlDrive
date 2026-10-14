using System.IO;
using GlDrive.Util;
using Xunit;

namespace GlDrive.Tests;

// 2026-10-13 19:35: Settings > category path "Browse" crashed the UI twice with
// Win32Exception (15) "The system cannot find the drive specified" because the
// configured path lived on T:\, which was not mounted. OpenFolderDialog throws
// from ShowDialog when InitialDirectory does not resolve to a shell item.
public sealed class DialogInitialDirectoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-dialog-dir-tests-" + Guid.NewGuid().ToString("N"));
    public DialogInitialDirectoryTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    private static string UnmountedDrivePath()
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        var free = "ZYXWVUTSRQPONMLKJIHGFED".First(c => !used.Contains(c));
        return $@"{free}:\Movies\Some Category";
    }

    [Fact]
    public void Path_on_unmounted_drive_is_never_returned()
    {
        Assert.Null(DialogPaths.ExistingDirectoryOrNull(UnmountedDrivePath()));
    }

    [Fact]
    public void Falls_through_to_next_candidate_when_first_drive_is_missing()
    {
        Assert.Equal(_root, DialogPaths.ExistingDirectoryOrNull(UnmountedDrivePath(), _root));
    }

    [Fact]
    public void Missing_subfolder_resolves_to_nearest_existing_ancestor()
    {
        var notYetCreated = Path.Combine(_root, "TV", "Show", "Season 01");
        Assert.Equal(_root, DialogPaths.ExistingDirectoryOrNull(notYetCreated));
    }

    [Fact]
    public void Existing_directory_is_returned_unchanged()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "Movies")).FullName;
        Assert.Equal(dir, DialogPaths.ExistingDirectoryOrNull(dir));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relative\\path")]
    public void Blank_or_relative_candidates_are_skipped(string? candidate)
    {
        Assert.Null(DialogPaths.ExistingDirectoryOrNull(candidate));
    }

    [Fact]
    public void Nonexistent_name_with_odd_characters_falls_back_to_its_existing_root()
    {
        var odd = Path.Combine(_root, "bad|chars", "deeper");
        Assert.Equal(_root, DialogPaths.ExistingDirectoryOrNull(odd));
    }
}
