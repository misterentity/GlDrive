using System;
using GlDrive.Config;
using GlDrive.Logging;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// v3.10.47 — log retention is expressed in DAYS. The Serilog sink rolls daily AND on
/// size, but retainedFileCountLimit counts FILES, so a day noisy enough to exceed the
/// size cap used to evict a neighbouring day's history. Observed 2026-08-03: one
/// mid-day roll turned the configured 3 days into ~1.5, wiping the history exactly
/// when an incident made it worth keeping.
/// </summary>
public class LogRetentionTests
{
    [Fact]
    public void Intraday_roll_allowance_leaves_headroom_for_size_rolls()
    {
        // Must be >1, otherwise a single size-triggered roll evicts a whole day again.
        Assert.True(SerilogSetup.IntradayRollAllowance > 1,
            "a day that rolls on size must not cost another day its history");
    }

    [Theory]
    [InlineData(3)]
    [InlineData(1)]
    [InlineData(14)]
    public void File_count_cap_never_binds_before_the_day_cap(int retainedDays)
    {
        // The count limit is only a disk-blowout stop. For it to be a stop and not the
        // effective retention policy, it must exceed one-file-per-retained-day.
        var countCap = retainedDays * SerilogSetup.IntradayRollAllowance;
        Assert.True(countCap > retainedDays,
            $"count cap {countCap} must not bind before {retainedDays} days of history");
    }

    [Fact]
    public void Worst_case_log_disk_usage_stays_bounded()
    {
        var cfg = new LoggingConfig();
        var worstCaseMb = cfg.RetainedFiles * SerilogSetup.IntradayRollAllowance * cfg.MaxFileSizeMb;

        // Bounded, and small enough that the fix can't fill a disk with defaults.
        Assert.True(worstCaseMb <= 512,
            $"worst-case log footprint {worstCaseMb} MB should stay modest");
    }

    [Fact]
    public void Default_retention_is_three_days_of_history()
    {
        var cfg = new LoggingConfig();
        // Default intent: three DAYS of history, not three files.
        Assert.Equal(3, cfg.RetainedFiles);
        Assert.Equal(10, cfg.MaxFileSizeMb);
    }
}
