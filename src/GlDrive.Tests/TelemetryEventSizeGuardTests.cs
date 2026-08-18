using GlDrive.AiAgent;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// The write-side half of the 2026-08-14 poison-row defect (see AgentPromptBudgetTests).
///
/// A single RaceOutcomeEvent carrying a 2,000,000-char section was accepted by the recorder,
/// written to races-20260814.jsonl (2 MB for one row), then COPIED FORWARD by
/// SectionActivityRollup into a fresh stream the next day — 4 MB across two servers. Bounding
/// the prompt heals the read side; bounding the event stops the write side producing more.
///
/// A telemetry event is an aggregate row: server ids, section keys, counters. None of them are
/// large. Anything past the cap is not data, it is a defect somewhere upstream, and it must be
/// visible rather than silently written.
/// </summary>
public sealed class TelemetryEventSizeGuardTests
{
    [Fact]
    public void OversizedEvent_IsRejected()
    {
        var evt = new RaceOutcomeEvent
        {
            RaceId = "r1",
            Section = new string('A', 2_000_000),   // the exact 2026-08-14 payload
            Release = "x",
            Result = "aborted"
        };

        Assert.False(TelemetryRecorder.IsAcceptableSize(Serialize(evt)));
    }

    [Fact]
    public void NormalEvent_IsAccepted()
    {
        var evt = new RaceOutcomeEvent
        {
            RaceId = "3f5450a4-0808-4c7f-84a7-93b7515dc40b",
            Section = "tv-hd",
            Release = "Deadliest.Catch.S21E07.1080p.WEB.h264-EDITH",
            Result = "complete"
        };

        Assert.True(TelemetryRecorder.IsAcceptableSize(Serialize(evt)));
    }

    [Fact]
    public void Cap_LeavesGenerousRoomForRealEvents()
    {
        // A real race row with many participants is still small. The cap exists to catch
        // pathology, not to trim legitimate telemetry — set it far above anything organic.
        Assert.True(TelemetryRecorder.MaxEventBytes >= 64 * 1024);
    }

    [Fact]
    public void Cap_IsFarBelowWhatBrokeTheAgent()
    {
        // 2 MB in one row is what produced the 1.5M-token prompt.
        Assert.True(TelemetryRecorder.MaxEventBytes < 1_000_000);
    }

    private static string Serialize<T>(T evt) where T : TelemetryEnvelope =>
        System.Text.Json.JsonSerializer.Serialize(evt, evt!.GetType());
}
