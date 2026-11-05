using System.IO;
using Xunit;

namespace GlDrive.Tests;

public class WatchdogCrashAttributionTests
{
    private const string WerMessage = """
        Faulting application name: GlDrive.exe, version: 3.20.8.0, time stamp: 0x653fab24
        Faulting module name: coreclr.dll, version: 10.0.125.57005, time stamp: 0x15b851a4
        Exception code: 0xc0000005
        Faulting process id: 0x908
        """;

    [Fact]
    public void Runtime_1026_matches_only_the_crashed_process()
    {
        Assert.True(Program.CrashEventBelongsTo(1026, 72000, null, "Application: GlDrive.exe", 72000));
        // A ReleaseCheck/testhost crash whose stack mentions GlDrive, seconds before ours.
        Assert.False(Program.CrashEventBelongsTo(1026, 87940, null, "at GlDrive.Ftp.LoginAttemptGovernor.Deny", 72000));
        Assert.False(Program.CrashEventBelongsTo(1026, null, null, "Application: GlDrive.exe", 72000));
    }

    [Fact]
    public void Application_error_1000_matches_the_faulting_pid_not_the_wer_writer()
    {
        Assert.True(Program.CrashEventBelongsTo(1000, 46248, "0x908", WerMessage, 0x908));
        Assert.False(Program.CrashEventBelongsTo(1000, 46248, "0x908", WerMessage, 46248));
        Assert.False(Program.CrashEventBelongsTo(1000, 46248, "0x909", WerMessage, 0x908));
    }

    [Fact]
    public void Application_error_1000_falls_back_to_the_message_line()
    {
        Assert.True(Program.CrashEventBelongsTo(1000, 46248, null, WerMessage, 0x908));
        Assert.False(Program.CrashEventBelongsTo(1000, 46248, null, WerMessage, 0x909));
        Assert.False(Program.CrashEventBelongsTo(1000, 46248, null, "no pid here", 0x908));
    }

    [Fact]
    public void Other_events_never_match() =>
        Assert.False(Program.CrashEventBelongsTo(1001, 0x908, "0x908", WerMessage, 0x908));

    [Fact]
    public void Crash_reason_query_filters_by_the_target_pid()
    {
        var program = File.ReadAllText(Path.Combine(RepoRoot(), "src", "GlDrive", "Program.cs"));
        var body = LifecycleSourceGuard.BlockAfter(program, "private static string GetCrashReason(int pid)");
        Assert.Contains("if (!CrashEventBelongsTo(evt.Id, evt.ProcessId, faultingPid, msg, pid)) continue;", body);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "GlDrive.sln"))) return dir.FullName;
        throw new InvalidOperationException("Could not locate repository root");
    }
}
