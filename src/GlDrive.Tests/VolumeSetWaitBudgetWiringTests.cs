using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Structural cover for the 2026-08-20 defect. <see cref="GlDrive.Downloads.VolumeSetArrivalBudget"/>
/// is unit-tested on its own, but a correct policy that the caller does not consult is exactly
/// the failure being fixed here: <c>VolumeSetReadiness.IsStillArriving</c> already existed, its
/// summary already said callers use it to tell "not finished yet" from "genuine stall", and
/// <c>WaitForVolumeSetReady</c> incremented a counter with it and then fell out of the loop on
/// wall clock anyway.
///
/// The extractor lives in a WPF window that cannot be constructed under xUnit, so the wiring is
/// pinned in the source — the same approach, and the same comment-stripping precaution, as
/// <see cref="ExtractAbandonGateCoverageTests"/>. A future edit that reintroduces a duration
/// bound on the set-wide loop fails here.
/// </summary>
public sealed class VolumeSetWaitBudgetWiringTests
{
    /// <summary>
    /// The extractor source with every comment removed. Comments are stripped because a
    /// source-text assertion that can be satisfied by prose tests prose, not behaviour.
    /// </summary>
    private static string StrippedSource()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (; dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "GlDrive", "UI", "ExtractorWindow.xaml.cs");
            if (!File.Exists(candidate)) continue;

