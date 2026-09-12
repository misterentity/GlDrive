using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentFTP;
using GlDrive.Ftp;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// v3.10.116 — every poisoned discard names the site that spent the login.
///
/// The 2026-09-09..11 sweep ran at 0 ERR / 0 FTL. The only record of a spent login is the
/// pool's quarantine line, and it read "poisoned-discard" for all 28 poison sites alike.
/// Six superbnc discards on 2026-09-11 had no adjacent line naming a cause; the two that
/// followed a Relay dupe-skip were an ABOR failure logged at Debug on an Information sink;
/// one at 18:24:55 was whoever held the single main-pool connection for the 18 s before a
/// scan's borrow timed out. Attribution now travels on the connection itself
/// (<see cref="PooledConnection.Poison"/>) and is printed by the pool — the invariant
/// belongs to the resource, not to one caller's catch (recurring pattern #10).
///
/// The scan half: a deadline INSIDE a LIST (data TCP connect, data TLS) surfaces as the
/// same <see cref="OperationCanceledException"/> as a borrow timeout, and both the scan's
/// caller and <see cref="ScanFailureClassifier"/> read it as "never got a connection".
/// It spent one. <see cref="ScanListingFailedException"/> keeps the two apart.
/// </summary>
public class PoisonAttributionTests
{
    private sealed class InertClient() : AsyncFtpClient("example.invalid");

    // ---- the connection carries the reason ----

    [Fact]
    public void Poison_with_a_reason_marks_the_connection_and_keeps_the_reason()
    {
        using var client = new InertClient();
        var conn = new PooledConnection(client, null!);
        conn.Poison("scan LIST /x: data-channel deadline");
        Assert.True(conn.Poisoned);
        Assert.Equal("scan LIST /x: data-channel deadline", conn.PoisonReason);
    }

    [Fact]
    public void The_first_reason_wins_over_a_later_generic_attribution()
    {
        // FxpTransfer's route probe names the fault; SpreadJob's finally-chain attribution
        // runs afterwards with a generic "both poisoned". The specific one must survive.
        using var client = new InertClient();
        var conn = new PooledConnection(client, null!);
        conn.Poison("direct CPSV-PASV probe failed");
        conn.Poison("FXP fault side ambiguous — both poisoned");
        Assert.Equal("direct CPSV-PASV probe failed", conn.PoisonReason);
    }

    [Fact]
    public void Setting_the_flag_without_a_reason_is_recorded_as_unattributed()
    {
        using var client = new InertClient();
        var conn = new PooledConnection(client, null!);
        conn.Poisoned = true;
        Assert.True(conn.Poisoned);
        Assert.Equal(PooledConnection.UnattributedReason, conn.PoisonReason);
    }

    [Fact]
    public void Clearing_the_flag_clears_the_reason()
    {
        // The proactive sites (RETR-to-stream, media streaming) poison at borrow and clear
        // on success — a cleared connection must return to the pool with no reason.
        using var client = new InertClient();
        var conn = new PooledConnection(client, null!);
        conn.Poison("RETR-to-stream abandoned before completion");
        conn.Poisoned = false;
        Assert.False(conn.Poisoned);
        Assert.Null(conn.PoisonReason);
    }

    [Fact]
    public void A_blank_reason_is_not_an_attribution()
    {
        using var client = new InertClient();
        var conn = new PooledConnection(client, null!);
        conn.Poison("  ");
        Assert.Equal(PooledConnection.UnattributedReason, conn.PoisonReason);
    }

    [Theory]
    [InlineData("scan LIST /x: IOException", false, "scan LIST /x: IOException")]
    [InlineData("scan LIST /x: IOException", true, "scan LIST /x: IOException")]
    [InlineData(null, true, PooledConnection.PendingReplyReason)]
    [InlineData(null, false, PooledConnection.UnattributedReason)]
    public void The_discard_cause_prefers_the_poison_reason_then_the_pending_mark(
        string? reason, bool pending, string expected)
        => Assert.Equal(expected, PooledConnection.DescribeDiscard(reason, pending));

    // ---- the scan keeps a spent login apart from a borrow that never got one ----

    [Fact]
    public void A_listing_that_failed_after_borrow_is_a_fault_even_though_it_wraps_a_cancellation()
    {
        var ex = new ScanListingFailedException("/incoming/tv-hd/X", new TaskCanceledExceptionStandIn());
        Assert.False(ScanFailureClassifier.IsContention(ex));
        Assert.False(ScanFailureClassifier.IsContention(new InvalidOperationException("wrapped", ex)));
        Assert.Contains("/incoming/tv-hd/X", ex.Message);
        Assert.Equal("/incoming/tv-hd/X", ex.Path);
    }

