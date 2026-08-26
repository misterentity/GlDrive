using System.IO;
using GlDrive.AiAgent;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// LogDigester.ReadFile swallowed every unparseable telemetry row with
/// <c>Log.Debug(ex, "digester parse skip ...")</c>. The Serilog sink for this app runs at
/// Information, so those lines are never written anywhere — a dropped row left no trace at all.
///
/// That is the same blind spot that let the 2026-08-14 poison row brick the agent for 40+
/// consecutive runs before anyone noticed. The write side already learned this lesson: the
/// oversize-event guard in TelemetryRecorder logs at Warning and explicitly comments that a Debug
/// line would be invisible. The read side had not, so the two halves of one subsystem disagreed
/// about whether losing evidence is worth mentioning.
///
/// Rows are still skipped rather than aborting the digest — one bad row must not cost the whole
/// stream — but the count is now surfaced.
/// </summary>
public sealed class DigesterParseSkipVisibilityTests
{
    private static string NewRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gldrive-digest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string RaceRow(string id) =>
        $"{{\"raceId\":\"{id}\",\"section\":\"tv-hd\",\"release\":\"R\",\"result\":\"complete\"}}";

    [Fact]
    public void MalformedRows_AreSkippedButCounted()
    {
        var root = NewRoot();
        try
        {
            var day = new DateTime(2026, 8, 25);
            File.WriteAllText(Path.Combine(root, "races-20260825.jsonl"),
                RaceRow("r1") + "\n" +
                "{this is not json\n" +           // malformed row
                RaceRow("r2") + "\n");

            var d = new LogDigester(root);
            var events = d.ReadStream<RaceOutcomeEvent>("races", day, day).ToList();

            Assert.Equal(2, events.Count);          // good rows still come through
            Assert.Equal(1, d.ParseSkips);          // and the loss is visible
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void CleanFile_ReportsNoSkips()
    {
        // Guards against a counter that always fires — the failure mode that makes a
        // "0 skips" reading meaningless.
        var root = NewRoot();
        try
        {
            var day = new DateTime(2026, 8, 25);
            File.WriteAllText(Path.Combine(root, "races-20260825.jsonl"),
                RaceRow("r1") + "\n" + RaceRow("r2") + "\n");

            var d = new LogDigester(root);
            var events = d.ReadStream<RaceOutcomeEvent>("races", day, day).ToList();

            Assert.Equal(2, events.Count);
            Assert.Equal(0, d.ParseSkips);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void BomPrefixedFirstRow_IsStillReadable()
    {
        // The historical on-disk shape: files already written with a BOM must keep working
        // after the writer stops emitting one. Regression guard for the fix in
        // TelemetryFileEncodingTests.
        var root = NewRoot();
        try
        {
            var day = new DateTime(2026, 8, 25);
            var path = Path.Combine(root, "races-20260825.jsonl");
            File.WriteAllText(path, RaceRow("r1") + "\n" + RaceRow("r2") + "\n",
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            var d = new LogDigester(root);
            var events = d.ReadStream<RaceOutcomeEvent>("races", day, day).ToList();

            Assert.Equal(2, events.Count);
            Assert.Equal(0, d.ParseSkips);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
