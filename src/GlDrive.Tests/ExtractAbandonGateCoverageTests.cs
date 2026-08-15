using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Regression cover for the 2026-08-14 finding: the durable abandon record introduced in
/// v3.10.57 to survive restarts was bypassed BY THE RESTART PATH.
///
/// <c>HandleWatchedFileAsync</c> (the watcher route) asked the store before extracting.
/// <c>ScanAndAutoExtractAsync</c> (the startup / recovery-scan route) did not — it went
/// straight to <c>IsAlreadyExtracted</c> and then <c>AutoExtractItem</c>. So every launch
/// re-ran three archives already recorded unextractable on 2026-08-13T17:56Z, failing
/// byte-identically each time (hackers: "expected 16924333715 found 1994329334").
///
/// This is the same shape as the CPSV desync guard that sat in one caller's catch while six
/// other borrowers went unguarded: an invariant about a resource must sit where every caller
/// reaches it. Both routes now go through <c>SkipIfAbandoned</c>.
///
/// The extractor lives in a WPF window that cannot be constructed under xUnit, so this pins
/// the invariant structurally — the guarantee is "every route into extraction consults the
/// store", and that is a property of the source, not of a runtime value. A future third
/// route that forgets the check fails here.
/// </summary>
public sealed class ExtractAbandonGateCoverageTests
{
    /// <summary>
    /// Source with comments removed. Essential, not cosmetic: the first version of these
    /// tests matched the words "IsAlreadyExtracted" and "SkipIfAbandoned" inside the very
    /// comments that explain the ordering, and reported the call order backwards. A test
    /// that reads prose is not testing the code.
    /// </summary>
    private static string ExtractorCode()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (; dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "GlDrive", "UI", "ExtractorWindow.xaml.cs");
            if (File.Exists(candidate)) return StripComments(File.ReadAllText(candidate));
        }

        throw new FileNotFoundException(
            "ExtractorWindow.xaml.cs not found walking up from " + Directory.GetCurrentDirectory());
    }

    private static string StripComments(string source)
    {
        source = Regex.Replace(source, @"/\*[\s\S]*?\*/", "");
        source = Regex.Replace(source, @"//[^\n]*", "");
        return source;
    }

    /// <summary>
    /// Body of a method, anchored on its DECLARATION (modifiers + signature), not on the
    /// first textual occurrence of the name — which is often a call site.
    /// </summary>
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
            else if (code[i] == '}')
            {
                depth--;
                if (depth == 0) return code.Substring(start, i - start + 1);
            }
        }

        throw new Xunit.Sdk.XunitException($"Unbalanced braces walking the body of {name}.");
    }

    /// <summary>
    /// The abandon store must be consulted through the shared helper only. A direct
    /// ShouldSkip call elsewhere is how the two routes drifted apart in the first place.
    /// </summary>
    [Fact]
    public void AbandonStore_is_consulted_only_through_the_shared_helper()
    {
        var code = ExtractorCode();

        var directCalls = Regex.Matches(code, @"_abandonStore\s*\.\s*ShouldSkip").Count;

        Assert.True(directCalls == 1,
            $"Expected exactly one _abandonStore.ShouldSkip call (inside SkipIfAbandoned); found {directCalls}. " +
            "Route the check through SkipIfAbandoned so every path into extraction shares it.");

        Assert.Contains("_abandonStore.ShouldSkip", MethodBody(code, "SkipIfAbandoned"));
    }

    /// <summary>
    /// The startup/recovery scan is the route that regressed. Pin it explicitly: it must
    /// call the gate, and must do so before the expensive IsAlreadyExtracted probe, which
    /// opens the archive to compare entries.
    /// </summary>
    [Fact]
    public void StartupRecoveryScan_consults_the_gate_before_extracting()
    {
        var body = MethodBody(ExtractorCode(), "ScanAndAutoExtractAsync");

        var gateIndex = body.IndexOf("SkipIfAbandoned", System.StringComparison.Ordinal);
        var extractIndex = body.IndexOf("AutoExtractItem", System.StringComparison.Ordinal);
        var alreadyIndex = body.IndexOf("IsAlreadyExtracted", System.StringComparison.Ordinal);

        Assert.True(gateIndex >= 0,
            "ScanAndAutoExtractAsync must call SkipIfAbandoned — this is the restart path the " +
            "durable record exists for.");
        Assert.True(gateIndex < extractIndex,
            "SkipIfAbandoned must run before AutoExtractItem.");
        Assert.True(gateIndex < alreadyIndex,
            "SkipIfAbandoned must run before IsAlreadyExtracted, which opens the archive.");
    }

    /// <summary>
    /// The watcher route must keep its check too — fixing one caller by moving the guard
    /// must not quietly unguard the other.
    /// </summary>
    [Fact]
    public void WatcherRoute_still_consults_the_gate() =>
        Assert.Contains("SkipIfAbandoned", MethodBody(ExtractorCode(), "HandleWatchedFileAsync"));

    /// <summary>
    /// The set-wide readiness gate must stay on the watcher route. Reverting it to the
    /// first-volume-only WaitForFileReady call is the v3.10.62 defect returning.
    /// </summary>
    [Fact]
    public void WatcherRoute_gates_on_the_whole_volume_set() =>
        Assert.Contains("WaitForVolumeSetReady", MethodBody(ExtractorCode(), "HandleWatchedFileAsync"));
}
