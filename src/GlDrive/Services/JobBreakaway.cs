using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace GlDrive.Services;

/// <summary>
/// Launches the crash watchdog OUTSIDE any job object the app inherited from its launcher.
///
/// 2026-09-23: an automation shell restarted GlDrive via Restart Manager, so the app, its
/// watchdog and the watchdog's post-update relaunch all inherited that shell's job. When the
/// shell's session ended at 10:27 the job was terminated: app and watchdog died in the same
/// instant, no crash event, no restart, 9.5 h of downtime. A watchdog that shares its target's
/// job cannot outlive it, so it must break away. Where the job forbids breakaway we fall back to
/// a normal launch and say so at startup, which is the only evidence such a death leaves.
/// </summary>
internal static class JobBreakaway
{
    internal const uint JOB_OBJECT_LIMIT_BREAKAWAY_OK = 0x00000800;
    internal const uint JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK = 0x00001000;
    internal const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    private const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000;
    private const uint CREATE_NO_WINDOW = 0x08000000;

    internal enum JobState { NotInJob, BreakawayAllowed, ChildrenLeaveAutomatically, BreakawayForbidden, Unknown }

    internal static JobState Classify(bool inJob, uint? limitFlags)
    {
        if (!inJob) return JobState.NotInJob;
        if (limitFlags is not { } flags) return JobState.Unknown;
        if ((flags & JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK) != 0) return JobState.ChildrenLeaveAutomatically;
        if ((flags & JOB_OBJECT_LIMIT_BREAKAWAY_OK) != 0) return JobState.BreakawayAllowed;
        return JobState.BreakawayForbidden;
    }

    internal static JobState CurrentState()
    {
        try
        {
            if (!IsProcessInJob(GetCurrentProcess(), IntPtr.Zero, out var inJob)) return JobState.Unknown;
            return Classify(inJob, inJob ? QueryCurrentJobLimitFlags() : null);
        }
        catch
        {
            return JobState.Unknown;
        }
    }

    internal sealed record LogNote(bool IsWarning, string Text);

    /// <summary>Startup note for the log, or null when the watchdog is independent of the launcher.</summary>
    internal static LogNote? DescribeForLog(JobState state, bool brokeAway, string? breakawayError)
    {
        if (state is JobState.NotInJob or JobState.ChildrenLeaveAutomatically) return null;
        if (brokeAway)
            return new LogNote(false,
                "launched inside a job object; watchdog spawned outside it so it survives the launcher's job being terminated");
        return new LogNote(true,
            $"launched inside a job object (state={state}) and the watchdog could not break away" +
            (breakawayError is null ? "" : $" ({breakawayError})") +
            ". If the launcher's job is terminated, GlDrive and its watchdog die together with no crash " +
            "event and no restart - relaunch GlDrive from the desktop to restore crash recovery");
    }

    internal static bool ShouldAttemptBreakaway(JobState state) =>
        state is JobState.BreakawayAllowed or JobState.Unknown;

    /// <summary>
    /// Starts <paramref name="exe"/> with CREATE_BREAKAWAY_FROM_JOB. Returns the PID, or throws
    /// <see cref="Win32Exception"/> (ERROR_ACCESS_DENIED when the job forbids breakaway).
    /// </summary>
    internal static int StartBrokenAway(string exe, string arguments)
    {
        var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
        var cmd = new StringBuilder($"\"{exe}\" {arguments}");
        if (!CreateProcessW(exe, cmd, IntPtr.Zero, IntPtr.Zero, false,
                CREATE_BREAKAWAY_FROM_JOB | CREATE_NO_WINDOW, IntPtr.Zero,
                Path.GetDirectoryName(exe), ref si, out var pi))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        return pi.dwProcessId;
    }

    private static uint? QueryCurrentJobLimitFlags()
    {
        // JOBOBJECT_EXTENDED_LIMIT_INFORMATION (class 9); a null job handle means "my job".
        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryInformationJobObject(IntPtr.Zero, 9, buf, size, out _)) return null;
            return Marshal.PtrToStructure<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>(buf).BasicLimitInformation.LimitFlags;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string lpApplicationName, StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags,
        IntPtr lpEnvironment, string? lpCurrentDirectory, ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(IntPtr job, int infoClass, IntPtr info, int length, out int returnLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
