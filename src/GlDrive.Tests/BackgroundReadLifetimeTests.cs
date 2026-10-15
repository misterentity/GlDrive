using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// v3.11.9 — a race's background scan / SFV read must not outlive the race.
///
/// 2026-10-14 11:51:00 "Spread race complete: Its.Always.Sunny...S18E05". At 11:51:18 that
/// release's scan was still borrowing superbnc logins ("main pool exhausted ... falling back
/// to spread pool"), and at 11:51:29 it poisoned one ("scan LIST /incoming/tv-hd/...S18E05:
/// DataChannelTimeoutException") while S18E06 — started 11:51:04 — dispatched on a
/// 1-usable-login spread pool. 10:58:47: Reasonable.Doubt S04E03's scan still waiting on a
/// borrow 55 s after its race ended. RunAsync's finally drained FXP transfers but never the
/// scan, and the scan's token came from a CTS that is disposed — not cancelled — on exit.
/// </summary>
public class BackgroundReadLifetimeTests
{
    [Fact]
    public async Task Stopping_cancels_the_borrow_token()
    {
        var reads = new BackgroundReadLifetime(CancellationToken.None);
        Assert.False(reads.IsStopped);
        var token = reads.StopToken;

        Assert.True(await reads.StopAndDrainAsync(TimeSpan.FromSeconds(1)));

        Assert.True(reads.IsStopped);
        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public void The_job_token_also_stops_background_borrows()
    {
        using var job = new CancellationTokenSource();
        var reads = new BackgroundReadLifetime(job.Token);
        job.Cancel();
        Assert.True(reads.IsStopped);
    }

    [Fact]
    public async Task Drain_waits_for_a_read_already_on_the_wire()
    {
        // A LIST in flight is allowed to finish (tearing it mid-stream poisons the login),
        // and cleanup must not run until it has.
        var reads = new BackgroundReadLifetime(CancellationToken.None);
        var release = new TaskCompletionSource();
        var listing = Task.Run(async () => await release.Task);

        var drain = reads.StopAndDrainAsync(TimeSpan.FromSeconds(10), listing);
        await Task.Delay(100);
        Assert.False(drain.IsCompleted);

        release.SetResult();
        Assert.True(await drain);
    }

    [Fact]
    public async Task A_borrow_waiting_on_the_stop_token_unblocks_immediately()
    {
        var reads = new BackgroundReadLifetime(CancellationToken.None);
        var borrowWait = Task.Delay(Timeout.Infinite, reads.StopToken);

        var started = DateTime.UtcNow;
        Assert.True(await reads.StopAndDrainAsync(TimeSpan.FromSeconds(10), borrowWait));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
        Assert.True(borrowWait.IsCanceled);
    }

    [Fact]
    public async Task Drain_is_bounded_and_reports_a_read_that_will_not_finish()
    {
        var reads = new BackgroundReadLifetime(CancellationToken.None);
        var stuck = new TaskCompletionSource().Task;
        Assert.False(await reads.StopAndDrainAsync(TimeSpan.FromMilliseconds(100), stuck));
    }

    [Fact]
    public async Task A_faulted_read_is_observed_not_rethrown()
    {
        var reads = new BackgroundReadLifetime(CancellationToken.None);
        var failed = Task.FromException(new IOException("data connection not completed within 10s"));
        Assert.True(await reads.StopAndDrainAsync(TimeSpan.FromSeconds(1), failed, null));
    }

    [Fact]
    public async Task A_second_drain_is_a_no_op_not_an_ObjectDisposedException()
    {
        // It runs in a finally: throwing there would skip the race's cleanup and history.
        var reads = new BackgroundReadLifetime(CancellationToken.None);
        Assert.True(await reads.StopAndDrainAsync(TimeSpan.FromSeconds(1)));
        Assert.True(await reads.StopAndDrainAsync(TimeSpan.FromSeconds(1)));
    }

    // ---- structural: the race actually uses it ----

    private static string Job() => File.ReadAllText(Path.Combine(RepoRoot(), "src", "GlDrive", "Spread", "SpreadJob.cs"));

    private static string Slice(string src, string from, string to)
    {
        var start = src.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{from}' not found — renamed?");
        var end = src.IndexOf(to, start + from.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"'{to}' not found after '{from}'");
        return src[start..end];
    }

    [Fact]
    public void RunAsync_creates_the_lifetime_from_the_race_token()
    {
        var run = Slice(Job(), "public async Task RunAsync()", "private async Task ScanSites(");
        Assert.Contains("_backgroundReads = new BackgroundReadLifetime(token);", run);
    }

    [Fact]
    public void The_finally_drains_the_scan_and_sfv_read_before_cleanup()
    {
        var run = Slice(Job(), "public async Task RunAsync()", "private async Task ScanSites(");
        var settle = run.IndexOf("Let in-flight fire-and-forget transfers settle", StringComparison.Ordinal);
        Assert.True(settle >= 0, "RunAsync's transfer drain moved — re-anchor this test");
        var fin = run[run.LastIndexOf("finally", settle, StringComparison.Ordinal)..];
        var drain = fin.IndexOf("backgroundReads.StopAndDrainAsync(", StringComparison.Ordinal);
        Assert.True(drain >= 0, "RunAsync's finally no longer drains background reads");
        Assert.Contains("_backgroundScan, _sfvParseTask)", fin[drain..(drain + 120)]);
        // ...before the dir cleanup and history consume ownership.
        Assert.True(drain < fin.IndexOf("ReconcileProgressFromOwnership();", StringComparison.Ordinal));
        Assert.Contains("Interlocked.Exchange(ref _backgroundReads, null)", fin);
    }

    [Fact]
    public void The_scan_borrow_is_gated_but_the_list_is_not()
    {
        var scope = Slice(Job(), "private async Task ScanDirectoryRecursive", "List<string>? subdirs");
        Assert.Contains("CreateLinkedTokenSource(ct, readStop)", scope);
        Assert.DoesNotContain("CreateLinkedTokenSource(ct);", scope);
        // The LIST itself keeps the race token: a read on the wire is never torn.
        Assert.Contains("CpsvDataHelper.ListDirectory(conn.Client, currentPath, pool.ControlHost, ct)", scope);
        Assert.Contains("FtpListing.GetListing(conn.Client, currentPath, ct)", scope);
        Assert.DoesNotContain("currentPath, pool.ControlHost, readStop", scope);
    }

    [Fact]
    public void Every_recursive_scan_call_passes_the_stop_token()
    {
        var job = Job();
        var calls = System.Text.RegularExpressions.Regex.Matches(job, @"await ScanDirectoryRecursive\(([^;]*)\);");
        Assert.Equal(3, calls.Count);
        Assert.All(calls, m => Assert.EndsWith(", ct, readStop", m.Groups[1].Value));
    }

    [Fact]
    public void A_stopped_scan_neither_falls_back_nor_feeds_results_to_an_ended_race()
    {
        var scan = Slice(Job(), "private async Task ScanSites(", "private async Task ScanDirectoryRecursive");
        Assert.Contains("var readStop = _backgroundReads?.StopToken ?? CancellationToken.None;", scan);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(scan,
            @"catch \(OperationCanceledException\) when \(readStop\.IsCancellationRequested\)\s*\{[^}]*return;").Count);
        var whenAll = scan.IndexOf("await Task.WhenAll(tasks);", StringComparison.Ordinal);
        var guard = scan.IndexOf("if (readStop.IsCancellationRequested) return;", whenAll, StringComparison.Ordinal);
        Assert.True(guard > whenAll && guard < scan.IndexOf("ProcessFiles(", whenAll, StringComparison.Ordinal),
            "scan results can land in a race that has already ended");
    }

    [Fact]
    public void The_sfv_read_borrow_is_gated_too()
    {
        var sfv = Slice(Job(), "private async Task ParseSfvForCount", "internal bool ApplySfvInventory");
        Assert.Contains("CreateLinkedTokenSource(token, readStop)", sfv);
        Assert.Contains("pool.Borrow(borrowCts.Token)", sfv);
        Assert.DoesNotContain("pool.Borrow(token)", sfv);
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir, "src", "GlDrive", "Spread", "SpreadJob.cs"))) return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException("Could not locate the repository root from " + AppContext.BaseDirectory);
    }
}