            var code = File.ReadAllText(candidate);
            code = Regex.Replace(code, @"/\*[\s\S]*?\*/", "");
            code = Regex.Replace(code, @"//[^\n]*", "");
            code = Regex.Replace(code, @"^\s*///[^\n]*$", "", RegexOptions.Multiline);
            return code;
        }

        throw new FileNotFoundException(
            "ExtractorWindow.xaml.cs not found walking up from " + Directory.GetCurrentDirectory());
    }

    private static string WaitBody() => MethodBody(StrippedSource(), "WaitForVolumeSetReady");

    /// <summary>
    /// The name of the readiness gate's budget parameter, read from its signature so a rename
    /// cannot quietly turn the guards below into no-ops.
    /// </summary>
    private static string BudgetParameterName()
    {
        var signature = Regex.Match(StrippedSource(),
            @"\bWaitForVolumeSetReady\s*\(\s*string\s+\w+\s*,\s*CancellationToken\s+\w+\s*,\s*int\s+(?<name>\w+)");
        Assert.True(signature.Success, "Could not read the budget parameter from WaitForVolumeSetReady.");
        return signature.Groups["name"].Value;
    }

    private static string MethodBody(string code, string name)
    {
        var declaration = Regex.Match(code,
            $@"(?:private|internal|public|protected)[^\n(]*\b{Regex.Escape(name)}\s*\([^)]*\)\s*\n?\s*\{{");
        Assert.True(declaration.Success, $"Could not locate the declaration of {name}.");

        var start = declaration.Index + declaration.Length - 1;
        var depth = 0;
        for (var i = start; i < code.Length; i++)
        {
            if (code[i] == '{') depth++;
            else if (code[i] == '}' && --depth == 0) return code.Substring(start, i - start + 1);
        }

        throw new Xunit.Sdk.XunitException($"Unbalanced braces walking the body of {name}.");
    }

    [Fact]
    public void TheWaitConsultsTheArrivalBudget()
    {
        Assert.Contains("VolumeSetArrivalBudget.Evaluate", WaitBody());
    }

    /// <summary>
    /// The claim that caught the 2026-08-20 bug, restated for v3.10.76.
    ///
    /// It used to read "<c>maxWaitMs</c> may bound the FIRST-volume wait and nothing else",
    /// which was true only while that first wait was still a duration bound — and that bound
    /// was itself the 2026-08-21 defect. The budget parameter is now an INACTIVITY budget for
    /// both phases, so the guard is restated rather than retired: it may reach
    /// <c>WaitForFileReady</c> and <c>VolumeSetArrivalBudget.Evaluate</c>, and nothing else.
    ///
    /// Stated as "no use outside those two" rather than as a count, so it cannot be made
    /// vacuous again by renaming the parameter: the name is read from the signature.
    /// </summary>
    [Fact]
    public void NeitherPhaseIsBoundedByDuration()
    {
        var body = WaitBody();
        var budget = BudgetParameterName();

        var uses = Regex.Matches(body, $@"\b{Regex.Escape(budget)}\b").Count;
        var handOff = Regex.Matches(body, $@"WaitForFileReady\s*\([^)]*\b{Regex.Escape(budget)}\b[^)]*\)").Count;
        var toBudget = Regex.Matches(body,
            $@"VolumeSetArrivalBudget\.Evaluate\s*\([^;]*\b{Regex.Escape(budget)}\b[^;]*\)").Count;

        Assert.Equal(1, handOff);
        Assert.True(toBudget >= 1, "The set-wide loop must hand the inactivity budget to Evaluate.");

        // Every remaining mention must be one of those two, plus the log line that prints it.
        var logUses = Regex.Matches(body, $@"\b{Regex.Escape(budget)}\s*/\s*1000").Count;
        Assert.Equal(handOff + toBudget + logUses, uses);

        // The wall-clock loop condition that was the defect must not come back in either phase.
        Assert.DoesNotContain("sw.ElapsedMilliseconds <", body);
    }

    /// <summary>
    /// The first-volume wait must be governed by the same activity rule as the set-wide loop.
    /// This is the v3.10.76 fix: v3.10.73 converted the second phase and left this one on wall
    /// clock, so the false timeout simply moved one gate upstream (first-volume timeouts 0 → 2
    /// across 2026-08-21, exactly paired with the caller's "not ready before timeout").
    /// </summary>
    [Fact]
    public void TheFirstVolumeWaitIsBoundedOnActivityToo()
    {
        var body = MethodBody(StrippedSource(), "WaitForFileReady");

        Assert.Contains("VolumeSetArrivalBudget.Evaluate", body);
        Assert.Contains("IsStillArriving", body);
        Assert.DoesNotContain("sw.ElapsedMilliseconds <", body);
    }

    /// <summary>
    /// The caller must branch on <c>ArchiveWait.DeservesRetry</c>. It is the predicate that says
    /// the twelve-hour ceiling must not consume one of the five bounded watch retries, and until
    /// v3.10.76 its production caller count was ZERO while a unit test asserted it — which is
    /// what made the gap look covered.
    /// </summary>
    [Fact]
    public void TheCallerBranchesOnDeservesRetry()
    {
        var body = MethodBody(StrippedSource(), "HandleWatchedFileAsync");

        Assert.Contains("DeservesRetry", body);

        // The retry must sit inside that branch, not beside it.
        var branch = Regex.Match(body, @"if\s*\(\s*ArchiveWait\.DeservesRetry\s*\([^)]*\)\s*\)\s*\{(?<block>[\s\S]*?)\n            \}");
        Assert.True(branch.Success, "Could not locate the DeservesRetry branch.");
        Assert.Contains("ScheduleWatchRetry", branch.Groups["block"].Value);
    }

    /// <summary>
    /// The budget is an INACTIVITY budget, which only means anything if observed progress
    /// actually resets the clock it reads.
    /// </summary>
    [Fact]
    public void ObservedProgressResetsTheClockTheBudgetReads()
    {
        var body = WaitBody();

        var arrivalBranch = Regex.Match(body,
            @"IsStillArriving\s*\([^)]*\)\s*\)\s*\{(?<block>[\s\S]*?)\n            \}");
        Assert.True(arrivalBranch.Success, "Could not locate the still-arriving branch.");

        var marker = Regex.Match(arrivalBranch.Groups["block"].Value, @"(?<name>\w+)\s*=\s*sw\.ElapsedMilliseconds");
        Assert.True(marker.Success, "The still-arriving branch does not stamp a progress marker.");

        Assert.Matches(
            new Regex($@"Evaluate\s*\(\s*sw\.ElapsedMilliseconds\s*-\s*{Regex.Escape(marker.Groups["name"].Value)}\b"),
            body);
    }
}
