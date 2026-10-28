using GlDrive.Config;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

public sealed class RaceControlPolicyTests
{
    internal static readonly ServerConfig Destination = new()
    {
        Id = "dst", Connection = new() { Host = "site.invalid", Port = 21, Username = "account" }
    };
    internal static PersistedRace Request(bool partial = false) => new("video", "Example.Release", ["src", "dst", "third"],
        SpreadMode.Race, "src", "/video/Example.Release", DateTime.UtcNow.AddHours(-1))
    {
        ResumeCount = 2, CleanupAttempts = 1, WaitAttempts = 4,
        NextProbeAtUtc = DateTime.UtcNow.AddHours(1), SourceHints = [new("src", "video", "/video/Example.Release"), new("third", "video")],
        Partial = partial ? new()
        {
            FirstUploadAtUtc = DateTime.UtcNow.AddHours(-1), HasLandedPayload = true,
            ExpectedCount = 2, ExpectedFiles = new() { ["a.rar"] = 10, ["b.rar"] = 10 },
            Directories = [new()
            {
                ServerId = "dst", RemotePath = "/video/Example.Release",
                AccountIdentity = RaceLifetimePolicy.AccountIdentity(Destination.Connection),
                AttemptedFiles = new() { ["a.rar"] = 10 }, DeliveredFiles = new() { ["a.rar"] = 10 }
            }]
        } : null
    };

    [Fact]
    public void QueuedMembershipEditsPreserveAgeBudgetsOwnershipAndSourceHints()
    {
        var original = Request(partial: true);
        var result = RaceControlPolicy.EditSite(original, "third", false, true, false, out var removed);
        Assert.True(result.Accepted); Assert.Equal(["src", "dst"], removed!.ServerIds);
        Assert.Equal(original.RequestId, removed.RequestId); Assert.Equal(original.QueuedAtUtc, removed.QueuedAtUtc);
        Assert.Equal(original.NextProbeAtUtc, removed.NextProbeAtUtc); Assert.Equal(original.ResumeCount, removed.ResumeCount);
        Assert.Same(original.Partial, removed.Partial); Assert.Single(removed.SourceHints);
        Assert.True(RaceControlPolicy.EditSite(removed, "fourth", true, true, false, out var added).Accepted);
        Assert.Equal(["src", "dst", "fourth"], added!.ServerIds);
        Assert.Equal(3, original.ServerIds.Count);
    }

    [Theory]
    [InlineData("src", false, true, false, "known-source")]
    [InlineData("dst", false, true, false, "owns-partial")]
    [InlineData("fourth", true, false, false, "site-unavailable")]
    [InlineData("fourth", true, true, true, "nuke-held")]
    [InlineData("src", true, true, false, "unchanged")]
    public void UnsafeOrNoOpMembershipChangesAreRejected(string site, bool add, bool available, bool nuked, string code)
    {
        var result = RaceControlPolicy.EditSite(Request(true), site, add, available, nuked, out var changed);
        Assert.False(result.Accepted); Assert.Equal(code, result.Code); Assert.Null(changed);
    }

    [Fact]
    public void RemovingLastDestinationOrChangingCleanupMembershipIsRejected()
    {
        var request = Request() with { ServerIds = ["src", "dst"] };
        Assert.Equal("too-few-sites", RaceControlPolicy.EditSite(request, "dst", false, true, false, out _).Code);
        Assert.Equal("cleanup-only", RaceControlPolicy.EditSite(request with { CleanupOnly = true }, "extra", true, true, false, out _).Code);
    }

    [Fact]
    public void ConfirmedCleanupAdvancesOnlyTheDueTimeAndKeepsAllSafetyEvidence()
    {
        var request = Request(true); var now = DateTime.UtcNow;
        var result = RaceControlPolicy.CleanupNow(request, [Destination], now, out var cleanup);
        Assert.True(result.Accepted); Assert.True(cleanup!.CleanupOnly); Assert.Equal(now, cleanup.NextProbeAtUtc);
        Assert.Equal(request.ResumeCount, cleanup.ResumeCount); Assert.Equal(request.CleanupAttempts, cleanup.CleanupAttempts);
        Assert.Equal(request.FirstUploadAtUtc, cleanup.FirstUploadAtUtc); Assert.Same(request.Partial, cleanup.Partial);
        Assert.Equal(RaceStopReason.UserStop.ToString(), cleanup.StopReason);
    }

    [Theory]
    [InlineData("account")]
    [InlineData("path")]
    [InlineData("complete")]
    [InlineData("budget")]
    [InlineData("missing")]
    public void CleanupCannotBroadenOwnershipOrResetSpentBudget(string change)
    {
        var request = Request(true);
        if (change == "account") request.Partial!.Directories[0] = request.Partial.Directories[0] with { AccountIdentity = "other" };
        if (change == "path") request.Partial!.Directories[0] = request.Partial.Directories[0] with { RemotePath = "/video" };
        if (change == "complete") request.Partial!.Directories[0] = request.Partial.Directories[0] with { CompletionConfirmed = true };
        if (change == "budget") request = request with { CleanupAttempts = RaceLifetimePolicy.MaxCleanupAttempts };
        if (change == "missing") request = request with { Partial = null };
        Assert.False(RaceControlPolicy.CleanupNow(request, [Destination], DateTime.UtcNow, out var changed).Accepted);
        Assert.Null(changed);
    }
}
