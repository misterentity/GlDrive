using GlDrive.Services;
using GlDrive.UI;
using GlDrive.Ftp;
using System.Runtime.CompilerServices;
using System.Reflection;
using Xunit;

namespace GlDrive.Tests;

public sealed class AuthenticationNotificationTests
{
    [Fact]
    public void Forwarder_acknowledges_only_a_live_notification_consumer()
    {
        // No constructor, file access, timers or connections: exercise forwarding only.
        var manager = (ServerManager)RuntimeHelpers.GetUninitializedObject(typeof(ServerManager));
        var notice = new FtpAuthenticationNotice(new("ftp.example.invalid", 21, "fixture"), DateTime.UtcNow);
        Assert.False(manager.OnAuthenticationRejected(notice));
        var calls = 0;
        Action<FtpAuthenticationNotice> listener = _ => calls++;
        manager.AuthenticationRejected += listener;
        Assert.True(manager.OnAuthenticationRejected(notice));
        Assert.Equal(1, calls);
        manager.AuthenticationRejected -= listener;
        Assert.False(manager.OnAuthenticationRejected(notice));
        manager.AuthenticationRejected += listener;
        typeof(ServerManager).GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(manager, true);
        Assert.False(manager.OnAuthenticationRejected(notice));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void Held_live_services_keep_a_manual_retry_action(bool active, bool held, bool offer) =>
        Assert.Equal(offer, TrayIconSetup.ShouldOfferConnectionRetry(active, held));

    [Theory]
    [InlineData(MountState.Error, false, true)]
    [InlineData(MountState.Error, true, false)]
    [InlineData(MountState.Reconnecting, false, true)]
    [InlineData(MountState.Reconnecting, true, false)]
    [InlineData(MountState.Connected, false, true)]
    [InlineData(MountState.Connected, true, true)]
    [InlineData(MountState.Connecting, false, false)]
    [InlineData(MountState.Unmounted, false, false)]
    public void Authentication_notice_replaces_repeated_error_and_reconnect_toasts(
        MountState state, bool held, bool notify) =>
        Assert.Equal(notify, TrayViewModel.ShouldNotifyConnectionState(state, held));
}
