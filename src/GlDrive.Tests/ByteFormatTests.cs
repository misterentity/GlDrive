using System.Globalization;
using GlDrive.Util;
using Xunit;

namespace GlDrive.Tests;

/// <summary>Pins the text each former private formatter produced at its call site.</summary>
public class ByteFormatTests
{
    private static string Invariant(Func<string> format)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try { return format(); }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    // Cleanup, Extractor, torrent search (blank when unknown).
    [Theory]
    [InlineData(0L, "0 B", "")]
    [InlineData(-5L, "-5 B", "")]
    [InlineData(800L, "800 B", "800 B")]
    [InlineData(1536L, "2 KB", "2 KB")]
    [InlineData(5L * 1024 * 1024 + 300 * 1024, "5.3 MB", "5.3 MB")]
    [InlineData(3L << 30, "3.0 GB", "3.0 GB")]
    [InlineData(2L << 40, "2048.0 GB", "2048.0 GB")]
    public void Compact(long bytes, string plain, string blankZero)
    {
        Assert.Equal(plain, Invariant(() => ByteFormat.Compact(bytes)));
        Assert.Equal(blankZero, Invariant(() => ByteFormat.Compact(bytes, zeroText: "")));
    }

    // Dashboard sizes: one decimal at every unit up to TB, em dash when empty.
    [Theory]
    [InlineData(0L, "—")]
    [InlineData(800L, "800.0 B")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(3L << 40, "3.0 TB")]
    [InlineData(3L << 50, "3072.0 TB")]
    public void One_decimal_sizes(long bytes, string expected)
        => Assert.Equal(expected, Invariant(() => ByteFormat.Format(bytes, [1, 1, 1, 1, 1], zeroText: "—")));

    // Spread file lists and race detail.
    [Theory]
    [InlineData(800L, "800 B", "800 B")]
    [InlineData(1536L, "1.5 KB", "2 KB")]
    [InlineData(5L << 20, "5.0 MB", "5.0 MB")]
    [InlineData(3L << 30, "3.00 GB", "3.00 GB")]
    public void Spread_sizes(long bytes, string file, string race)
    {
        Assert.Equal(file, Invariant(() => ByteFormat.Format(bytes, [0, 1, 1, 2])));
        Assert.Equal(race, Invariant(() => ByteFormat.Format(bytes, [0, 0, 1, 2])));
    }

    // Player: KB is the smallest unit, blank when empty.
    [Theory]
    [InlineData(0L, "")]
    [InlineData(500L, "0 KB")]
    [InlineData(1536L, "2 KB")]
    [InlineData(5L << 20, "5.0 MB")]
    [InlineData(3L << 30, "3.00 GB")]
    public void Player_sizes(long bytes, string expected)
        => Assert.Equal(expected, Invariant(() => ByteFormat.Format(bytes, [0, 0, 1, 2], zeroText: "", minUnit: 1)));

    // Stream matrix: IEC labels.
    [Theory]
    [InlineData(800L, "800 B")]
    [InlineData(1536L, "2 KiB")]
    [InlineData(5L << 20, "5.0 MiB")]
    [InlineData(3L << 30, "3.00 GiB")]
    [InlineData(3L << 40, "3.00 TiB")]
    public void Stream_matrix_sizes(long bytes, string expected)
        => Assert.Equal(expected, Invariant(() => ByteFormat.Format(bytes, [0, 0, 1, 2, 2], iec: true)));

    [Theory]
    [InlineData(0d, "", "0 B/s", "0 B/s", "")]
    [InlineData(800d, "800 B/s", "800 B/s", "800 B/s", "800.0 B/s")]
    [InlineData(1536d, "1.5 KB/s", "2 KB/s", "2 KB/s", "1.5 KB/s")]
    [InlineData(5d * 1024 * 1024, "5.0 MB/s", "5.0 MB/s", "5.0 MB/s", "5.0 MB/s")]
    [InlineData(2d * 1024 * 1024 * 1024, "2048.0 MB/s", "2048.0 MB/s", "2.00 GB/s", "2.0 GB/s")]
    public void Rates(double bps, string spreadRow, string tray, string streamMatrix, string dashboard)
    {
        Assert.Equal(spreadRow, Invariant(() => ByteFormat.Format(bps, [0, 1, 1], zeroText: "", suffix: "/s")));
        Assert.Equal(tray, Invariant(() => ByteFormat.Format(bps, [0, 0, 1], suffix: "/s")));
        Assert.Equal(streamMatrix, Invariant(() => ByteFormat.Format(bps, [0, 0, 1, 2], suffix: "/s")));
        Assert.Equal(dashboard, Invariant(() => ByteFormat.Format(bps, [1, 1, 1, 1], zeroText: "", suffix: "/s")));
    }
}
