using System;
using System.IO;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// A borrower waiting at capacity used to wait on the channel alone. A connection that is
/// quarantined (discarded) while it waits never comes back through the channel, so the waiter
/// hung until its own deadline — or forever, for a caller without one. Found by the Codex review
/// of multi-stream downloads: a download dropping a borrowed race login after a failed transfer
/// while a race sat waiting for that login. Borrow needs a live FTPS endpoint, so this pins the
/// wiring in source, the way <see cref="BorrowCancellationWiringTests"/> does.
/// </summary>
public class PoolSlotFreedWakeTests
{
    private static string ReadPoolSource()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12 && dir != null; i++)
        {
            var candidate = Path.Combine(dir, "src", "GlDrive", "Ftp", "FtpConnectionPool.cs");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException("Could not locate src/GlDrive/Ftp/FtpConnectionPool.cs");
    }

    private static string Section(string source, string from, string to)
    {
        var start = source.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{from}' not found");
        var end = source.IndexOf(to, start + from.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"'{to}' not found after '{from}'");
        return source[start..end];
    }

    [Fact]
    public void Quarantine_SignalsThatASlotFreed()
    {
        var quarantine = Section(ReadPoolSource(), "internal void Quarantine(", "_ = Task.Run(");
        Assert.Contains("ReleasePermit();", quarantine);
        Assert.Contains("_slotFreed", quarantine);
        Assert.Contains("TrySetResult()", quarantine);
    }

    [Fact]
    public void Quarantine_AdvancesTheFreedGeneration()
    {
        var quarantine = Section(ReadPoolSource(), "internal void Quarantine(", "_ = Task.Run(");
        Assert.Contains("Interlocked.Increment(ref _freedGeneration)", quarantine);
    }

    [Fact]
    public void Waiter_RacesTheChannelAgainstAFreedSlot_AndRechecksPeriodically()
    {
        var helper = Section(ReadPoolSource(), "private async Task<AsyncFtpClient?> WaitForReturnedConnectionAsync(", "public int ActiveCount");
        // Snapshot the pulse before checking state, or a quarantine in between is missed.
        Assert.True(helper.IndexOf("_slotFreed", StringComparison.Ordinal) < helper.IndexOf("TryRead", StringComparison.Ordinal));
        Assert.Contains("WaitToReadAsync", helper);
        Assert.Contains("Task.WhenAny(readable, freed, Task.Delay(SlotRecheckInterval, ct))", helper);
        // Re-run Borrow only for a slot freed AFTER Borrow began — never just because capacity
        // looks free, which is the state right after a failed connect (reconnect storm).
        Assert.Contains("_freedGeneration) != freedGeneration", helper);
    }

    [Fact]
    public void Borrow_NeverWaitsOnTheChannelAlone()
    {
        var source = ReadPoolSource();
        Assert.Contains("public Task<PooledConnection> Borrow(CancellationToken ct = default) => BorrowFor(FtpBorrowPurpose.Unspecified, ct);", source);
        var borrow = Section(source, "public async Task<PooledConnection> BorrowFor(", "internal void Return(");
        Assert.DoesNotContain("_pool.Reader.ReadAsync(", borrow);
        Assert.Contains("WaitForReturnedConnectionAsync(parked: true, freedGeneration, ct)", borrow);
        Assert.Contains("WaitForReturnedConnectionAsync(parked: false, freedGeneration, ct)", borrow);
        // A null wait result re-runs Borrow so a fresh connect goes through the normal gated,
        // failure-classified path rather than a side door.
        Assert.Contains("if (client == null) return await BorrowFor(purpose, ct);", borrow);
    }
}
