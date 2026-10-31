using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// v3.19.4 — one WRN per IRC announce while a peer site was down (355 in 9.5 h on v3.19.3)
/// buried every other warning and never named the unavailable sites.
/// </summary>
public sealed class AutoRaceSkipNoticeTests
{
    private static readonly DateTime T0 = new(2026, 10, 30, 17, 31, 0, DateTimeKind.Utc);

    [Fact]
    public void FirstSkipWarns()
    {
        var notice = new AutoRaceSkipNotice();
        Assert.Equal(0, notice.Record(["zephyr", "SYN"], T0));
    }

    [Fact]
    public void RepeatSkipsForSameOutageAreSuppressed()
    {
        var notice = new AutoRaceSkipNotice();
        notice.Record(["zephyr", "SYN"], T0);
        Assert.Null(notice.Record(["zephyr", "SYN"], T0.AddMinutes(1)));
        Assert.Null(notice.Record(["SYN", "zephyr"], T0.AddMinutes(2)));
    }

    [Fact]
    public void ChangedUnavailableSetWarnsWithSuppressedCount()
    {
        var notice = new AutoRaceSkipNotice();
        notice.Record(["zephyr", "SYN"], T0);
        notice.Record(["zephyr", "SYN"], T0.AddMinutes(1));
        notice.Record(["zephyr", "SYN"], T0.AddMinutes(2));
        Assert.Equal(2, notice.Record(["SYN"], T0.AddMinutes(3)));
    }

    [Fact]
    public void SameOutageReWarnsAfterAnHour()
    {
        var notice = new AutoRaceSkipNotice();
        notice.Record(["zephyr"], T0);
        Assert.Null(notice.Record(["zephyr"], T0.AddMinutes(59)));
        Assert.Equal(1, notice.Record(["zephyr"], T0.AddHours(1)));
        Assert.Null(notice.Record(["zephyr"], T0.AddHours(1).AddMinutes(1)));
    }

    [Fact]
    public void RecoveryResetsSoTheNextOutageWarns()
    {
        var notice = new AutoRaceSkipNotice();
        notice.Record(["zephyr"], T0);
        notice.Reset();
        Assert.Equal(0, notice.Record(["zephyr"], T0.AddMinutes(1)));
    }

    [Fact]
    public void SiteNamesCompareCaseInsensitively()
    {
        var notice = new AutoRaceSkipNotice();
        notice.Record(["zephyr"], T0);
        Assert.Null(notice.Record(["ZEPHYR"], T0.AddMinutes(1)));
    }
}
