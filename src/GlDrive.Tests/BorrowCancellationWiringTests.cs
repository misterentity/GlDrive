using System;
using System.IO;
using System.Linq;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// v3.10.59 — pins the WIRING, not just the verdict.
///
/// <see cref="ConnectFailureClassifierTests"/> proves the classifier calls a caller
/// deadline what it is. That is worth nothing if <c>FtpConnectionPool.Borrow</c> does
/// not consult it, and Borrow cannot be exercised in a unit test: it needs a real
/// <c>FtpClientFactory</c> and a live FTPS endpoint. So these tests read the method's
/// source, the way <see cref="SpreadScanBorrowScopeTests"/> does for the scan deadlock.
///
/// The three consequences that must never be applied to a caller abandonment — and
/// which the 2026-08-13 log shows being applied 2,545 times in a day — are: arming a
/// cooldown, spending the once-per-episode ghost kill, and reporting the borrow as
/// pool exhaustion.
/// </summary>
public class BorrowCancellationWiringTests
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

        throw new InvalidOperationException(
            "Could not locate src/GlDrive/Ftp/FtpConnectionPool.cs from " + AppContext.BaseDirectory);
    }

    private static string ExtractBorrow(string source)
    {
        var start = source.IndexOf("public async Task<PooledConnection> Borrow", StringComparison.Ordinal);
        Assert.True(start >= 0, "Borrow not found — was it renamed?");

        var open = source.IndexOf('{', start);
        Assert.True(open > 0, "Could not find Borrow's opening brace.");

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

        throw new InvalidOperationException("Unbalanced braces walking Borrow.");
    }

    [Fact]
    public void Borrow_ClassifiesConnectFailuresInsteadOfActingOnAllOfThem()
    {
        var body = ExtractBorrow(ReadPoolSource());

        Assert.Contains("ConnectFailureClassifier.Classify", body);
        Assert.Contains("ConnectFailureClassifier.IsRealFinding", body);
    }

    [Fact]
    public void Borrow_PassesTheCallersOwnTokenStateToTheClassifier()
    {
        // Classifying against anything other than the caller's token would recreate
        // the defect: the pool would once again be guessing at whose deadline expired.
        var body = ExtractBorrow(ReadPoolSource());

        Assert.Contains("callerCancelled: ct.IsCancellationRequested", body);
    }

    [Fact]
    public void ExhaustionIsNotReportedAfterTheCallerGaveUp()
    {
        // 669 of 703 "Pool exhausted" throws on 2026-08-13 were this. The cancellation
        // check must come BEFORE the exhaustion throw, or the ordering means nothing.
        var body = ExtractBorrow(ReadPoolSource());

        var guard = body.IndexOf("ct.ThrowIfCancellationRequested()", StringComparison.Ordinal);
        var exhausted = body.IndexOf("Pool exhausted: all connections discarded", StringComparison.Ordinal);

        Assert.True(guard >= 0, "Borrow no longer checks the caller's token before reporting exhaustion.");
        Assert.True(exhausted >= 0, "The exhaustion throw disappeared — did this move?");
        Assert.True(guard < exhausted, "The cancellation guard must precede the exhaustion throw.");
    }

    [Fact]
    public void EveryGhostKillCatchLetsACallerCancellationThrough()
    {
        // The ghost-kill blocks wrap their work in catch-all Debug logging. A bare
        // catch-all there swallows the rethrow from the connect retry and drops the
        // borrow straight into the exhaustion report — the defect by another route.
        var body = ExtractBorrow(ReadPoolSource());

        var filtered = System.Text.RegularExpressions.Regex.Matches(
            body, @"catch \(OperationCanceledException\) when \(ct\.IsCancellationRequested\)").Count;
        var ghostKillCatches = System.Text.RegularExpressions.Regex.Matches(
            body, @"ghost kill.*failed").Count;

        Assert.True(
            filtered >= ghostKillCatches,
            $"Found {ghostKillCatches} ghost-kill catch-alls but only {filtered} cancellation filters ahead of them.");
    }

    [Fact]
    public void TheAbandonmentPathBacksOutItsOwnCreatedAccounting()
    {
        // The early rethrow skips the shared Decrement further down the handler, so it
        // has to do its own. A leaked _created permanently understates pool capacity.
        var body = ExtractBorrow(ReadPoolSource());

        var start = body.IndexOf("IsRealFinding", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var window = body[start..Math.Min(body.Length, start + 700)];

        Assert.Contains("Interlocked.Decrement(ref _created)", window);
        Assert.Contains("throw;", window);
    }

    [Fact]
    public void AbandonmentIsNotLoggedWithAStackAtInformation()
    {
        // 2,545 stacks a day, ~25k lines, evicting real history from the rolling log.
        var body = ExtractBorrow(ReadPoolSource());

        var start = body.IndexOf("IsRealFinding", StringComparison.Ordinal);
        var window = body[start..Math.Min(body.Length, start + 700)];

        Assert.DoesNotContain("Log.Information(ex", window);
        Assert.DoesNotContain("Log.Warning(ex", window);
    }
}
