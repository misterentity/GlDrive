using System.IO;
using System.Net;
using FluentFTP;
using GlDrive.Downloads.Segmented;
using Xunit;

namespace GlDrive.Tests;

public class FtpLaneNetworkBindingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Standard_data_open_binds_selected_adapter_then_restores_prior_configuration(bool existingBinding)
    {
        using var client = new AsyncFtpClient();
        var previous = existingBinding ? IPAddress.Parse("192.0.2.1") : null;
        var selected = IPAddress.Parse("192.0.2.2");
        client.Config.SocketLocalIp = previous;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var expected = new MemoryStream();
        var open = FtpLane.OpenStandardDataAsync(client, selected, async () =>
        {
            Assert.Equal(selected, client.Config.SocketLocalIp);
            entered.SetResult();
            await release.Task;
            Assert.Equal(selected, client.Config.SocketLocalIp);
            return expected;
        });
        await entered.Task;
        Assert.False(open.IsCompleted);
        release.SetResult();
        Assert.Same(expected, await open);
        Assert.Equal(previous, client.Config.SocketLocalIp);
        Assert.False(client.IsConnected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Standard_data_setup_failure_or_cancellation_restores_previous_binding(bool cancelled)
    {
        using var client = new AsyncFtpClient();
        var previous = IPAddress.Parse("192.0.2.1");
        var selected = IPAddress.Parse("192.0.2.2");
        client.Config.SocketLocalIp = previous;
        Exception expected = cancelled ? new OperationCanceledException() : new IOException("synthetic data setup failure");
        var caught = await Record.ExceptionAsync(() => FtpLane.OpenStandardDataAsync(client, selected, () =>
        {
            Assert.Equal(selected, client.Config.SocketLocalIp);
            return Task.FromException<Stream>(expected);
        }));
        Assert.Same(expected, caught);
        Assert.Equal(previous, client.Config.SocketLocalIp);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task Default_network_preserves_an_existing_client_binding_during_open()
    {
        using var client = new AsyncFtpClient();
        var previous = IPAddress.Parse("192.0.2.1");
        client.Config.SocketLocalIp = previous;
        using var stream = await FtpLane.OpenStandardDataAsync(client, null, () =>
        {
            Assert.Equal(previous, client.Config.SocketLocalIp);
            return Task.FromResult<Stream>(new MemoryStream());
        });
        Assert.Equal(previous, client.Config.SocketLocalIp);
    }
}
