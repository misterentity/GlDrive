using GlDrive.Config;

namespace GlDrive.Services.Recovery;

/// <summary>Automatic safe mode keeps updates alive and gives normal operation another chance.</summary>
internal static class SafeModeRecoverySchedule
{
    internal static TimeSpan ResumeDelay(RecoveryConfig settings)
    {
        var delays = settings.SafeModeRetryDelaysSeconds;
        var seconds = delays is { Length: > 0 }
            ? delays.Take(8).Sum(value => Math.Clamp(value, 3, 3600)) : 900;
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 60, 3600));
    }

    internal static async Task RunAsync(RecoveryConfig settings, Action startUpdates,
        Func<bool> tryResume, CancellationToken cancellationToken,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        delay ??= Task.Delay;
        cancellationToken.ThrowIfCancellationRequested();
        startUpdates();
        await delay(ResumeDelay(settings), cancellationToken);
        // An import or an installer already in progress owns shutdown until it finishes.
        // A failed launch gets another chance without requiring an attended desktop.
        while (!cancellationToken.IsCancellationRequested && !tryResume())
            await delay(TimeSpan.FromSeconds(30), cancellationToken);
    }
}
