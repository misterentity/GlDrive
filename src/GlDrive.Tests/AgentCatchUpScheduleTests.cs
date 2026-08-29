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

    /// <summary>The production scenario end to end: parse the real stamp, then ask the real
    /// predicate. A ~22.1h gap is NOT a missed run and must not schedule a catch-up.</summary>
    [Fact]
    public void ParsedStamp_TwentyTwoHoursLater_DoesNotTriggerCatchUp()
    {
        Assert.True(AgentRunner.TryParseLastRunUtc(PersistedStamp, out var lastRun));
        var gapHours = (NextMorningUtc - lastRun).TotalHours;
        Assert.InRange(gapHours, 22.0, 22.2);
        Assert.False(AgentRunner.NeedsCatchUp(lastRun, NextMorningUtc));
    }

    /// <summary>The guard must still do its job: a genuinely missed run is caught up.</summary>
    [Fact]
    public void GenuinelyMissedRun_StillTriggersCatchUp()
    {
        Assert.True(AgentRunner.TryParseLastRunUtc(PersistedStamp, out var lastRun));
        Assert.True(AgentRunner.NeedsCatchUp(lastRun, lastRun.AddHours(23)));
        Assert.True(AgentRunner.NeedsCatchUp(lastRun, lastRun.AddHours(30)));
    }

    [Fact]
    public void NeverRun_DoesNotTriggerCatchUp()
    {
        Assert.False(AgentRunner.NeedsCatchUp(DateTime.MinValue, NextMorningUtc));
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
        Assert.False(AgentRunner.NeedsCatchUp(readBack, written.AddHours(22.1)));
    }
}
