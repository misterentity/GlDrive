using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GlDrive.AiAgent;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// The audit trail is the undo/forensics record for AI-driven AppConfig mutations, so it is the
/// one ai-data file where losing a row has a consequence beyond a skewed digest.
///
/// Two defects found in the 2026-08-26 sweep, both siblings of v3.10.80's TelemetryRecorder fix
/// that the original sweep did not reach:
///   1. Writes used <c>Encoding.UTF8</c> (BOM-emitting), stamping a BOM at position 0.
///   2. <c>MarkUndone</c> rebuilt the file from <c>ReadAll()</c>, which silently dropped rows it
///      could not deserialize — so the rewrite ERASED them permanently.
///
/// These are behavioural tests against real files on disk: they assert the bytes and the surviving
/// rows, not the shape of the source, so a rename or refactor cannot make them vacuous.
/// </summary>
public class AuditTrailDurabilityTests : IDisposable
{
    private readonly string _dir;
    private readonly string _file;

    public AuditTrailDurabilityTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "gldrive-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "ai-audit.jsonl");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static AuditRow Row(string runId, string target, bool applied = true) => new()
    {
        RunId = runId,
        Target = target,
        Category = "sectionMapping",
        Applied = applied,
        Confidence = 0.9
    };

    // ---- 1. BOM ----------------------------------------------------------------

    [Fact]
    public void Append_writes_no_utf8_bom()
    {
        var trail = new AuditTrail(_dir);
        trail.Append(Row("run-1", "tv-hd"));

        var bytes = File.ReadAllBytes(_file);
        Assert.True(bytes.Length >= 3);
        // EF BB BF is the UTF-8 BOM that Encoding.UTF8 emits at position 0.
        Assert.False(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "audit trail must not start with a UTF-8 BOM");
        Assert.Equal((byte)'{', bytes[0]);
    }

    [Fact]
    public void MarkUndone_rewrite_writes_no_utf8_bom()
    {
        var trail = new AuditTrail(_dir);
        trail.Append(Row("run-1", "tv-hd"));
        trail.MarkUndone("run-1", "tv-hd", "operator undo");

        var bytes = File.ReadAllBytes(_file);
        Assert.False(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "MarkUndone rewrite must not reintroduce a BOM");
    }

    // ---- 2. MarkUndone must not erase unparseable rows --------------------------

    [Fact]
    public void MarkUndone_preserves_unparseable_rows_verbatim()
    {
        const string corrupt = "{\"RunId\":\"run-0\",\"Target\":\"trunc";

        var trail = new AuditTrail(_dir);
        trail.Append(Row("run-1", "tv-hd"));
        // Simulate a torn write (crash mid-append / disk full) between two good rows.
        File.AppendAllText(_file, corrupt + "\n", new UTF8Encoding(false));
        trail.Append(Row("run-2", "movies"));

        trail.MarkUndone("run-1", "tv-hd", "operator undo");

        var lines = File.ReadAllLines(_file).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
        Assert.Equal(3, lines.Length);
        Assert.Contains(corrupt, lines);
    }

    [Fact]
    public void MarkUndone_marks_only_the_matching_row()
    {
        var trail = new AuditTrail(_dir);
        trail.Append(Row("run-1", "tv-hd"));
        trail.Append(Row("run-2", "movies"));

        trail.MarkUndone("run-1", "tv-hd", "operator undo");

        var rows = trail.ReadAll().ToList();
        Assert.Equal(2, rows.Count);
        var undone = Assert.Single(rows, r => r.Undone);
        Assert.Equal("run-1", undone.RunId);
        Assert.Equal("operator undo", undone.UndoneReason);
        Assert.False(rows.Single(r => r.RunId == "run-2").Undone);
    }

    [Fact]
    public void MarkUndone_leaves_untouched_rows_byte_identical()
    {
        var trail = new AuditTrail(_dir);
        trail.Append(Row("run-1", "tv-hd"));
        trail.Append(Row("run-2", "movies"));
        var before = File.ReadAllLines(_file).Single(l => l.Contains("run-2"));

        trail.MarkUndone("run-1", "tv-hd", "operator undo");

        var after = File.ReadAllLines(_file).Single(l => l.Contains("run-2"));
        Assert.Equal(before, after);
    }

    // ---- 3. Line endings stay consistent ---------------------------------------

    [Fact]
    public void MarkUndone_does_not_introduce_crlf_line_endings()
    {
        var trail = new AuditTrail(_dir);
        trail.Append(Row("run-1", "tv-hd"));
        trail.Append(Row("run-2", "movies"));

        trail.MarkUndone("run-1", "tv-hd", "operator undo");

        // Append uses "\n"; a StringBuilder.AppendLine rewrite would emit "\r\n" and leave
        // the file with mixed endings after the first undo.
        var text = File.ReadAllText(_file);
        Assert.DoesNotContain("\r\n", text);
    }

    // ---- 4. Parse skips are counted, not swallowed ------------------------------

    [Fact]
    public void ReadAll_counts_unparseable_rows()
    {
        var trail = new AuditTrail(_dir);
        trail.Append(Row("run-1", "tv-hd"));
        File.AppendAllText(_file, "{not json\n", new UTF8Encoding(false));
        trail.Append(Row("run-2", "movies"));

        var rows = trail.ReadAll().ToList();

        Assert.Equal(2, rows.Count);
        Assert.Equal(1, trail.ParseSkips);
    }

    [Fact]
    public void ReadAll_reports_zero_skips_on_a_clean_file()
    {
        var trail = new AuditTrail(_dir);
        trail.Append(Row("run-1", "tv-hd"));

        _ = trail.ReadAll().ToList();

        Assert.Equal(0, trail.ParseSkips);
    }

    // ---- 5. Round-trip sanity ---------------------------------------------------

    [Fact]
    public void Rows_survive_a_write_read_roundtrip_with_non_ascii_targets()
    {
        var trail = new AuditTrail(_dir);
        trail.Append(Row("run-1", "tv-hd-éü-日本"));

        var row = Assert.Single(trail.ReadAll());
        Assert.Equal("tv-hd-éü-日本", row.Target);
    }
}
