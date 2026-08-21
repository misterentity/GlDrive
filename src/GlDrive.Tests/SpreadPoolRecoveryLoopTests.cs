using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Regression coverage for v3.10.73 production behavior: a spread-pool handshake
/// timed out while its main mount was recovering, permanently excluding that site
/// from racing until restart.
/// </summary>
public sealed class SpreadPoolRecoveryLoopTests
{
    [Fact]
    public async Task FailedAttemptIsRetriedUntilRecoverySucceeds()
    {
        var attempts = new List<int>();
        using var loop = ImmediateLoop();
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(loop.Schedule("site-a", (attempt, _) =>
        {
            lock (attempts) attempts.Add(attempt);
            if (attempt == 3) recovered.TrySetResult();
            return Task.FromResult(attempt == 3);
        }));

        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        lock (attempts) Assert.Equal([1, 2, 3], attempts);
    }

    [Fact]
    public async Task ScheduleIsSingleFlightPerServer()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var loop = new SpreadPoolRecoveryLoop(
            _ => TimeSpan.Zero,
            (_, ct) => release.Task.WaitAsync(ct));

        Assert.True(loop.Schedule("site-a", (_, _) => Task.FromResult(true)));
        Assert.False(loop.Schedule("site-a", (_, _) => Task.FromResult(true)));

        release.SetResult();
        await loop.CancelAsync("site-a");
    }

    [Fact]
    public async Task CancelStopsAWaitingRecoveryAndAllowsReschedule()
    {
        var enteredDelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var loop = new SpreadPoolRecoveryLoop(
            _ => TimeSpan.FromMinutes(5),
            async (_, ct) =>
            {
                enteredDelay.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            });

        Assert.True(loop.Schedule("site-a", (_, _) => Task.FromResult(false)));
        await enteredDelay.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await loop.CancelAsync("site-a").WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(loop.Schedule("site-a", (_, _) => Task.FromResult(true)));
    }

    [Fact]
    public async Task DifferentServersRecoverIndependently()
    {
        using var loop = ImmediateLoop();
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;

        Task<bool> Recover(int _, CancellationToken __)
        {
            if (Interlocked.Increment(ref count) == 2) recovered.TrySetResult();
            return Task.FromResult(true);
        }

        Assert.True(loop.Schedule("site-a", Recover));
        Assert.True(loop.Schedule("site-b", Recover));
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static SpreadPoolRecoveryLoop ImmediateLoop() =>
        new(_ => TimeSpan.Zero, (_, _) => Task.CompletedTask);
}
