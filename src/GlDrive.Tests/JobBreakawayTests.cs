using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using GlDrive.Services;
using static GlDrive.Services.JobBreakaway;
using Xunit;

namespace GlDrive.Tests;

public class JobBreakawayTests
{
    [Fact]
    public void Classify_maps_job_limit_flags()
    {
        Assert.Equal(JobState.NotInJob, Classify(false, null));
        Assert.Equal(JobState.NotInJob, Classify(false, JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE));
        Assert.Equal(JobState.Unknown, Classify(true, null));
        Assert.Equal(JobState.BreakawayAllowed, Classify(true, JOB_OBJECT_LIMIT_BREAKAWAY_OK));
        Assert.Equal(JobState.BreakawayAllowed,
            Classify(true, JOB_OBJECT_LIMIT_BREAKAWAY_OK | JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE));
        Assert.Equal(JobState.ChildrenLeaveAutomatically, Classify(true, JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK));
        Assert.Equal(JobState.BreakawayForbidden, Classify(true, 0));
        Assert.Equal(JobState.BreakawayForbidden, Classify(true, JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE));
    }

    [Fact]
    public void Breakaway_is_attempted_unless_it_is_impossible_or_unnecessary()
    {
        Assert.True(ShouldAttemptBreakaway(JobState.BreakawayAllowed));
        Assert.True(ShouldAttemptBreakaway(JobState.Unknown));
        Assert.False(ShouldAttemptBreakaway(JobState.NotInJob));
        Assert.False(ShouldAttemptBreakaway(JobState.ChildrenLeaveAutomatically));
        Assert.False(ShouldAttemptBreakaway(JobState.BreakawayForbidden));
    }

    [Fact]
    public void Log_note_is_silent_outside_a_job_and_warns_when_the_watchdog_shares_the_job()
    {
        Assert.Null(DescribeForLog(JobState.NotInJob, false, null));
        Assert.Null(DescribeForLog(JobState.ChildrenLeaveAutomatically, false, null));

        var ok = DescribeForLog(JobState.BreakawayAllowed, brokeAway: true, null);
        Assert.NotNull(ok);
        Assert.False(ok!.IsWarning);

        var forbidden = DescribeForLog(JobState.BreakawayForbidden, brokeAway: false, null);
        Assert.NotNull(forbidden);
        Assert.True(forbidden!.IsWarning);
        Assert.Contains("die together", forbidden.Text);

        var failed = DescribeForLog(JobState.BreakawayAllowed, brokeAway: false, "Access is denied.");
        Assert.True(failed!.IsWarning);
        Assert.Contains("Access is denied.", failed.Text);
    }

    [Fact]
    public void Program_spawns_the_watchdog_through_the_breakaway_launcher()
    {
        var program = File.ReadAllText(FindRepoFile("src/GlDrive/Program.cs"));
        var spawn = program.IndexOf("private static void SpawnWatchdog()", StringComparison.Ordinal);
        var breakaway = program.IndexOf("JobBreakaway.StartBrokenAway(exe, $\"--watchdog", spawn, StringComparison.Ordinal);
        var fallback = program.IndexOf("Process.Start(psi)", spawn, StringComparison.Ordinal);
        Assert.True(spawn >= 0 && breakaway > spawn && fallback > breakaway,
            "SpawnWatchdog must try the job breakaway before the plain Process.Start fallback");

        var app = File.ReadAllText(FindRepoFile("src/GlDrive/App.xaml.cs"));
        Assert.Contains("Program.WatchdogJobNote", app, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reproduces the 2026-09-23 outage shape: the process that spawns the watchdog lives in a
    /// job. A plain child inherits the job (and would be killed with it); the breakaway child
    /// must not.
    /// </summary>
    [Fact]
    public void Breakaway_child_is_outside_the_parents_job_while_a_plain_child_is_inside()
    {
        if (!OperatingSystem.IsWindows()) return;
        // A pre-existing job that forbids breakaway (some CI runners) makes this unobservable.
        if (CurrentState() is JobState.BreakawayForbidden or JobState.ChildrenLeaveAutomatically) return;

        var job = CreateJobObjectW(IntPtr.Zero, null);
        Assert.NotEqual(IntPtr.Zero, job);
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_BREAKAWAY_OK;
        Assert.True(SetInformationJobObject(job, 9, ref info, Marshal.SizeOf(info)));
        Assert.True(AssignProcessToJobObject(job, Process.GetCurrentProcess().Handle),
            $"AssignProcessToJobObject failed: {Marshal.GetLastWin32Error()}");

        Assert.Equal(JobState.BreakawayAllowed, CurrentState());

        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        const string args = "/c ping -n 6 127.0.0.1 >nul";
        using var plain = Process.Start(new ProcessStartInfo(cmd, args) { UseShellExecute = false, CreateNoWindow = true })!;
        using var detached = Process.GetProcessById(StartBrokenAway(cmd, args));
        try
        {
            Assert.True(IsProcessInJob(plain.Handle, job, out var plainInJob));
            Assert.True(plainInJob, "control: a plain child must inherit the job");

            Assert.True(IsProcessInJob(detached.Handle, job, out var detachedInJob));
            Assert.False(detachedInJob, "the breakaway child must not be in the parent's job");
        }
        finally
        {
            try { plain.Kill(); } catch { }
            try { detached.Kill(); } catch { }
        }
    }

    private static string FindRepoFile(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            var candidate = Path.Combine(dir, relative);
            if (File.Exists(candidate)) return candidate;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException($"Could not locate {relative}");
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
}
