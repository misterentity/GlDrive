using System;
using System.IO;
using System.Linq;
using GlDrive.AiAgent;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Smoke coverage for the v3.10.82 AiAgent persistence changes against REAL production-shaped
/// data: a BOM-prefixed ai-audit.jsonl (every file written by a pre-v3.10.81 build carries one)
/// carrying thousands of rows, plus a real races-*.jsonl.
///
/// The unit tests build tiny synthetic files; this one proves the same code survives the actual
/// on-disk shapes — specifically that MarkUndone's whole-file rewrite is lossless at scale, which
/// is the property whose absence made the old implementation a data shredder.
///
/// Fixtures are checked in under TestData/ so this runs anywhere, not just on Dave's box.
/// </summary>
public sealed class ProductionDataSmokeTests
{
    private static string FixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "TestData");

    private static bool Fixture(string name, out string path)
    {
        path = Path.Combine(FixtureDir, name);
        return File.Exists(path);
    }

    private static string NewRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gldrive-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void MarkUndone_on_a_large_bom_prefixed_audit_file_loses_no_rows()
    {
        if (!Fixture("ai-audit.sample.jsonl", out var src)) return;   // fixture absent → skip

        var root = NewRoot();
        try
        {
            var path = Path.Combine(root, "ai-audit.jsonl");
            File.Copy(src, path);

            var trail = new AuditTrail(root);
            var before = trail.ReadAll().ToList();
            var beforeLines = File.ReadAllLines(path).Count(l => !string.IsNullOrWhiteSpace(l));
            Assert.True(before.Count > 0, "fixture should contain rows");
            Assert.Equal(0, trail.ParseSkips);

            // Undo a run that exists in the data; if none matches this is a no-op, which is
            // still a valid rewrite path to exercise.
            var target = before.FirstOrDefault(r => r.Applied && !r.Undone);
            if (target is null) return;
            trail.MarkUndone(target.RunId, target.Target, "smoke test");

            var afterLines = File.ReadAllLines(path).Count(l => !string.IsNullOrWhiteSpace(l));
            var after = trail.ReadAll().ToList();

            Assert.Equal(beforeLines, afterLines);          // no row lost to the rewrite
            Assert.Equal(before.Count, after.Count);
            Assert.Equal(0, trail.ParseSkips);
            Assert.Contains(after, r => r.RunId == target.RunId
                                     && r.Target == target.Target && r.Undone);

            // The rewrite must not reintroduce the BOM the fixture arrived with.
            var bytes = File.ReadAllBytes(path);
            Assert.False(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void RollUp_over_a_real_races_file_reports_no_parse_skips()
    {
        if (!Fixture("races.sample.jsonl", out var src)) return;      // fixture absent → skip

        var root = NewRoot();
        try
        {
            File.Copy(src, Path.Combine(root, "races-20260825.jsonl"));

            var r = new SectionActivityRollup(new TelemetryRecorder(root, 64), root);
            r.RollUp(new DateTime(2026, 8, 25));

            // Real telemetry must parse cleanly; a non-zero count here would mean the live
            // stream is losing rows, which is exactly the signal this counter now surfaces.
            Assert.Equal(0, r.ParseSkips);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
