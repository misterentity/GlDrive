using System.Reflection;
using GlDrive.Config;
using GlDrive.Services;
using Xunit;

namespace GlDrive.Tests;

public class RuntimeConfigPolicyTests
{
    [Fact]
    public void Every_server_and_download_property_including_nested_types_is_explicitly_classified()
    {
        var visited = new HashSet<Type>();
        Visit(typeof(ServerConfig));
        Visit(typeof(DownloadConfig));
        Assert.Equal(RuntimeConfigPolicy.Properties.Keys.OrderBy(t => t.Name), visited.OrderBy(t => t.Name));
        void Visit(Type type)
        {
            if (type.IsGenericType)
            {
                foreach (var argument in type.GetGenericArguments()) Visit(argument);
                return;
            }
            if (type.IsArray) { Visit(type.GetElementType()!); return; }
            if (type.Namespace != typeof(ServerConfig).Namespace || type.IsEnum || !visited.Add(type)) return;
            Assert.True(RuntimeConfigPolicy.Properties.TryGetValue(type, out var classified), $"Unclassified type: {type}");
            var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead && p.CanWrite).ToArray();
            Assert.Equal(properties.Select(p => p.Name).Order(), classified!.Keys.Order());
            foreach (var property in properties) Visit(property.PropertyType);
        }
    }

    [Theory]
    [InlineData("theme", false)]
    [InlineData("quality", false)]
    [InlineData("skiplist", false)]
    [InlineData("slots", false)]
    [InlineData("affils", false)]
    [InlineData("speed", false)]
    [InlineData("concurrency", false)]
    [InlineData("notifications", false)]
    [InlineData("search", false)]
    [InlineData("cache", false)]
    [InlineData("keepalive", false)]
    [InlineData("irc", false)]
    [InlineData("host", true)]
    [InlineData("port", true)]
    [InlineData("user", true)]
    [InlineData("pool", true)]
    [InlineData("drive", true)]
    [InlineData("tls", true)]
    [InlineData("root", true)]
    [InlineData("proxy", true)]
    [InlineData("headroom", true)]
    public void Fingerprint_changes_only_for_connection_resources(string edit, bool remount)
    {
        var server = new ServerConfig();
        var downloads = new DownloadConfig();
        var original = RuntimeConfigPolicy.Capture(server, downloads, "credential-hash", 3);
        switch (edit)
        {
            case "theme": downloads.Theme = "Other"; break;
            case "quality": downloads.QualityDefault = "2160p"; break;
            case "skiplist": server.SpreadSite.Skiplist.Add(new() { Pattern = "*.nfo" }); break;
            case "slots": server.SpreadSite.MaxUploadSlots++; break;
            case "affils": server.SpreadSite.Affils.Add("GROUP"); break;
            case "speed": server.SpeedLimitKbps++; break;
            case "concurrency": downloads.MaxConcurrentDownloads++; break;
            case "notifications": server.Notifications.WatchPath = "/new"; break;
            case "search": server.Search.IndexCacheMinutes++; break;
            case "cache": server.Cache.DirectoryListingTtlSeconds++; break;
            case "keepalive": server.Pool.KeepaliveIntervalSeconds++; break;
            case "irc": server.Irc.Host = "irc.example.test"; break;
            case "host": server.Connection.Host = "ftp.example.test"; break;
            case "port": server.Connection.Port++; break;
            case "user": server.Connection.Username = "new-user"; break;
            case "pool": server.Pool.PoolSize++; break;
            case "drive": server.Mount.DriveLetter = "Z"; break;
            case "tls": server.Tls.PreferTls12 = false; break;
            case "root": server.Connection.RootPath = "/site"; break;
            case "proxy": server.Connection.Proxy = new() { Enabled = true }; break;
            case "headroom": server.Pool.LoginHeadroom++; break;
            default: throw new ArgumentException(edit);
        }
        var changed = RuntimeConfigPolicy.Capture(server, downloads, "credential-hash", 3);
        Assert.Equal(remount, original.Fingerprint != changed.Fingerprint);
        Assert.NotEmpty(changed.Changed(original));
    }

    [Fact]
    public void Credential_and_spread_pool_changes_require_rebuild()
    {
        var server = new ServerConfig();
        var downloads = new DownloadConfig();
        var original = RuntimeConfigPolicy.Capture(server, downloads, "a", 3);
        Assert.NotEqual(original.Fingerprint, RuntimeConfigPolicy.Capture(server, downloads, "b", 3).Fingerprint);
        Assert.NotEqual(original.Fingerprint, RuntimeConfigPolicy.Capture(server, downloads, "a", 4).Fingerprint);
    }

    [Fact]
    public void Snapshots_preserve_before_state_without_retaining_values()
    {
        var server = new ServerConfig { Connection = new() { Host = "private.example.test" } };
        server.Irc.Channels.Add(new() { Key = "private-fish-key" });
        var before = RuntimeConfigPolicy.Capture(server, new(), "private-password", 3);
        server.Cache.FileInfoTimeoutMs++;
        var after = RuntimeConfigPolicy.Capture(server, new(), "private-password", 3);
        Assert.Equal(new[] { "cache.fileInfoTimeoutMs" }, after.Changed(before));
        Assert.Equal(RuntimeConfigEffect.NextDriveMount, after.Values["cache.fileInfoTimeoutMs"].Effect);
        Assert.DoesNotContain("private", string.Join(";", before.Values.Select(v => v.Value.Digest)), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before.Fingerprint, after.Fingerprint);
    }
}
