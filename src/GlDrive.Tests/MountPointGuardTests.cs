using System.IO;
using GlDrive.Services;
using Xunit;

namespace GlDrive.Tests;

public sealed class MountPointGuardTests
{
    [Theory]
    [InlineData("C", "C:\\")]
    [InlineData("g", "G:\\")]
    [InlineData("G", "g:")]
    public void Occupied_letters_are_rejected_without_probing_or_disconnect(string letter, string occupied)
    {
        // This includes disconnected shares and removable media: their existence
        // in the OS drive table is sufficient, regardless of directory accessibility.
        var error = Assert.Throws<IOException>(() => MountPointGuard.EnsureAvailable(letter, [occupied]));
        Assert.Contains("already in use", error.Message);
    }

    [Fact]
    public void Free_letter_is_accepted_among_other_occupied_drives() =>
        MountPointGuard.EnsureAvailable("G", ["C:\\", "D:\\", "H:\\"]);

    [Theory]
    [InlineData("")]
    [InlineData("C:")]
    [InlineData("1")]
    [InlineData("é")]
    [InlineData("G /P")]
    public void Invalid_mount_points_are_rejected(string letter) =>
        Assert.Throws<ArgumentException>(() => MountPointGuard.EnsureAvailable(letter, []));
}
