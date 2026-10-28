using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GlDrive.Config;

namespace GlDrive.Services;

internal enum RuntimeConfigEffect { Live, Remount, Irc, Notifications, Search, NextDriveMount }

/// <summary>Explicit runtime ownership of every setting. Snapshots contain digests, never values.</summary>
internal static class RuntimeConfigPolicy
{
    internal static readonly IReadOnlyDictionary<Type, IReadOnlyDictionary<string, RuntimeConfigEffect>> Properties =
        new Dictionary<Type, IReadOnlyDictionary<string, RuntimeConfigEffect>>
        {
            [typeof(ServerConfig)] = Map("Id Name Enabled SpeedLimitKbps SiteStatsCommand", remount: "Connection Mount Tls Pool", irc: "Irc", notifications: "Notifications", search: "Search", live: "Cache SpreadSite"),
            [typeof(ConnectionConfig)] = Map(remount: "Host Port Username RootPath PassivePorts Proxy"),
            [typeof(ProxyConfig)] = Map(remount: "Enabled Host Port Username"),
            [typeof(MountConfig)] = Map("AutoMountOnStart", remount: "DriveLetter VolumeLabel MountDrive"),
            [typeof(TlsConfig)] = Map(remount: "PreferTls12 CertificateFingerprintFile"),
            [typeof(PoolConfig)] = Map("KeepaliveIntervalSeconds", remount: "PoolSize ReconnectInitialDelaySeconds ReconnectMaxDelaySeconds LoginCap LoginHeadroom"),
            [typeof(CacheConfig)] = Map("DirectoryListingTtlSeconds MaxCachedDirectories DirectoryListTimeoutSeconds ReadBufferSpillThresholdMb", deferred: "FileInfoTimeoutMs"),
            [typeof(NotificationConfig)] = Map(notifications: "Enabled PollIntervalSeconds WatchPath ExcludedCategories"),
            [typeof(SearchConfig)] = Map(search: "SearchPaths MaxDepth Method IndexCacheMinutes"),
            [typeof(DownloadConfig)] = Map("LocalPath CategoryPaths MaxConcurrentDownloads StreamingBufferSizeKb WriteBufferLimitMb QualityDefault OmdbApiKey TmdbApiKey OpenRouterModel AutoDownloadWishlist AutoExtract DeleteArchivesAfterExtract SpeedLimitKbps SkipIncompleteReleases MaxRetries RetryDelaySeconds ScheduleEnabled ScheduleStartHour ScheduleEndHour VerifySfv MultiStream PlaySoundOnComplete Theme CyberpunkPromoted"),
            [typeof(MultiStreamConfig)] = Map("Enabled BorrowIdleRaceLogin HttpLanesPerNetwork FtpMinSplitMb SlowMode SlowModeKbps Networks"),
            [typeof(NetworkPathConfig)] = Map("AdapterId AdapterName FriendlyName Enabled SpeedLimitKbps AllowanceGb Period"),
            [typeof(SiteSpreadConfig)] = Map("Sections Priority MaxUploadSlots MaxDownloadSlots DownloadOnly NeverSource Skiplist Affils SectionMappings MetadataFilter"),
            [typeof(MetadataFilterConfig)] = Map("Enabled MinImdbRating MinVotes AllowGenres DenyGenres SkipEndedShows LookupTimeoutSeconds"),
            [typeof(SectionMapping)] = Map("IrcSection RemoteSection Path TriggerRegex TagRules Enabled"),
            [typeof(SkiplistRule)] = Map("Pattern IsRegex Action Scope MatchDirectories MatchFiles Section Expression"),
            [typeof(IrcConfig)] = Map(irc: "Enabled Host Port UseTls Nick AltNick RealName AutoConnect InviteNick FishEnabled FishMode FallbackCharset Channels AnnounceRules RequestFiller"),
            [typeof(IrcChannelConfig)] = Map(irc: "Name Key AutoJoin"),
            [typeof(IrcAnnounceRule)] = Map(irc: "Enabled Channel Pattern AutoRace"),
            [typeof(RequestFillerConfig)] = Map(irc: "Enabled Pattern Channel MaxPerHour CooldownSeconds")
        };

    private static IReadOnlyDictionary<string, RuntimeConfigEffect> Map(string basic = "", string remount = "", string irc = "", string notifications = "", string search = "", string live = "", string deferred = "")
    {
        var result = new Dictionary<string, RuntimeConfigEffect>(StringComparer.Ordinal);
        foreach (var (names, effect) in new[] { (basic + " " + live, RuntimeConfigEffect.Live), (remount, RuntimeConfigEffect.Remount), (irc, RuntimeConfigEffect.Irc), (notifications, RuntimeConfigEffect.Notifications), (search, RuntimeConfigEffect.Search), (deferred, RuntimeConfigEffect.NextDriveMount) })
            foreach (var name in names.Split(' ', StringSplitOptions.RemoveEmptyEntries)) result.Add(name, effect);
        return result;
    }

    internal sealed record Value(RuntimeConfigEffect Effect, string Digest);
    internal sealed record Snapshot(IReadOnlyDictionary<string, Value> Values)
    {
        public string Fingerprint => string.Join(";", Values.Where(p => p.Value.Effect == RuntimeConfigEffect.Remount).OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value.Digest));
        public IReadOnlyList<string> Changed(Snapshot previous, RuntimeConfigEffect? effect = null) => Values
            .Where(p => (!effect.HasValue || p.Value.Effect == effect) && (!previous.Values.TryGetValue(p.Key, out var old) || old != p.Value))
            .Select(p => p.Key).ToArray();
    }

    public static Snapshot Capture(ServerConfig server, DownloadConfig downloads, string credentialsHash, int spreadPoolSize)
    {
        var values = new Dictionary<string, Value>(StringComparer.Ordinal);
        Add(server, typeof(ServerConfig), "", values);
        Add(downloads, typeof(DownloadConfig), "downloads.", values);
        values["connection.credentials"] = new(RuntimeConfigEffect.Remount, Hash(credentialsHash));
        values["spread.spreadPoolSize"] = new(RuntimeConfigEffect.Remount, Hash(spreadPoolSize));
        return new(values);
    }

    private static void Add(object? config, Type type, string prefix, Dictionary<string, Value> values)
    {
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead && p.CanWrite))
        {
            if (!Properties[type].TryGetValue(property.Name, out var effect))
                throw new InvalidOperationException($"Unclassified runtime setting: {type.Name}.{property.Name}");
            var path = prefix + char.ToLowerInvariant(property.Name[0]) + property.Name[1..];
            var value = config == null ? null : property.GetValue(config);
            if (Properties.ContainsKey(property.PropertyType)) Add(value, property.PropertyType, path + ".", values);
            else values[path] = new(effect, Hash(value));
        }
    }

    internal static string Hash(object? value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
}
