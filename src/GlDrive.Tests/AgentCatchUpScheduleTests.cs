using System;
using System.Text.Json.Nodes;
using GlDrive.AiAgent;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Regression cover for the spurious daily "catch-up" run.
///
/// AgentRunner.LoadLastRun parsed the persisted UTC stamp with a bare DateTime.TryParse. For an
/// ISO-8601 string carrying a zone designator the default styles CONVERT to local time and return
/// Kind=Local; that value was then subtracted from DateTime.UtcNow, inflating the measured gap by
/// the machine's UTC offset. On this box (UTC-7 in August) a real 22.1h gap measured 29.1h, so the
/// >=23h catch-up predicate fired and the agent ran a second, unwanted time at ~02:00.
///
/// Production evidence: one run/day at 04:00 while the process was stable (2026-08-18..24), then
/// two runs every day across the restart-heavy release window (08-25..28), because each restart
/// re-read the stamp through the broken parse.
/// </summary>
public class AgentCatchUpScheduleTests
{
    // The exact stamp production had on disk, and the exact moment ScheduleNext ran on 08-28.
    private const string PersistedStamp = "2026-08-28T11:00:48.7523203Z";
    private static readonly DateTime NextMorningUtc =
        new(2026, 8, 29, 9, 7, 20, DateTimeKind.Utc); // 02:07:20 local, when TimeChanged fired

    /// <summary>
    /// Kind is the timezone-INDEPENDENT half of this regression. On a UTC build agent the broken
    /// parse yields the correct instant by coincidence, so an instant-only assertion would pass
    /// against the bug and lock it in. Kind=Local is wrong on every machine.
    /// </summary>
    [Fact]
    public void TryParseLastRunUtc_ReturnsUtcKind_NotLocal()
    {
        Assert.True(AgentRunner.TryParseLastRunUtc(PersistedStamp, out var parsed));
        Assert.Equal(DateTimeKind.Utc, parsed.Kind);
    }

    [Fact]
    public void TryParseLastRunUtc_PreservesTheAbsoluteInstant()
    {
        Assert.True(AgentRunner.TryParseLastRunUtc(PersistedStamp, out var parsed));
        Assert.Equal(new DateTime(2026, 8, 28, 11, 0, 48, DateTimeKind.Utc), parsed.AddTicks(-parsed.Ticks % TimeSpan.TicksPerSecond));
    }

    /// <summary>
    /// A file written by the BUGGY build holds an offset form ("...-07:00") rather than "...Z",
    /// because the corrupted Kind=Local value was round-tripped through "O". Normalising via
    /// ToUniversalTime means the fix self-heals that state instead of needing the file deleted.
    /// </summary>
    [Fact]
    public void TryParseLastRunUtc_NormalisesLegacyOffsetForm_ToTheSameInstant()
    {
        Assert.True(AgentRunner.TryParseLastRunUtc("2026-08-28T04:00:48.7523203-07:00", out var legacy));
        Assert.True(AgentRunner.TryParseLastRunUtc(PersistedStamp, out var canonical));
        Assert.Equal(DateTimeKind.Utc, legacy.Kind);
        Assert.Equal(canonical, legacy);
    }

    // Pacific time, the zone every production timestamp in these tests was recorded in.
    private static readonly TimeZoneInfo Pacific = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
    private const int RunHour = 4;

    private static DateTime PacificUtc(int y, int mo, int d, int h, int mi) =>
        TimeZoneInfo.ConvertTimeToUtc(new DateTime(y, mo, d, h, mi, 0, DateTimeKind.Unspecified), Pacific);

    /// <summary>The production scenario end to end: parse the real stamp, then ask the real
    /// predicate. A ~22.1h gap is NOT a missed run and must not schedule a catch-up.</summary>
    [Fact]
    public void ParsedStamp_TwentyTwoHoursLater_DoesNotTriggerCatchUp()
    {
        Assert.True(AgentRunner.TryParseLastRunUtc(PersistedStamp, out var lastRun));
        var gapHours = (NextMorningUtc - lastRun).TotalHours;
        Assert.InRange(gapHours, 22.0, 22.2);
        Assert.False(AgentRunner.PlanSchedule(lastRun, NextMorningUtc, RunHour, Pacific).CatchUp);
    }

