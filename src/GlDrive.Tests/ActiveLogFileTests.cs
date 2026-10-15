using System;
using System.IO;
using System.Linq;
using GlDrive.Logging;
using GlDrive.Services.Automation;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// v3.11.9 — crash dumps and out-of-band log lines find the file Serilog is actually writing.
///
/// 26 of the 30 crashdump-*.log files on the production box end in "&lt;failed to read today's
/// log: The process cannot access the file ... because it is being used by another
/// process&gt;": <c>File.ReadAllLines</c> cannot share with the live sink, so every crash lost
/// the tail that explains it. And on 2026-10-13 the sink rolled to _001 (19:43) then _002
/// (19:54), while the watchdog kept appending "relaunched GlDrive" lines at 20:32-22:06 to the
/// stale base file, out of time order and invisible to a sweep of the live file.
/// </summary>
public sealed class ActiveLogFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gldrive-activelog-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Day = new(2026, 10, 13, 19, 35, 33);

    public ActiveLogFileTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Touch(string name, string content = "x\n")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void The_highest_roll_sequence_is_the_live_file()
    {
        Touch("gldrive-20261013.log");
        Touch("gldrive-20261013_001.log");
        var live = Touch("gldrive-20261013_002.log");
        Assert.Equal(live, ActiveLogFile.Find(_dir, Day));
    }

    [Fact]
    public void Sequence_is_numeric_not_lexical_and_not_by_write_time()
    {
        Touch("gldrive-20261013_009.log");
        var live = Touch("gldrive-20261013_010.log");
        // A rejected second launch appends to the BASE name after the roll: newest write,
        // wrong file.
        var baseFile = Touch("gldrive-20261013.log");
        File.SetLastWriteTimeUtc(baseFile, DateTime.UtcNow.AddMinutes(5));
        Assert.Equal(live, ActiveLogFile.Find(_dir, Day));
    }

    [Fact]
    public void Other_days_and_unrelated_names_are_ignored()
    {
        Touch("gldrive-20261012_005.log");
        Touch("gldrive-20261013_old.log");
        Touch("gldrive-202610130.log");
        Touch("crashdump-20261013-193533.log");
        var live = Touch("gldrive-20261013.log");
        Assert.Equal(live, ActiveLogFile.Find(_dir, Day));
    }

    [Fact]
    public void No_file_for_the_day_yields_null_and_the_base_name_for_writers()
    {
        Assert.Null(ActiveLogFile.Find(_dir, Day));
        Assert.Null(ActiveLogFile.Find(Path.Combine(_dir, "missing"), Day));
        Assert.Equal(Path.Combine(_dir, "gldrive-20261013.log"), ActiveLogFile.PathFor(_dir, Day));
    }

    [Fact]
    public void The_tail_is_readable_while_the_sink_holds_the_file_open()
    {
        var path = Path.Combine(_dir, "gldrive-20261013_002.log");
        // Serilog's shared file sink keeps a write handle open for the process lifetime.
        using var sink = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        using (var w = new StreamWriter(sink, leaveOpen: true))
        {
            for (var i = 0; i < 150; i++) w.WriteLine($"2026-10-13 19:35:{i % 60:00}.000 [INF] line {i}");
        }
        sink.Flush();

        // The old reader: this is the exception 26 of 30 dumps recorded.
        Assert.Throws<IOException>(() => File.ReadAllLines(path));

        var tail = DiagnosticAutomation.ReadTail(path, 100, 256 * 1024).Text.Split('\n');
        Assert.Equal(100, tail.Length);
        Assert.EndsWith("line 149", tail[^1].TrimEnd('\r'));
        Assert.EndsWith("line 50", tail[0].TrimEnd('\r'));
    }

    [Fact]
    public void AppendLine_lands_in_the_live_file_alongside_an_open_sink()
    {
        var baseFile = Touch("gldrive-" + DateTime.Now.ToString("yyyyMMdd") + ".log", "");
        var live = Path.Combine(_dir, "gldrive-" + DateTime.Now.ToString("yyyyMMdd") + "_001.log");
        using (new FileStream(live, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            ActiveLogFile.AppendLine(_dir, "2026-10-13 20:32:24.641 [INF] WATCHDOG: relaunched");
        }
        Assert.Contains("WATCHDOG: relaunched", File.ReadAllText(live));
        Assert.Equal("", File.ReadAllText(baseFile));
    }

    // ---- structural: every out-of-band writer and the crash dumper use it ----

    private static string Src(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { RepoRoot(), "src", "GlDrive" }.Concat(parts).ToArray()));

    [Fact]
    public void The_crash_dumper_reads_the_live_file_shared()
    {
        var app = Src("App.xaml.cs");
        var start = app.IndexOf("private static void WriteCrashDump", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var body = app[start..app.IndexOf("private int _exitMarked", start, StringComparison.Ordinal)];
        Assert.Contains("ActiveLogFile.Find(logsDir, now)", body);
        Assert.Contains("DiagnosticAutomation.ReadTail(", body);
        Assert.DoesNotContain("File.ReadAllLines", body);
    }

    [Fact]
    public void No_writer_hardcodes_the_base_log_name()
    {
        var program = Src("Program.cs");
        Assert.DoesNotContain("$\"gldrive-{DateTime.Now:yyyyMMdd}.log\"", program);
        Assert.DoesNotContain("File.AppendAllText(", program);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(program, @"ActiveLogFile\.AppendLine\(").Count);
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir, "src", "GlDrive", "App.xaml.cs"))) return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException("Could not locate the repository root from " + AppContext.BaseDirectory);
    }
}
