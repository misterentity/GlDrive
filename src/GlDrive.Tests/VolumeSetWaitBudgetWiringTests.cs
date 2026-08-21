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
    private static string WaitBody()
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
            return MethodBody(code, "WaitForVolumeSetReady");
        }

        throw new FileNotFoundException(
            "ExtractorWindow.xaml.cs not found walking up from " + Directory.GetCurrentDirectory());
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
    /// The one claim that actually caught the bug: <c>maxWaitMs</c> may bound the FIRST-volume
    /// wait and nothing else. Its only permitted use in this body is the argument handed to
    /// <c>WaitForFileReady</c>.
    /// </summary>
    [Fact]
    public void TheSetWideLoopIsNotBoundedByDuration()
    {
        var body = WaitBody();

        var uses = Regex.Matches(body, @"\bmaxWaitMs\b").Count;
        var handOff = Regex.Matches(body, @"WaitForFileReady\s*\([^)]*\bmaxWaitMs\b[^)]*\)").Count;

        Assert.Equal(1, handOff);
        Assert.Equal(handOff, uses);
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
