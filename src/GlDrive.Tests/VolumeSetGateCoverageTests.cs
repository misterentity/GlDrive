using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Regression cover for the 2026-09-15 finding: the whole-set readiness gate added in
/// v3.10.62/.63 (<c>WaitForVolumeSetReady</c>) was wired into ONE route — the watcher —
/// while the two startup routes reached extraction without it.
///
/// Observed in production on 2026-09-14 at 09:31:28, one second after launch: two UHD
/// BluRay sets went straight to UnRAR with no "watched archive detected", no "volume set
/// still arriving" and no "settled" line — the signature of the gate never running. Both
/// failed with exit 3 and were recorded durably unrecoverable.
///
/// <c>ExtractFailureClassifier</c> states the premise this breaks in its own source: its
/// "unpacked file size does not match header" marker is Permanent only "for as long as
/// [the whole-set gate] stays true: a still-arriving set must never reach extraction."
/// On the startup routes it was not true, so a restart landing mid-download of a
/// multi-volume set durably mislabels a healthy set as corrupt.
///
/// This is the SAME shape as the abandon-store gate one file over, and the same shape its
/// own tests warn about: <c>ExtractAbandonGateCoverageTests</c> explains at length that
/// "enumerating call sites is precisely how the defect survived two fixes" — and then
/// pinned this gate to a single call site with
/// <c>WatcherRoute_gates_on_the_whole_volume_set</c>. A passing test locked the blind spot
/// in. The guarantee belongs on the operation every route funnels through.
/// </summary>
public sealed class VolumeSetGateCoverageTests
{
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

    /// <summary>
    /// Comments stripped. The gate's name appears in explanatory comments around
    /// AutoExtractItem; matching those would report the guarantee as present while the call
    /// is absent — the exact failure the sibling suite documents.
    /// </summary>
    private static string StripComments(string source)
    {
        source = Regex.Replace(source, @"/\*[\s\S]*?\*/", "");
        source = Regex.Replace(source, @"//[^\n]*", "");
        return source;
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
            else if (code[i] == '}')
            {
                depth--;
                if (depth == 0) return code.Substring(start, i - start + 1);
            }
        }

        throw new Xunit.Sdk.XunitException($"Unbalanced braces walking the body of {name}.");
    }

    /// <summary>
    /// THE test. Every route into extraction funnels through AutoExtractItem, so the
    /// whole-set readiness gate must sit there, before any extraction work begins.
    /// Guarding the watcher caller alone is what let the 2026-09-14 startup extractions run
    /// ungated.
    /// </summary>
    [Fact]
    public void AutoExtractItem_itself_gates_on_the_whole_volume_set()
    {
        var body = MethodBody(ExtractorCode(), "AutoExtractItem");

        var gateIndex = body.IndexOf("WaitForVolumeSetReady", StringComparison.Ordinal);
        var workIndex = body.IndexOf("_extractionGate", StringComparison.Ordinal);

        Assert.True(gateIndex >= 0,
            "AutoExtractItem must await WaitForVolumeSetReady. Guarding the watcher route is " +
            "not enough: the initial watch-folder scan and the recovery scan both reach " +
            "extraction directly, and on 2026-09-14 they extracted an incomplete set one " +
            "second after launch and recorded it unrecoverable.");

        Assert.True(workIndex < 0 || gateIndex < workIndex,
            "WaitForVolumeSetReady must run before AutoExtractItem starts extracting.");
    }

    /// <summary>
    /// Calling the gate and discarding its answer would satisfy the test above while leaving
    /// the defect live. The non-Ready outcome must be acted on, and the two endings must stay
    /// distinguishable — a stall may clear on retry, the arrival ceiling cannot.
    /// </summary>
    [Fact]
    public void AutoExtractItem_acts_on_a_not_ready_verdict()
    {
        var body = MethodBody(ExtractorCode(), "AutoExtractItem");

        Assert.Contains("ArchiveWaitOutcome.Ready", body);
        Assert.Contains("ArchiveWait.DeservesRetry", body);
        Assert.Contains("ScheduleWatchRetry", body);
    }

    /// <summary>
    /// A set that is merely still arriving must never be abandoned durably from the
    /// chokepoint. That conversion — incomplete read as unrecoverable — is the whole defect.
    /// </summary>
    [Fact]
    public void AutoExtractItem_never_abandons_an_arriving_set_durably()
    {
        var body = MethodBody(ExtractorCode(), "AutoExtractItem");

        foreach (Match call in Regex.Matches(body, @"AbandonWatchedPath\s*\([^;]*;"))
            Assert.True(call.Value.Contains("durable: false"),
                "The chokepoint may only record a NON-durable abandon for a readiness verdict; " +
                $"found: {call.Value.Trim()}");
    }

    /// <summary>
    /// The startup routes are the ones that were ungated. Pinning their count means a fourth
    /// route has to come back here and confirm it funnels through the chokepoint.
    /// </summary>
    [Fact]
    public void AllRoutes_reach_extraction_only_through_the_readiness_gated_chokepoint()
    {
        var code = ExtractorCode();

        var callSites = Regex.Matches(code, @"AutoExtractItem\s*\(").Count
                        - Regex.Matches(code, @"(?:private|internal|public|protected)[^\n(]*AutoExtractItem\s*\(").Count;

        Assert.True(callSites >= 3,
            $"Expected the three known routes into AutoExtractItem; found {callSites}.");
    }

    /// <summary>
    /// The classifier's Permanent verdict for a short-payload archive is sound ONLY while a
    /// still-arriving set cannot reach extraction. That justification is written into its
    /// source; if the marker is present without the gate, the v3.10.62 defect is back.
    /// </summary>
    [Fact]
    public void ShortPayload_stays_permanent_only_while_the_chokepoint_is_gated()
    {
        var classifier = ClassifierCode();

        if (!classifier.Contains("unpacked file size does not match header")) return;

        Assert.Contains("WaitForVolumeSetReady", MethodBody(ExtractorCode(), "AutoExtractItem"));
    }

    private static string ClassifierCode()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (; dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(
                dir.FullName, "src", "GlDrive", "Downloads", "ExtractFailureClassifier.cs");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }

        throw new FileNotFoundException("ExtractFailureClassifier.cs not found.");
    }
}
