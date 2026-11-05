using System.Diagnostics;
using System.IO;
using GlDrive.Services;
using Xunit;

namespace GlDrive.Tests;

public sealed class UpdateProcessRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gldrive-update-process-{Guid.NewGuid():N}");

    public UpdateProcessRunnerTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void DrainsStderrBeforeStdoutClosesAndPreservesNonzeroExit()
    {
        var process = Powershell("[Console]::Error.Write(('e' * 262144)); [Console]::Out.Write('done'); exit 7");

        var result = UpdateProcessRunner.Run(process, TimeSpan.FromSeconds(15));

        Assert.False(result.TimedOut);
        Assert.Equal(7, result.ExitCode);
        Assert.Equal("done", result.StandardOutput);
        Assert.Equal(new string('e', 262144), result.StandardError);
    }

    [Fact]
    public void RegistrationDrainsStderrBeforeStdoutCloses()
    {
        File.WriteAllText(Path.Combine(_root, "register-update-task.ps1"),
            "param([string]$InstallDir)\n[Console]::Error.Write(('e' * 262144)); [Console]::Out.Write('registration-finished'); exit 0");
        var messages = new List<string>();

        UpdateInstaller.TryRegisterUpdateTask(_root, messages.Add, TimeSpan.FromSeconds(15));

        var result = Assert.Single(messages);
        Assert.StartsWith("Update task registration exit=0 registration-finished", result);
        Assert.EndsWith(new string('e', 262144), result);
    }

    [Fact]
    public void RegistrationTimeoutIncludesOutputReadsAndStopsTheChild()
    {
        var marker = Path.Combine(_root, "child-pid.txt");
        File.WriteAllText(Path.Combine(_root, "register-update-task.ps1"),
            "param([string]$InstallDir)\n[IO.File]::WriteAllText((Join-Path $InstallDir 'child-pid.txt'), [string]$PID); Start-Sleep -Seconds 60");
        var messages = new List<string>();
        var timer = Stopwatch.StartNew();
        try
        {
            UpdateInstaller.TryRegisterUpdateTask(_root, messages.Add, TimeSpan.FromSeconds(5));

            Assert.Equal("Update task registration timed out", Assert.Single(messages));
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(12), $"Timeout took {timer.Elapsed}");
            Assert.True(File.Exists(marker), "Synthetic registration did not reach its hang");
            Assert.False(IsRunning(int.Parse(File.ReadAllText(marker))));
        }
        finally
        {
            if (File.Exists(marker))
            {
                try { using var child = Process.GetProcessById(int.Parse(File.ReadAllText(marker))); child.Kill(true); }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
        }
    }

    private static bool IsRunning(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static ProcessStartInfo Powershell(string script)
    {
        var info = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-NonInteractive");
        info.ArgumentList.Add("-Command");
        info.ArgumentList.Add(script);
        return info;
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
