using System.IO;
using System.Text.Json;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Services.Automation;
using Xunit;

namespace GlDrive.Tests;

public class AutomationServiceTests
{
    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);
    private static AutomationService Service() => new(new AppConfig(), null!, new NotificationStore(persistToDisk: false));

    [Theory]
    [InlineData("/incoming/../other")]
    [InlineData("/incoming/./other")]
    [InlineData("relative/path")]
    [InlineData("/release\r\nDELE /other")]
    [InlineData("/release\0suffix")]
    public void RemotePathsRejectTraversalRelativeNamesAndProtocolInjection(string path)
        => Assert.Throws<ArgumentException>(() => AutomationService.RemotePath(Args(new { path })));

    [Fact]
    public void RootIsProtectedForDestructiveOperationsAfterNormalization()
    {
        Assert.Throws<ArgumentException>(() => AutomationService.RemotePath(Args(new { path = "////" }), allowRoot: false));
        Assert.Equal("/incoming/release/file.nfo", AutomationService.RemotePath(Args(new { path = "/incoming//release\\file.nfo" })));
        Assert.Equal("/", AutomationService.RemotePath(Args(new { path = "/" })));
    }

    [Fact]
    public async Task InlineReadLimitIsEnforcedForStreamingWrites()
    {
        using var stream = new AutomationService.LimitedMemoryStream(5);
        await stream.WriteAsync(new byte[3].AsMemory());
        stream.Write(new byte[2], 0, 2);
        Assert.Equal(5, stream.Length);
        await Assert.ThrowsAsync<IOException>(async () => await stream.WriteAsync(new byte[1].AsMemory()));
        Assert.Throws<IOException>(() => stream.WriteByte(1));
        Assert.Equal(5, stream.Length);
    }

    [Theory]
    [InlineData("{\"limit\":\"100\"}")]
    [InlineData("{\"limit\":-1}")]
    [InlineData("{\"limit\":2001}")]
    [InlineData("{\"limit\":1.5}")]
    public void InvalidPaginationFailsAsArgumentError(string json)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Throws<ArgumentException>(() => AutomationService.Integer(doc.RootElement, "limit", 200, 1, 2000));
    }

    [Fact]
    public async Task NotificationReadsReturnDetachedPaginatedSnapshotAndClearSharedStore()
    {
        var store = new NotificationStore(persistToDisk: false);
        var first = new NotificationItem { Id = "first", ReleaseName = "one" };
        store.Add(first);
        store.Add(new NotificationItem { Id = "second", ReleaseName = "two" });
        var service = new AutomationService(new AppConfig(), null!, store);
        var snapshot = Assert.IsType<JsonElement>(await service.ExecuteAsync("notifications.list", Args(new { offset = 1, limit = 1 }), default));
        first.ReleaseName = "changed";
        Assert.Equal(2, snapshot.GetProperty("total").GetInt32());
        Assert.Equal("one", snapshot.GetProperty("items")[0].GetProperty("releaseName").GetString());
        await service.ExecuteAsync("notifications.clear", Args(new { }), default);
        Assert.Empty(store.Items);
        Assert.Equal("one", snapshot.GetProperty("items")[0].GetProperty("releaseName").GetString());
    }

    [Fact]
    public async Task CancelledOperationDoesNotMutateStore()
    {
        var store = new NotificationStore(persistToDisk: false);
        store.Add(new NotificationItem());
        var service = new AutomationService(new AppConfig(), null!, store);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ExecuteAsync("notifications.clear", Args(new { }), new CancellationToken(true)));
        Assert.Single(store.Items);
    }

    [Fact]
    public async Task MalformedArgumentsAndUnknownOperationAreRejected()
    {
        var service = Service();
        await Assert.ThrowsAsync<ArgumentException>(() => service.ExecuteAsync("notifications.clear", Args(new[] { 1 }), default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ExecuteAsync("not.a.real.operation", Args(new { }), default));
    }

    [Fact]
    public void CatalogNamesAreUniqueAndMutatingToolsHaveCorrectHints()
    {
        var tools = Service().Tools;
        Assert.True(tools.Count >= 60);
        Assert.Equal(tools.Count, tools.Select(t => t.Name).Distinct(StringComparer.Ordinal).Count());
        foreach (var tool in tools)
        {
            Assert.Equal("object", tool.InputSchema.GetProperty("type").GetString());
            Assert.False(tool.InputSchema.GetProperty("additionalProperties").GetBoolean());
            if (tool.Destructive) Assert.False(tool.ReadOnly);
        }
        Assert.True(tools.Single(t => t.Name == "ftp.delete").Destructive);
        Assert.False(tools.Single(t => t.Name == "irc.send").ReadOnly);
        Assert.True(tools.Single(t => t.Name == "irc.history").ReadOnly);
        Assert.True(tools.Single(t => t.Name == "downloads.list").ReadOnly);
    }
}
