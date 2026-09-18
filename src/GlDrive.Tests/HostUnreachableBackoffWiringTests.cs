using System;
using System.Linq;
using System.IO;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// v3.10.123 — pins the three wirings the unreachable backoff needs to be anything
/// other than decoration. The verdict and the diagnosis are unit-tested in
/// <see cref="HostUnreachableAttributionTests"/>; these exist because each of the
/// three below has a silent-failure precedent in this repo:
///
///   * armed but never READ — v3.10.54's scan-yield guard was sized against the wrong
///     number and fired 0 times in 534 evaluations while nothing failed loudly. The
///     Borrow early-out is the only place the pool declines to open a connection, so a
///     deadline it does not consult parks nothing.
///   * a snapshot passing a LITERAL instead of the live property — v3.10.78's guard
///     asserted only that an argument NAME appeared, which also passed the inert
///     mutant. So each check here demands the property and rejects the literal.
///   * the stack surviving on the unreachable path — the 14,219 identical stacks of
///     2026-09-17 were the whole log-volume defect, not a side effect of it.
/// </summary>
public sealed class HostUnreachableBackoffWiringTests
{
    private static readonly string Pool = Read("Ftp", "FtpConnectionPool.cs");
    private static readonly string Job = Read("Spread", "SpreadJob.cs");

    [Fact]
    public void Borrow_early_out_reads_the_unreachable_deadline()
    {
        var read = Pool.IndexOf("var unreachableUntil = Interlocked.Read(ref _unreachableUntilTicks);",
            StringComparison.Ordinal);
        Assert.True(read > 0,
            "Borrow must READ _unreachableUntilTicks; arming a deadline nothing consults parks no connections.");

        var parked = Pool.IndexOf("var parkedUntil =", read, StringComparison.Ordinal);
        Assert.True(parked > read && parked - read < 400, "parkedUntil must be computed after the read.");

        var expr = Pool.Substring(parked, Pool.IndexOf(';', parked) - parked);
        Assert.Contains("unreachableUntil", expr, StringComparison.Ordinal);
    }

    [Fact]
    public void The_failure_handler_arms_the_deadline_on_the_unreachable_verdict()
    {
        Assert.Contains("verdict == ConnectFailureClassifier.ConnectFailure.HostUnreachable", Pool, StringComparison.Ordinal);

        var arm = Pool.IndexOf("Interlocked.Exchange(ref _unreachableUntilTicks, DateTime.UtcNow.Add(UnreachableCooldown).Ticks)",
            StringComparison.Ordinal);
        Assert.True(arm > 0, "The HostUnreachable verdict must arm the backoff.");
    }

    [Fact]
    public void A_successful_connect_clears_the_deadline_exactly_once()
    {
        var clear = "Interlocked.Exchange(ref _unreachableUntilTicks, 0);";
        var first = Pool.IndexOf(clear, StringComparison.Ordinal);
        Assert.True(first > 0, "RecordConnect must clear the unreachable deadline, or the pool stays parked after recovery.");
        Assert.Equal(-1, Pool.IndexOf(clear, first + 1, StringComparison.Ordinal));

        var recordConnect = Pool.IndexOf("internal void RecordConnect(double ms)", StringComparison.Ordinal);
        Assert.True(first > recordConnect && first - recordConnect < 600,
            "The clear must live inside RecordConnect.");
    }

    [Fact]
    public void The_unreachable_log_line_carries_no_stack_and_is_rate_limited()
    {
        var branch = Pool.IndexOf("if (armedUnreachable)", StringComparison.Ordinal);
        Assert.True(branch > 0, "The unreachable log must be gated on the transition INTO the window.");

        var line = Pool.Substring(branch, Pool.IndexOf(");", branch) - branch);
        // Serilog's overload taking the exception first is what emits the stack. 14,219
        // byte-identical copies of it rolled the log three times in one day.
        Assert.DoesNotContain("(ex,", line, StringComparison.Ordinal);
        Assert.Contains("{Pool}", line, StringComparison.Ordinal);
    }

    [Fact]
    public void SnapshotPool_passes_the_live_property_not_a_literal()
    {
        var snap = Job.IndexOf("BorrowStarvationDiagnoser.PoolState SnapshotPool", StringComparison.Ordinal);
        Assert.True(snap > 0, "SnapshotPool not found.");
        var body = Job.Substring(snap, Job.IndexOf(';', snap) - snap);

        Assert.Contains("p.IsHostUnreachable", body, StringComparison.Ordinal);
        // The v3.10.78 lesson: asserting the NAME appears also passes `IsHostUnreachable: true`
        // and `IsHostUnreachable: false`. Reject both literals explicitly.
        Assert.DoesNotContain("IsHostUnreachable: true", body, StringComparison.Ordinal);
        Assert.DoesNotContain("IsHostUnreachable: false", body, StringComparison.Ordinal);
    }

    [Fact]
    public void IsThrottled_covers_the_unreachable_state()
    {
        var idx = Pool.IndexOf("public bool IsThrottled =>", StringComparison.Ordinal);
        Assert.True(idx > 0, "IsThrottled not found.");
        var expr = Pool.Substring(idx, Pool.IndexOf(';', idx) - idx);
        Assert.Contains("IsHostUnreachable", expr, StringComparison.Ordinal);
    }

    private static string Read(params string[] parts)
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (; dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(new[] { dir.FullName, "src", "GlDrive" }.Concat(parts).ToArray());
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        throw new FileNotFoundException("not found from " + Directory.GetCurrentDirectory());
    }
}
