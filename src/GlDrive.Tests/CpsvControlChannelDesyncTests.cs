using FluentFTP;
using GlDrive.Ftp;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// v3.10.55 — a CPSV op is a multi-reply sequence on the control channel
/// (CPSV -> 227, LIST/RETR/STOR -> 150, transfer, -> 226). Abort it anywhere in
/// the middle and the channel is left holding an unread reply; the next command
/// on that connection reads the STALE one. That is the production signature
///
///     System.IO.IOException: Failed to parse CPSV response: Type set to A.
///
/// — CPSV reading back TYPE A's leftover 200.
///
/// <c>FtpOperations.ListDirectory</c> already poisoned its borrowed connection on
/// failure, but the guard was attached to ONE CALL SITE rather than to the
/// connection it protects. Six other callers (NewReleaseMonitor, SpreadJob,
/// SpreadManager, FtpSearchService, StreamingDownloader, MediaStreamServer) hand
/// the desynced connection straight back to the pool. Observed 2026-08-11: three
/// consecutive NewReleaseMonitor polls, 60s apart, each inheriting the same
/// broken connection from the previous one.
///
/// The fix keys on the property that defines the hazard — "this control channel
/// owes us a reply" — and stores it on the client, so every borrower's dispose
/// path discards it regardless of which code borrowed it.
/// </summary>
public class CpsvControlChannelDesyncTests
{
    private static AsyncFtpClient NewClient() => new("example.invalid", "u", "p");

    [Fact]
    public void FreshClient_HasNoPendingSequence()
    {
        using var c = NewClient();
        Assert.False(CpsvDataHelper.HasPendingDataSequence(c));
    }

    [Fact]
    public void StartedSequence_MarksTheChannelAsOwingAReply()
    {
        using var c = NewClient();
        CpsvDataHelper.BeginDataSequence(c);

        Assert.True(CpsvDataHelper.HasPendingDataSequence(c));
    }

    [Fact]
    public void CompletedSequence_ClearsTheMark()
    {
        using var c = NewClient();
        CpsvDataHelper.BeginDataSequence(c);
        CpsvDataHelper.EndDataSequence(c);

        Assert.False(CpsvDataHelper.HasPendingDataSequence(c));
    }

    [Fact]
    public void AbandonedSequence_StaysMarked()
    {
        // The whole point: no clear happened, so the connection must not look
        // reusable. This is the state after any throw between CPSV and the 226.
        using var c = NewClient();
        CpsvDataHelper.BeginDataSequence(c);

        Assert.True(CpsvDataHelper.HasPendingDataSequence(c));
    }

    [Fact]
    public void MarkIsPerConnection_NotGlobal()
    {
        // A static table keyed wrongly would quarantine every pooled connection
        // the moment one op failed — turning a single desync into a pool-wide
        // outage. Independence is the load-bearing property here.
        using var broken = NewClient();
        using var healthy = NewClient();

        CpsvDataHelper.BeginDataSequence(broken);

        Assert.True(CpsvDataHelper.HasPendingDataSequence(broken));
        Assert.False(CpsvDataHelper.HasPendingDataSequence(healthy));
    }

    [Fact]
    public void CompletingOneConnection_DoesNotClearAnother()
    {
        using var a = NewClient();
        using var b = NewClient();
        CpsvDataHelper.BeginDataSequence(a);
        CpsvDataHelper.BeginDataSequence(b);

        CpsvDataHelper.EndDataSequence(a);

        Assert.False(CpsvDataHelper.HasPendingDataSequence(a));
        Assert.True(CpsvDataHelper.HasPendingDataSequence(b));
    }

    [Fact]
    public void RepeatedBegin_IsIdempotent()
    {
        // Publics mark before their TYPE command and OpenDataTcp marks again
        // before CPSV; the second mark must not throw (ConditionalWeakTable.Add
        // would) nor require a matching second clear.
        using var c = NewClient();
        CpsvDataHelper.BeginDataSequence(c);
        CpsvDataHelper.BeginDataSequence(c);

        CpsvDataHelper.EndDataSequence(c);

        Assert.False(CpsvDataHelper.HasPendingDataSequence(c));
    }

    [Fact]
    public void ClearingAnUnmarkedConnection_IsHarmless()
    {
        // Non-CPSV paths share the same teardown code; clearing a mark that was
        // never set must be a no-op rather than an exception.
        using var c = NewClient();
        CpsvDataHelper.EndDataSequence(c);

        Assert.False(CpsvDataHelper.HasPendingDataSequence(c));
    }

    [Fact]
    public void ReusedAfterCompletion_IsMarkedAgainByTheNextSequence()
    {
        // A connection that completed cleanly goes back to the pool and gets
        // borrowed again; the next sequence must re-arm, not inherit "clean".
        using var c = NewClient();
        CpsvDataHelper.BeginDataSequence(c);
        CpsvDataHelper.EndDataSequence(c);

        CpsvDataHelper.BeginDataSequence(c);

        Assert.True(CpsvDataHelper.HasPendingDataSequence(c));
    }
}
