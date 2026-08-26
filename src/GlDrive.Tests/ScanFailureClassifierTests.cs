using System;
using System.IO;
using System.Threading.Tasks;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Regression guard for v3.10.54(b) — "Spread scan FAILED (both pools unavailable)"
/// logged at WRN **with a full stack trace** for what is almost always plain login
/// contention.
///
/// 2026-08-10 breakdown of the 527 occurrences:
///     264  OperationCanceledException  (borrow timeout)
///     258  TaskCanceledException       (borrow timeout)
///       2  InvalidOperationException: Server in BNC cooldown
///       2  InvalidOperationException: Pool exhausted
///       1  ObjectDisposedException
/// 522 of 527 (99.1%) never attempted any I/O — a permit or a cooldown said no. That is
/// the SAME condition the branch directly above deliberately logs at INF ("main pool
/// exhausted ... falling back"), and the same condition a deliberate yield logs at INF.
/// Only the third spelling of it screamed.
///
/// Cost: ~4,700 lines = 8% of a quiet day's log, and 1,874/day on 2026-08-09 — which
/// rolled gldrive-20260809.log at 13:52 and split the day in two. That is the
/// v3.10.47 failure mode restated: **log volume destroys the evidence you need to
/// diagnose the next bug**, and it is why this sweep had only ~1.5 days of history
/// to trend. Severity must track breakage, not author surprise (recurring pattern #5).
///
/// The classifier keys on the DEFINING property — "we never got a connection, so no
/// operation was attempted" — rather than enumerating exception types observed once
/// (recurring pattern #4).
/// </summary>
public class ScanFailureClassifierTests
{
    [Fact]
    public void A_borrow_timeout_is_contention_not_a_fault()
    {
        Assert.True(ScanFailureClassifier.IsContention(new OperationCanceledException()));
        Assert.True(ScanFailureClassifier.IsContention(new TaskCanceledException()));
    }

    [Fact]
    public void A_bnc_cooldown_is_contention()
    {
        // The pool already logged the cooldown once when it entered it. Re-reporting it
        // every ~10s per release, with a stack, adds nothing.
        Assert.True(ScanFailureClassifier.IsContention(
            new InvalidOperationException("Server in BNC cooldown — not attempting new connection")));
        Assert.True(ScanFailureClassifier.IsContention(
            new InvalidOperationException("Server in BNC cooldown — pooled connection was dead")));
    }

    [Fact]
    public void A_login_cap_refusal_is_contention()
    {
        Assert.True(ScanFailureClassifier.IsContention(
            new InvalidOperationException("Account login cap reached — no login permit available")));
    }

    [Fact]
    public void A_genuine_fault_is_still_a_warning()
    {
        // These describe something broken, not something busy — they must keep their
        // stack trace. Losing them is how a real regression goes unnoticed.
        Assert.False(ScanFailureClassifier.IsContention(new ObjectDisposedException("pool")));
        Assert.False(ScanFailureClassifier.IsContention(new IOException("connection reset")));
        Assert.False(ScanFailureClassifier.IsContention(new InvalidOperationException("something else")));
        Assert.False(ScanFailureClassifier.IsContention(new Exception("boom")));
    }

    [Fact]
    public void A_pool_exhausted_error_is_a_real_fault()
    {
        // "all connections discarded" means every connection was poisoned — that is a
        // genuine defect signal (it drove the v3.10.4 poison-discard work) and must not
        // be demoted alongside ordinary contention.
        Assert.False(ScanFailureClassifier.IsContention(
            new InvalidOperationException("Pool exhausted: all connections discarded and new connections fail")));
    }

    [Fact]
    public void A_missing_exception_is_treated_as_a_fault()
    {
        // No evidence is not evidence of contention (recurring pattern #1).
        Assert.False(ScanFailureClassifier.IsContention(null));
    }

    [Fact]
    public void An_inner_borrow_timeout_is_still_contention()
    {
        Assert.True(ScanFailureClassifier.IsContention(
            new InvalidOperationException("wrapped", new TaskCanceledException())));
    }

    [Fact]
    public void An_empty_batch_of_only_contention_deferrals_is_not_a_warning()
        => Assert.False(ScanFailureClassifier.ShouldWarnEmptyBatch(successfulScans: 0, hardFailures: 0));

    [Fact]
    public void An_empty_batch_with_a_genuine_failure_is_a_warning()
        => Assert.True(ScanFailureClassifier.ShouldWarnEmptyBatch(successfulScans: 0, hardFailures: 1));

    [Fact]
    public void Any_success_suppresses_the_empty_batch_warning()
        => Assert.False(ScanFailureClassifier.ShouldWarnEmptyBatch(successfulScans: 1, hardFailures: 3));

    // ---- the call site uses it ----

    private static string ReadSpreadJobSource()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12 && dir != null; i++)
        {
            var candidate = Path.Combine(dir, "src", "GlDrive", "Spread", "SpreadJob.cs");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException("Could not locate src/GlDrive/Spread/SpreadJob.cs");
    }

    [Fact]
    public void The_scan_failure_log_is_severity_split_by_the_classifier()
    {
        var source = ReadSpreadJobSource();
        Assert.Contains("ScanFailureClassifier.IsContention", source);
        Assert.Contains("ScanFailureClassifier.ShouldWarnEmptyBatch", source);

        // The contention branch must not carry the exception object — passing it is what
        // emits the stack trace that costs 8 lines per occurrence.
        var warn = source.IndexOf("Spread scan FAILED for {Server}", StringComparison.Ordinal);
        Assert.True(warn >= 0, "the both-pools-unavailable warning was renamed or removed");
        var window = source[Math.Max(0, warn - 700)..warn];
        Assert.Contains("IsContention", window);
    }
}
