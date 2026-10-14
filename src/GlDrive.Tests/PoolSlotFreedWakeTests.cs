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
    public void AtCapacityWait_RacesTheChannelAgainstAFreedSlot()
    {
        var wait = Section(ReadPoolSource(), "At capacity — wait for one to be returned", "CheckPooledForBorrow(client, ct)");
        Assert.Contains("WaitToReadAsync", wait);
        Assert.Contains("_slotFreed", wait);
        Assert.Contains("Task.WhenAny(readable, freed)", wait);
        // A freed slot must still honor the parked states and the gate-derived ceiling.
        Assert.Contains("IsThrottled", wait);
        Assert.Contains("EffectiveMaxSize", wait);
        Assert.Contains("AcquireAndConnect(ct)", wait);
        Assert.DoesNotContain("_pool.Reader.ReadAsync(ct)", wait);
    }
}
