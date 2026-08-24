using GlDrive.UI;
using Xunit;

namespace GlDrive.Tests;

public sealed class TrayNotificationSafetyTests
{
    [Fact]
    public void Notification_success_is_reported()
    {
        var called = false;

        var shown = TrayIconSetup.TryShowNotification(() => called = true);

        Assert.True(shown);
        Assert.True(called);
    }

    [Fact]
    public void Notification_provider_exception_is_contained()
    {
        var shown = true;
        var exception = Record.Exception(() =>
            shown = TrayIconSetup.TryShowNotification(() =>
                throw new InvalidOperationException("Show notification failed.")));

        Assert.Null(exception);
        Assert.False(shown);
    }
}
