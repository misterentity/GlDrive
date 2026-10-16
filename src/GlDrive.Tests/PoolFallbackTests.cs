using System.IO;
using System.Text.RegularExpressions;
using GlDrive.Ftp;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// 2026-10-15: superbnc's main pool holds ONE connection (login cap 4, headroom 2, FXP
/// reserved 1), and spread scans keep it busy ~145 times an hour. SITE INVITE queued behind
/// them for its whole 30s budget and timed out 7 times in one day; each miss left the
/// announce channels (#supers) unjoined until the 5–15 min standing retry. 13:27–14:32 the
/// site's announces were not received at all.
///
/// The invite is one control command. It now waits briefly for the main pool and then takes
/// the spread pool, the same fallback the spread scan has used since v3.10.x.
/// </summary>
public sealed class PoolFallbackTests
{
    private static readonly TimeSpan ShortBudget = TimeSpan.FromMilliseconds(100);

    private static async Task<string> NeverLends(CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        return "unreachable";
    }

    [Fact]
    public async Task Primary_that_lends_is_used_and_the_fallback_is_never_touched()
    {
        var fallbackCalled = false;

        var result = await PoolFallback.BorrowAsync(
            _ => Task.FromResult("main"),
            _ => { fallbackCalled = true; return Task.FromResult("spread"); },
            ShortBudget, CancellationToken.None);

        Assert.Equal("main", result);
        Assert.False(fallbackCalled);
    }

    [Fact]
    public async Task Primary_that_cannot_lend_within_its_budget_falls_back()
    {
        // A caller deadline far above the budget: a budget that never fires fails here, not hangs.
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await PoolFallback.BorrowAsync(
            NeverLends, _ => Task.FromResult("spread"), ShortBudget, caller.Token);

        Assert.Equal("spread", result);
    }

    [Fact]
    public async Task Primary_that_refuses_falls_back()
    {
        var result = await PoolFallback.BorrowAsync<string>(
            _ => throw new InvalidOperationException("Account login-gate backoff (no permit)"),
            _ => Task.FromResult("spread"), ShortBudget, CancellationToken.None);

        Assert.Equal("spread", result);
    }

    [Fact]
    public async Task Fallback_receives_the_callers_token_not_the_expired_budget()
    {
        CancellationToken seen = default;
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await PoolFallback.BorrowAsync(
            NeverLends, ct => { seen = ct; return Task.FromResult("spread"); },
            ShortBudget, caller.Token);

        Assert.False(seen.IsCancellationRequested);
    }

    [Fact]
    public async Task Callers_own_cancellation_is_not_a_fallback()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var fallbackCalled = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PoolFallback.BorrowAsync(
            NeverLends, _ => { fallbackCalled = true; return Task.FromResult("spread"); },
            TimeSpan.FromSeconds(10), cts.Token));

        Assert.False(fallbackCalled);
    }

    [Fact]
    public async Task Without_a_fallback_the_primary_gets_the_whole_caller_budget()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        var sw = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PoolFallback.BorrowAsync(NeverLends, null, ShortBudget, cts.Token));

        Assert.True(sw.ElapsedMilliseconds >= 350,
            $"Primary was cut off at {sw.ElapsedMilliseconds}ms; with no fallback it must wait for the caller.");
    }

    /// <summary>The wiring, not just the helper: SITE INVITE must actually borrow through it.</summary>
    [Fact]
    public void SiteInvite_borrows_through_the_fallback()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "GlDrive", "Services", "ServerManager.cs")))
            dir = dir.Parent;
        Assert.NotNull(dir);

        var code = Regex.Replace(
            File.ReadAllText(Path.Combine(dir!.FullName, "src", "GlDrive", "Services", "ServerManager.cs")),
            @"//[^\n]*", "");
        var start = code.IndexOf("SiteInviteFunc =", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var body = code.Substring(start, code.IndexOf("};", start, StringComparison.Ordinal) - start);

        Assert.Contains("PoolFallback.BorrowAsync", body);
        Assert.Matches(@"var\s+spreadPool\s*=\s*_spreadManager\?\.GetLivePool\(", body);
        // The fallback must be the computed pool, not a constant that disables it.
        Assert.Matches(@"spreadPool\s*==\s*null\s*\?\s*null\s*:\s*t\s*=>", body);
        Assert.DoesNotMatch(@"await\s+using\s+var\s+conn\s*=\s*await\s+mountService\.Pool\.Borrow", body);
    }
}