    /// <summary>
    /// 2026-09-22: the 09-21 run finished 04:04:19 PT; at 03:52:30 PT SystemEvents.TimeChanged
    /// re-ran ScheduleNext. 23.8h had elapsed, so the old ">= 23h" predicate called it a missed
    /// run, caught up at 03:53, then the regular 04:00 slot ran again — two LLM runs, two change
    /// budgets and two DryRunsRemaining decrements in 7 minutes. Same shape on 09-13 and 09-14.
    /// Nothing was missed: yesterday's slot was served. The next run is today's 04:00.
    /// </summary>
    [Fact]
    public void TwentyThreePointEightHoursAfterServedSlot_IsNotAMissedRun()
    {
        var lastRun = PacificUtc(2026, 9, 21, 4, 4);
        var now = PacificUtc(2026, 9, 22, 3, 52);

        var plan = AgentRunner.PlanSchedule(lastRun, now, RunHour, Pacific);

        Assert.False(plan.CatchUp);
        Assert.Equal(PacificUtc(2026, 9, 22, 4, 0), plan.NextRunUtc);
    }

    /// <summary>
    /// 2026-09-20 23:04 PT a genuine catch-up ran (the box missed the 09-19 and 09-20 slots), then
    /// the 09-21 04:00 slot ran again 5h later over an essentially identical digest. A run that
    /// recent already serves the next slot; skip to the following one.
    /// </summary>
    [Fact]
    public void SlotAlreadyServedByRecentCatchUp_IsSkipped()
    {
        var lastRun = PacificUtc(2026, 9, 20, 23, 4);
        var now = PacificUtc(2026, 9, 20, 23, 5);

        var plan = AgentRunner.PlanSchedule(lastRun, now, RunHour, Pacific);

        Assert.False(plan.CatchUp);
        Assert.Equal(PacificUtc(2026, 9, 22, 4, 0), plan.NextRunUtc);
    }

    /// <summary>The guard must still do its job: a slot that passed with no run after it is caught up.</summary>
    [Fact]
    public void GenuinelyMissedSlot_StillTriggersCatchUp()
    {
        var lastRun = PacificUtc(2026, 9, 18, 4, 1);

        Assert.True(AgentRunner.PlanSchedule(lastRun, PacificUtc(2026, 9, 19, 10, 0), RunHour, Pacific).CatchUp);
        Assert.True(AgentRunner.PlanSchedule(lastRun, PacificUtc(2026, 9, 20, 23, 0), RunHour, Pacific).CatchUp);
    }

    /// <summary>The ordinary daily cadence: right after the 04:00 run, the next run is tomorrow's 04:00.</summary>
    [Fact]
    public void AfterScheduledRun_NextRunIsTomorrowsSlot()
    {
        var lastRun = PacificUtc(2026, 9, 22, 4, 1);

        var plan = AgentRunner.PlanSchedule(lastRun, lastRun.AddSeconds(1), RunHour, Pacific);

        Assert.False(plan.CatchUp);
        Assert.Equal(PacificUtc(2026, 9, 23, 4, 0), plan.NextRunUtc);
    }

    [Fact]
    public void NeverRun_DoesNotTriggerCatchUp_AndWaitsForTheSlot()
    {
        var now = PacificUtc(2026, 9, 22, 10, 0);

        var plan = AgentRunner.PlanSchedule(DateTime.MinValue, now, RunHour, Pacific);

        Assert.False(plan.CatchUp);
        Assert.Equal(PacificUtc(2026, 9, 23, 4, 0), plan.NextRunUtc);
    }

    [Fact]
    public void TryParseLastRunUtc_RejectsMissingAndGarbage()
    {
        Assert.False(AgentRunner.TryParseLastRunUtc(null, out _));
        Assert.False(AgentRunner.TryParseLastRunUtc("", out _));
        Assert.False(AgentRunner.TryParseLastRunUtc("not-a-date", out _));
    }

    /// <summary>
    /// The save side writes with "O"; the load side must read back the same instant. This is the
    /// round-trip the daily schedule actually depends on across a restart.
    /// </summary>
    [Fact]
    public void SaveFormat_RoundTripsThroughTheReader()
    {
        var written = new DateTime(2026, 8, 28, 11, 0, 48, DateTimeKind.Utc);
        var json = new JsonObject { ["utc"] = written.ToString("O") };

        Assert.True(AgentRunner.TryParseLastRunUtc(json["utc"]!.ToString(), out var readBack));
        Assert.Equal(DateTimeKind.Utc, readBack.Kind);
        Assert.Equal(written, readBack);
        Assert.False(AgentRunner.PlanSchedule(readBack, written.AddHours(22.1), RunHour, Pacific).CatchUp);
    }
}