    private sealed class TaskCanceledExceptionStandIn() : OperationCanceledException("data-channel deadline");

    // ---- structural: production code attributes, the pool prints, the scan wraps ----

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir, "src", "GlDrive", "Ftp", "FtpConnectionPool.cs"))) return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException("Could not locate the repository root from " + AppContext.BaseDirectory);
    }

    private static string ReadSource(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { RepoRoot(), "src", "GlDrive" }.Concat(parts).ToArray()));

    [Fact]
    public void No_production_site_poisons_without_a_reason()
    {
        // 28 sites on 2026-09-11. A bare `Poisoned = true` prints "unattributed" and puts
        // the sweep back where it started. Test code and the pool's own setter are exempt.
        var offenders = Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", "GlDrive"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"\.Poisoned\s*=\s*true\s*;"))
            .Select(f => Path.GetRelativePath(RepoRoot(), f))
            .ToList();
        Assert.True(offenders.Count == 0, "bare `Poisoned = true` in: " + string.Join(", ", offenders));
    }

    [Fact]
    public void The_pool_prints_the_cause_on_the_quarantine_line()
    {
        var pool = ReadSource("Ftp", "FtpConnectionPool.cs");

        // DisposeAsync hands the cause to Discard, and Discard folds it into the reason
        // that Quarantine logs. Asserting the local, not a literal (v3.10.78 trap).
        Assert.Contains("_pool.Discard(client, DescribeDiscard(_poisonReason, pending))", pool);
        Assert.DoesNotContain("_pool.Discard(client);", pool);

        var discard = pool.IndexOf("internal void Discard(AsyncFtpClient client, string? cause", StringComparison.Ordinal);
        Assert.True(discard >= 0, "Discard no longer takes a cause");
        var body = pool[discard..pool.IndexOf("\n    }", discard, StringComparison.Ordinal)];
        Assert.Contains("$\"poisoned-discard: {cause}\"", body);
        Assert.Contains("QuarantineDeferred(client,", body);
    }

    [Fact]
    public void A_cancellation_inside_the_list_is_wrapped_and_the_callers_own_token_is_not()
    {
        var job = ReadSource("Spread", "SpreadJob.cs");
        var start = job.IndexOf("private async Task ScanDirectoryRecursive", StringComparison.Ordinal);
        Assert.True(start >= 0, "ScanDirectoryRecursive not found — was it renamed?");
        var borrow = job.IndexOf("await using var conn = await pool.Borrow", start, StringComparison.Ordinal);
        var end = job.IndexOf("List<string>? subdirs", borrow, StringComparison.Ordinal);
        var listScope = job[borrow..end];

        // The caller's token propagates as-is (shutdown / race stop must stay a cancellation).
        Assert.Contains("catch (OperationCanceledException) when (ct.IsCancellationRequested)", listScope);
        // Any other cancellation was a deadline inside the LIST and spent the login.
        var inner = listScope.IndexOf("catch (OperationCanceledException ex)", StringComparison.Ordinal);
        Assert.True(inner >= 0, "the in-LIST cancellation branch is gone");
        Assert.Contains("throw new ScanListingFailedException(currentPath, ex)", listScope[inner..]);
        // The unclean IOException branch wraps too.
        var io = listScope.IndexOf("catch (IOException ex) when (!FtpCommandRejection.IsClean(ex))", StringComparison.Ordinal);
        Assert.True(io >= 0);
        Assert.Contains("throw new ScanListingFailedException(currentPath, ex)", listScope[io..]);
        // Both poison with a reason before throwing.
        Assert.Equal(3, Regex.Matches(listScope, @"conn\.Poison\(").Count);

        // And the scan's main-pool branch names it instead of calling it exhaustion.
        var scan = job.IndexOf("catch (ScanListingFailedException ex)", StringComparison.Ordinal);
        Assert.True(scan >= 0, "the main-pool scan no longer distinguishes a listing fault");
        Assert.Contains("failed after borrow", job[scan..job.IndexOf("catch (Exception ex)", scan, StringComparison.Ordinal)]);
    }

    [Fact]
    public void A_failed_abor_after_a_relay_dupe_skip_is_visible_on_the_information_sink()
    {
        var fxp = ReadSource("Spread", "FxpTransfer.cs");
        var abor = fxp.IndexOf("catch (Exception abortEx)", StringComparison.Ordinal);
        Assert.True(abor >= 0, "the ABOR-after-dupe catch is gone");
        var body = fxp[abor..fxp.IndexOf("CpsvDataHelper.EndDataSequence(dst);", abor, StringComparison.Ordinal)];
        Assert.Contains("Log.Information(", body);
        Assert.DoesNotContain("Log.Debug(", body);
        Assert.Contains("abortEx.GetType().Name", body);
    }
}
