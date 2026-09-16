using System.Reflection;
using GlDrive.Config;
using GlDrive.Ftp;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

public sealed class SpreadScanCancellationTests
{
    private sealed class CancellingGate(Action onAcquire) : IAccountLoginGate
    {
        public int Attempts { get; private set; }
        public Task<bool> TryAcquireAsync(CancellationToken ct, TimeSpan? timeout = null, bool priority = false)
        {
            Attempts++;
            onAcquire();
            // No network connection can be opened by these fixtures.
            throw new OperationCanceledException(ct);
        }
        public void Release(bool priority = false) { }
        public int Held => 0;
        public int Limit => 4;
        public int Reserved => 0;
        public int PriorityLimit => 4;
        public int GeneralLimit => 4;
        public void TightenTo(int newLimit) { }
    }

    [Theory]
    [InlineData(true, true, 0)] // Stop during main borrow: never fall back.
    [InlineData(true, false, 1)] // Internal main timeout: fallback still allowed, then stop.
    [InlineData(false, false, 1)] // Stop during a scan with only a spread pool.
    public async Task CallerCancellationEscapesScanWithoutFurtherBorrowOrFailureAccounting(
        bool hasMain, bool cancelMain, int fallbackAttempts)
    {
        using var stop = new CancellationTokenSource();
        var mainGate = new CancellingGate(() => { if (cancelMain) stop.Cancel(); });
        var fallbackGate = new CancellingGate(stop.Cancel);
        await using var main = new FtpConnectionPool(null!, 3, mainGate);
        await using var fallback = new FtpConnectionPool(null!, 3, fallbackGate);
        using var job = new SpreadJob("TV", "Fixture.S01E01-GROUP", SpreadMode.Race,
            new SpreadConfig(), new() { ["fixture"] = fallback },
            hasMain ? new() { ["fixture"] = main } : new(),
            new() { ["fixture"] = new ServerConfig { Name = "Cancellation fixture" } },
            new SpeedTracker(), new SkiplistEvaluator());
        var scan = typeof(SpreadJob).GetMethod("ScanSites", BindingFlags.Instance | BindingFlags.NonPublic)!;

        var task = (Task)scan.Invoke(job, [new Dictionary<string, string> { ["fixture"] = "/fixture" }, stop.Token])!;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(hasMain ? 1 : 0, mainGate.Attempts);
        Assert.Equal(fallbackAttempts, fallbackGate.Attempts);
        Assert.Equal(0, main.ActiveCount);
        Assert.Equal(0, fallback.ActiveCount);
    }
}
