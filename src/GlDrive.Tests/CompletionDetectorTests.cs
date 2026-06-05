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
