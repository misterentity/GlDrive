using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

public class CompletionDetectorTests
{
    private static readonly string[] Markers =
        ["[ COMPLETE ]", "(COMPLETE)", "COMPLETE", "-=COMPLETE=-"];

    [Theory]
    [InlineData("[ COMPLETE ] - 1080p - iNTERNAL", true)]
    [InlineData("(COMPLETE)", true)]
    [InlineData("-=COMPLETE=- (TEAM)", true)]
    [InlineData("complete", true)]                 // case-insensitive
    [InlineData("Sample", false)]
    [InlineData("file.rar", false)]
    [InlineData("", false)]
    public void IsCompletionMarker_matches_configured_substrings(string name, bool expected)
        => Assert.Equal(expected, CompletionDetector.IsCompletionMarker(name, Markers));

    [Fact]
    public void IsCompletionMarker_empty_marker_list_never_matches()
        => Assert.False(CompletionDetector.IsCompletionMarker("[ COMPLETE ]", System.Array.Empty<string>()));

    // The glftpd race progress bar contains "NN% Complete" while the race is
    // STILL RUNNING — it must never satisfy a completion marker (the bare
    // "COMPLETE" config entry substring-matched it and ended races at 2/14
    // files on 2026-06-08). A 100% bar is a real completion signal.
    [Theory]
    [InlineData("[#####:::::] - 27% Complete - [SITE]", false)]
    [InlineData("[::::::::::] - 0% Complete - [SITE]", false)]
    [InlineData("[#########:] - 99% Complete - [SITE]", false)]
    [InlineData("[##########] - 100% Complete - [SITE]", true)]
    [InlineData("27% complete", false)]                  // lowercase, no brackets
    [InlineData("[xxx] - 50%-Complete - [xxx]", false)]  // dash separator variant
    [InlineData("[ COMPLETE ] - 1080p", true)]           // real tag unaffected
    [InlineData("-=COMPLETE=- (TEAM)", true)]
    public void IsCompletionMarker_rejects_in_progress_race_bar(string name, bool expected)
        => Assert.Equal(expected, CompletionDetector.IsCompletionMarker(name, Markers));

    // glftpd writes a "[ Incomplete ]"-style status stub for a release that received
    // the SFV but is still missing files. It contains the substring "complete" yet means
    // the OPPOSITE — the bare "COMPLETE" marker substring-matched it and ended SYN's race
    // at 4/22 files (Ryan.Hamilton, 2026-06-26). It must never count as a marker.
    [Theory]
    [InlineData("[ Incomplete ]", false)]
    [InlineData("incomplete", false)]
    [InlineData("INCOMPLETE", false)]
    [InlineData("[ INCOMPLETE ] - awaiting 21F", false)]
    [InlineData("release.incomplete.html", false)]
    [InlineData("in-complete", false)]
    [InlineData("uncompleted", false)]               // word-boundary: bare COMPLETE doesn't substring-hit
    [InlineData("[ COMPLETE ]", true)]               // real tag still matches
    [InlineData("[##########] - 100% Complete", true)]
    public void IsCompletionMarker_rejects_incomplete_status_stub(string name, bool expected)
        => Assert.Equal(expected, CompletionDetector.IsCompletionMarker(name, Markers));

    [Theory]
    [InlineData("-MISSING-file.r01", 0, true)]
    [InlineData("-missing-file.r01", 0, true)]
    [InlineData("file.rar.missing", 0, true)]
    [InlineData("file.rar-missing", 0, true)]
    [InlineData("-foo", 0, true)]                   // 0-byte dash stub
    [InlineData("file.rar", 1000, false)]
    [InlineData("file.rar", 0, false)]             // 0-byte real-ish name, no dash
    public void IsMissingStub_detects_missing_placeholders(string name, long size, bool expected)
        => Assert.Equal(expected, CompletionDetector.IsMissingStub(name, size));

    [Theory]
    [InlineData(0, 10, true, false, DestState.Complete)]
    [InlineData(10, 10, false, false, DestState.Complete)]
    [InlineData(11, 10, false, false, DestState.Complete)]
    [InlineData(10, 10, false, true, DestState.AwaitingCompletion)]
    [InlineData(5, 10, false, false, DestState.Transferring)]
    [InlineData(3, 0, false, false, DestState.Transferring)]
    public void Evaluate_returns_expected_state(
        int owned, int total, bool sawMarker, bool hasMissing, DestState expected)
        => Assert.Equal(expected, CompletionDetector.Evaluate(owned, total, sawMarker, hasMissing));

    [Fact]
    public void AllTerminal_true_only_when_every_dest_complete_or_timedout()
    {
        Assert.True(CompletionDetector.AllTerminal(new[] { DestState.Complete, DestState.TimedOut }));
        Assert.True(CompletionDetector.AllTerminal(new[] { DestState.Complete, DestState.Complete }));
        Assert.False(CompletionDetector.AllTerminal(new[] { DestState.Complete, DestState.AwaitingCompletion }));
        Assert.False(CompletionDetector.AllTerminal(new[] { DestState.Transferring }));
        Assert.False(CompletionDetector.AllTerminal(System.Array.Empty<DestState>()));
    }

    [Theory]
    [InlineData(0, 10, false)]
    [InlineData(10, 10, true)]
    [InlineData(15, 10, true)]
    public void IsAwaitExpired_uses_minutes_budget(double elapsedMin, int budgetMin, bool expected)
    {
        var allFilesAt = new System.DateTime(2026, 1, 1, 0, 0, 0, System.DateTimeKind.Utc);
        var now = allFilesAt.AddMinutes(elapsedMin);
        Assert.Equal(expected, CompletionDetector.IsAwaitExpired(allFilesAt, now, budgetMin));
    }
}
