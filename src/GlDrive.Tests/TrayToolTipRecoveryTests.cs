using GlDrive.UI;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using System.Windows.Threading;
using Xunit;

namespace GlDrive.Tests;

[Collection("WpfRender")]
public sealed class TrayToolTipRecoveryTests
{
    [Fact]
    public void Native_tooltip_boundary_recovers_when_shell_has_lost_the_icon()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var icon = new TaskbarIcon { Id = Guid.NewGuid(), ToolTipText = "Old status" };
                // Reproduce Explorer losing this icon while H.NotifyIcon still believes
                // it exists. No shell icon is created and the random GUID cannot address
                // the user's running application. Exercise the real native failure path.
                typeof(TrayIcon).GetProperty(nameof(TrayIcon.IsCreated))!.SetValue(icon.TrayIcon, true);

                TrayIconSetup.UpdateToolTip(icon, "New status");

                Assert.Equal("New status", icon.ToolTipText);
                Assert.Equal("New status", icon.TrayIcon.ToolTip);
            }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Native tooltip test did not finish");
        Assert.Null(failure);
    }

    [Fact]
    public void Successful_update_does_not_invoke_recovery()
    {
        var updated = false;
        var retained = false;

        Assert.True(TrayIconSetup.TryUpdateToolTip(() => updated = true, () => retained = true));

        Assert.True(updated);
        Assert.False(retained);
    }

    [Fact]
    public void Shell_rejection_retains_latest_tooltip_for_taskbar_recreation()
    {
        var cachedToolTip = "GlDrive — connecting";
        const string requestedToolTip = "GlDrive — connected";
        var continued = false;
        var updated = true;

        var error = Record.Exception(() =>
        {
            updated = TrayIconSetup.TryUpdateToolTip(
                () => throw new InvalidOperationException("UpdateToolTip failed."),
                () => cachedToolTip = requestedToolTip);
            continued = true;
        });

        Assert.Null(error);
        Assert.False(updated);
        Assert.Equal(requestedToolTip, cachedToolTip);
        Assert.True(continued);
    }

    [Fact]
    public void Unrelated_update_failure_is_not_hidden()
    {
        var failure = new InvalidOperationException("Application state is invalid.");
        var retained = false;

        var error = Assert.Throws<InvalidOperationException>(() => TrayIconSetup.TryUpdateToolTip(
            () => throw failure, () => retained = true));

        Assert.Same(failure, error);
        Assert.False(retained);
    }
}
