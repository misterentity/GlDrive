using GlDrive.Downloads;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// The reservation's whole job is to stop N concurrent downloads from each seeing
/// "enough room" and collectively overrunning the disk. Tests use an injected
/// fixed free-space provider so they're pure (no real DriveInfo).
/// </summary>
public class DiskReservationTests
{
    private static DiskReservation WithFreeSpace(long freeBytes, long headroom = 0) =>
        new(headroomBytes: headroom, freeSpaceProvider: _ => freeBytes);

    [Fact]
    public void Concurrent_reservations_cannot_overcommit_the_drive()
    {
        var disk = WithFreeSpace(100); // 100 bytes free, no headroom

        Assert.True(disk.TryReserve(@"C:\dl\a", 60, out var root));   // 60 reserved
        Assert.False(disk.TryReserve(@"C:\dl\b", 60, out _));         // 60+60 > 100 → denied
        Assert.True(disk.TryReserve(@"C:\dl\c", 40, out _));          // 60+40 == 100 → fits

        Assert.Equal(100, disk.ReservedOn(root));
    }

    [Fact]
    public void Release_frees_capacity_for_the_next_reservation()
    {
        var disk = WithFreeSpace(100);
        Assert.True(disk.TryReserve(@"C:\dl\a", 80, out var root));
        Assert.False(disk.TryReserve(@"C:\dl\b", 80, out _)); // 160 > 100

        disk.Release(root, 80);
        Assert.Equal(0, disk.ReservedOn(root));
        Assert.True(disk.TryReserve(@"C:\dl\b", 80, out _));  // now fits
    }

    [Fact]
    public void Headroom_is_kept_free()
    {
        var disk = WithFreeSpace(100, headroom: 30);
        Assert.False(disk.TryReserve(@"C:\dl\a", 80, out _)); // 80 leaves 20 < 30 headroom
        Assert.True(disk.TryReserve(@"C:\dl\a", 70, out _));  // 70 leaves 30 == headroom
    }

    [Fact]
    public void Zero_byte_reserve_always_succeeds_and_reserves_nothing()
    {
        var disk = WithFreeSpace(10);
        Assert.True(disk.TryReserve(@"C:\dl\a", 0, out var root));
        Assert.Equal(0, disk.ReservedOn(root));
    }

    [Fact]
    public void Different_drive_roots_are_tracked_independently()
    {
        var disk = WithFreeSpace(100);
        Assert.True(disk.TryReserve(@"C:\dl\a", 90, out _));
        Assert.True(disk.TryReserve(@"D:\dl\b", 90, out _)); // different root, own budget
    }
}
