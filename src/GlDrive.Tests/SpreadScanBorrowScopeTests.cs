using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Regression guard for v3.10.43 — the spread scanner's self-inflicted pool deadlock.
///
/// ScanDirectoryRecursive used to hold its borrowed connection across the recursive
/// call, so a depth-N walk pinned N+1 connections simultaneously. The account login
/// gate (LoginCap − LoginHeadroom) leaves the main pool only ~2 usable logins, so any
/// release with a subdirectory deadlocked the scan against itself: the parent could
/// not release until the child borrowed, and the child could not borrow because the
/// parents held every slot. Every such scan burned the full 20s borrow timeout and
/// then re-ran on the FXP spread pool, stealing transfer slots (2176 fallbacks and
/// 73 total scan failures in a single day, with zero ERR lines to show for it).
///
/// The fix scopes the borrow to the LIST alone and walks subdirectories afterwards,
/// capping concurrent connections per scan at exactly 1 for any depth.
/// </summary>
public class SpreadScanBorrowScopeTests
{
    private static string ReadSpreadJobSource()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12 && dir != null; i++)
        {
            var candidate = Path.Combine(dir, "src", "GlDrive", "Spread", "SpreadJob.cs");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new InvalidOperationException("Could not locate src/GlDrive/Spread/SpreadJob.cs from " + AppContext.BaseDirectory);
    }

    private static string ExtractScanDirectoryRecursive(string source)
    {
        var start = source.IndexOf("private async Task ScanDirectoryRecursive", StringComparison.Ordinal);
        Assert.True(start >= 0, "ScanDirectoryRecursive not found — was it renamed?");

        // Walk braces from the method's opening '{' to its matching close.
        var open = source.IndexOf('{', start);
        Assert.True(open > 0, "Could not find method body opening brace.");

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0) return source[open..(i + 1)];
            }
        }

        throw new InvalidOperationException("Unbalanced braces while extracting ScanDirectoryRecursive.");
    }

    [Fact]
    public void Recursive_call_is_not_inside_the_borrowed_connection_scope()
    {
        var body = ExtractScanDirectoryRecursive(ReadSpreadJobSource());

        var borrowIndex = body.IndexOf("await using var conn = await pool.Borrow", StringComparison.Ordinal);
        Assert.True(borrowIndex >= 0, "Expected a scoped 'await using var conn = await pool.Borrow(...)'.");

        // Find the block that the borrow lives in, then confirm it closes before the
        // recursive call. If the borrow is declared at method scope (the old bug), its
        // enclosing block only ends at the end of the method — i.e. after the recursion.
        var depth = 0;
        var borrowScopeEnd = -1;
        for (var i = borrowIndex; i < body.Length; i++)
        {
            if (body[i] == '{') depth++;
            else if (body[i] == '}')
            {
                if (depth == 0) { borrowScopeEnd = i; break; }
                depth--;
            }
        }

        Assert.True(borrowScopeEnd > 0, "Could not determine the borrow's enclosing scope.");

        var recursionIndex = body.IndexOf("await ScanDirectoryRecursive(", StringComparison.Ordinal);
        Assert.True(recursionIndex >= 0, "Expected a recursive ScanDirectoryRecursive call.");

        Assert.True(recursionIndex > borrowScopeEnd,
            "ScanDirectoryRecursive recurses while still holding a pooled connection. " +
            "That is hold-and-wait: a depth-N walk pins N+1 connections, which exceeds the " +
            "main pool's usable logins and deadlocks the scan against itself. Release the " +
            "connection after the LIST and walk subdirectories afterwards.");
    }

    [Fact]
    public void Subdirectories_are_collected_during_the_listing_and_walked_after_release()
    {
        var body = ExtractScanDirectoryRecursive(ReadSpreadJobSource());

        Assert.Contains("subdirs", body);
        Assert.Matches(new Regex(@"foreach\s*\(\s*var\s+subdir\s+in\s+subdirs\s*\)"), body);
    }

    /// <summary>
    /// Encodes the arithmetic that made the old shape fail deterministically rather
    /// than intermittently: a hold-across-recursion walk needs depth+1 permits, and
    /// the gate grants fewer than that, so it can never converge.
    /// </summary>
    [Theory]
    [InlineData(0, 2, true)]   // flat release (empty dest) — 1 permit needed, always fine
    [InlineData(1, 2, true)]   // one subdir — needs 2, exactly fits
    [InlineData(2, 2, false)]  // Sample/ + nested — needs 3, gate grants 2 => deadlock
    [InlineData(3, 2, false)]  // max production depth — needs 4 => deadlock
    public void Hold_across_recursion_needs_depth_plus_one_permits(int depth, int usableLogins, bool couldSucceed)
    {
        var permitsNeededByOldShape = depth + 1;
        Assert.Equal(couldSucceed, permitsNeededByOldShape <= usableLogins);

        // The fixed shape releases before recursing, so peak concurrency is 1 at any depth.
        Assert.True(1 <= usableLogins);
    }

    /// <summary>
    /// Behavioural proof against a bounded pool: the release-before-recurse shape
    /// completes a deep walk on a 2-permit pool, and never exceeds one permit.
    /// </summary>
    [Fact]
    public async Task Release_before_recurse_completes_deep_walk_on_a_two_permit_pool()
    {
        using var pool = new SemaphoreSlim(2, 2);
        var live = 0;
        var peak = 0;
        var visited = 0;

        async Task Walk(int depth)
        {
            await pool.WaitAsync(TimeSpan.FromSeconds(2));
            int observed;
            try
            {
                observed = Interlocked.Increment(ref live);
                peak = Math.Max(peak, observed);
                visited++;
                await Task.Yield(); // stand in for the LIST round-trip
            }
            finally
            {
                Interlocked.Decrement(ref live);
                pool.Release();
            }

            if (depth >= 3) return;
            foreach (var _ in Enumerable.Range(0, 2))
                await Walk(depth + 1); // recursion happens AFTER release
        }

        await Walk(0);

        Assert.Equal(15, visited);   // full binary walk to depth 3
        Assert.Equal(1, peak);       // never holds more than one connection
    }
}
