using System.Diagnostics;

namespace GlDrive.Services;

internal sealed record UpdateProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut);

/// <summary>Bounds both process exit and redirected output for the fixed updater helpers.</summary>
internal static class UpdateProcessRunner
{
    internal static UpdateProcessResult Run(ProcessStartInfo startInfo, TimeSpan timeout)
    {
        using var process = Process.Start(startInfo);
        if (process == null) return new(-1, "", "", false);

        using var deadline = new CancellationTokenSource(timeout);
        // Drain both pipes immediately. Reading either pipe synchronously can wait forever
        // for EOF, or deadlock against a child blocked while writing the other pipe.
        var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
        var completion = Task.WhenAll(process.WaitForExitAsync(deadline.Token), stdout, stderr);
        try
        {
            completion.WaitAsync(deadline.Token).GetAwaiter().GetResult();
            return new(process.ExitCode, stdout.Result, stderr.Result, false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            Stop(process);
            _ = completion.ContinueWith(task => _ = task.Exception,
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            return new(-1, "", "", true);
        }
        catch
        {
            // An output read failure must not orphan an elevated helper either.
            deadline.Cancel();
            Stop(process);
            throw;
        }
    }

    private static void Stop(Process process)
    {
        try { process.Kill(entireProcessTree: true); } catch { }
        // Do not leave the elevated helper running after we report its timeout.
        // Failure to reap it is still bounded so registration cannot hold relaunch.
        try { process.WaitForExit(5_000); } catch { }
    }
}
