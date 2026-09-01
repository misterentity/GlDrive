using GlDrive.Config;
using GlDrive.Irc;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Production 2026-08-31 23:44: all three IRC networks disconnected together. After reconnect,
/// SITE INVITE reported success, but four invite-only channels stayed out for the next nine
/// hours. Each superbnc retry also emitted three SITE commands in under one second—one from each
/// channel timer—even though one command invites every channel.
/// </summary>
public sealed class IrcSiteInviteRecoveryTests
{
    [Fact]
    public void PrimaryNickKeepsExplicitInviteNickSemantics()
    {
        var irc = new IrcConfig { Nick = "primary", InviteNick = "registered-primary" };

        Assert.Equal("registered-primary", IrcService.ResolveSiteInviteNick(irc, "primary"));
    }

    [Theory]
    [InlineData("alternate")]
    [InlineData("primary_")]
    public void NickCollisionTargetsTheNickActuallyAcceptedByIrc(string activeNick)
    {
        var irc = new IrcConfig { Nick = "primary", InviteNick = "primary" };

        Assert.Equal(activeNick, IrcService.ResolveSiteInviteNick(irc, activeNick));
    }

    [Fact]
    public void EmptyInviteNickStillDisablesSiteInviteAfterNickFallback()
    {
        var irc = new IrcConfig { Nick = "primary", InviteNick = "" };

        Assert.Equal("", IrcService.ResolveSiteInviteNick(irc, "alternate"));
    }

    [Fact]
    public async Task ConcurrentChannelRetriesShareOneSiteInvite()
    {
        var gate = new SiteInviteRequestGate();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        async Task Request()
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await release.Task;
        }

        var requests = Enumerable.Range(0, 6).Select(_ => gate.RunAsync(Request)).ToArray();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.All(requests, task => Assert.Same(requests[0], task));

        release.SetResult();
        await Task.WhenAll(requests);
    }

    [Fact]
    public async Task ACompletedInviteDoesNotSuppressTheNextRetryCycle()
    {
        var gate = new SiteInviteRequestGate();
        var calls = 0;

        await gate.RunAsync(() =>
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        });
        await gate.RunAsync(() =>
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        });

        Assert.Equal(2, calls);
    }
}
