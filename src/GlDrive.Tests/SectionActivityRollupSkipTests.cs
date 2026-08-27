using System;
using System.IO;
using System.Linq;
using GlDrive.AiAgent;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// v3.10.80 fixed the silent parse-skip in <see cref="LogDigester"/> but swept only that one
/// reader. Three siblings kept the identical <c>catch { continue; }</c>: AuditTrail.ReadAll,
/// NukePoller.TryCorrelateRace, and SectionActivityRollup.RollUp.
///
/// RollUp is the one that matters for the agent's view of the world: it is the ONLY producer of
/// SectionActivityEvent, so a race row dropped here under-reports that section's activity for the
/// whole day, and — before this fix — left no trace at any log level.
///
/// Same contract as DigesterParseSkipVisibilityTests: skip the row, never the stream, but count it.
/// </summary>
public sealed class SectionActivityRollupSkipTests
{
    private static string NewRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gldrive-rollup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string RaceRow(string id, string server) =>
        $"{{\"raceId\":\"{id}\",\"section\":\"tv-hd\",\"release\":\"R{id}\",\"winner\":\"{server}\"," +
        $"\"participants\":[{{\"serverId\":\"{server}\",\"files\":3,\"bytes\":100}}]}}";

    private static SectionActivityRollup Rollup(string root) =>
        new(new TelemetryRecorder(root, 64), root);

    [Fact]
    public void MalformedRaceRow_IsSkippedButCounted()
    {
        var root = NewRoot();
        try
        {
            var day = new DateTime(2026, 8, 25);
            File.WriteAllText(Path.Combine(root, "races-20260825.jsonl"),
                RaceRow("r1", "superbnc") + "\n" +
                "{this is not json\n" +
                RaceRow("r2", "superbnc") + "\n");

            var r = Rollup(root);
            r.RollUp(day);

            Assert.Equal(1, r.ParseSkips);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void CleanFile_ReportsNoSkips()
    {
        // Guards against a counter that always fires, which would make "0 skips" meaningless.
        var root = NewRoot();
        try
        {
            var day = new DateTime(2026, 8, 25);
            File.WriteAllText(Path.Combine(root, "races-20260825.jsonl"),
                RaceRow("r1", "superbnc") + "\n" + RaceRow("r2", "superbnc") + "\n");

            var r = Rollup(root);
            r.RollUp(day);

            Assert.Equal(0, r.ParseSkips);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void SkipCount_ResetsBetweenRuns()
    {
        // A sticky counter would report yesterday's damage against today's file.
        var root = NewRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, "races-20260825.jsonl"),
                RaceRow("r1", "superbnc") + "\n" + "{bad\n");
            File.WriteAllText(Path.Combine(root, "races-20260826.jsonl"),
                RaceRow("r2", "superbnc") + "\n");

            var r = Rollup(root);
            r.RollUp(new DateTime(2026, 8, 25));
            Assert.Equal(1, r.ParseSkips);

            r.RollUp(new DateTime(2026, 8, 26));
            Assert.Equal(0, r.ParseSkips);

            // A day with no races file at all must also clear the count rather than
            // leaving the previous day's damage reported against it.
            r.RollUp(new DateTime(2026, 8, 25));
            Assert.Equal(1, r.ParseSkips);
            r.RollUp(new DateTime(2026, 8, 27));   // no races-20260827.jsonl exists
            Assert.Equal(0, r.ParseSkips);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void BomPrefixedFirstRow_IsStillReadable()
    {
        // Files written by pre-v3.10.81 builds carry a BOM on line 1; they must keep rolling up.
        var root = NewRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, "races-20260825.jsonl"),
                RaceRow("r1", "superbnc") + "\n" + RaceRow("r2", "superbnc") + "\n",
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            var r = Rollup(root);
            r.RollUp(new DateTime(2026, 8, 25));

            Assert.Equal(0, r.ParseSkips);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
