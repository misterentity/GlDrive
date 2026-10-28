using System.Text.Json;
using GlDrive.Config;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

public sealed class RaceAnnounceMergePolicyTests
{
    private const string Release = "Fixture.Show.S01E02.1080p.WEB.H264-GRP";
    private static readonly DateTime Now = new(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);
    private static ServerConfig Server(string id, bool sourceOnly = false, bool destinationOnly = false, bool enabled = true) =>
        new() { Id = id, Enabled = enabled, SpreadSite = new() { DownloadOnly = sourceOnly, NeverSource = destinationOnly } };
    private static PersistedRace Request(string section, string source, string? path = null) =>
        new(section, Release, [source], SpreadMode.Race, source, path, Now);

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, true, false)]
    [InlineData(false, true, false, false)]
    public void Only_enabled_non_never_source_roles_supply_hints(bool enabled, bool downloadOnly, bool neverSource, bool expected)
    {
        Assert.Equal(expected, RaceAnnounceMerge.CanSource(Server("site", downloadOnly, neverSource, enabled)));
        Assert.False(RaceAnnounceMerge.CanSource(null));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Source_and_destination_announce_order_cannot_shadow_real_source(bool destinationFirst)
    {
        var sites = new Dictionary<string, ServerConfig>
        {
            ["source"] = Server("source", sourceOnly: true), ["destination"] = Server("destination", destinationOnly: true)
        };
        var source = Request("tv-hd", "source", "/incoming/" + Release);
        var destination = Request("-TV-", "destination", "/received/" + Release);
        var first = destinationFirst ? destination : source;
        var second = destinationFirst ? source : destination;
        var merged = RaceAnnounceMerge.Merge(first, second, id => sites.GetValueOrDefault(id), true);
        Assert.Equal("source", merged.KnownSourceServerId);
        Assert.Equal("/incoming/" + Release, merged.KnownSourcePath);
        Assert.Equal("tv-hd", merged.Section);
        Assert.Equal(new[] { "destination", "source" }, merged.ServerIds.Order());
        Assert.Single(merged.SourceHints);
        Assert.DoesNotContain(merged.SourceHints, h => h.ServerId == "destination");
        Assert.Equal(first.RequestId, merged.RequestId);
    }

    [Fact]
    public void Source_only_site_outranks_both_role_even_without_an_explicit_path()
    {
        var both = Server("both");
        var source = Server("source", sourceOnly: true);
        var merged = RaceAnnounceMerge.Merge(Request("destination-alias", "both", "/received/" + Release),
            Request("source-alias", "source"), id => id == "both" ? both : source, true);
        Assert.Equal("source", merged.KnownSourceServerId);
        Assert.Equal("source-alias", merged.Section);
        Assert.Null(merged.KnownSourcePath);
    }

    [Fact]
    public void Same_rank_prefers_explicit_path_and_server_union_is_idempotent()
    {
        var site = Server("source");
        var current = Request("tv", "source") with { ServerIds = ["source", "destination"] };
        var incoming = Request("tv", "source", "/incoming/" + Release) with { ServerIds = ["source", "destination", "alternate"] };
        ServerConfig? Config(string id) => id == "source" ? site : null;
        var once = RaceAnnounceMerge.Merge(current, incoming, Config, true);
        var twice = RaceAnnounceMerge.Merge(once, incoming, Config, true);
        Assert.Equal(["source", "destination", "alternate"], once.ServerIds);
        Assert.Equal("/incoming/" + Release, once.KnownSourcePath);
        Assert.Single(once.SourceHints);
        Assert.Same(once, twice);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void Active_owned_or_cleanup_request_keeps_destination_section_and_actual_source_alias(
        bool canChangeSection, bool hasOwnedPartial, bool cleanup)
    {
        var source = Server("source", sourceOnly: true);
        var destination = Server("destination", destinationOnly: true);
        ServerConfig? Config(string id) => id == "source" ? source : id == "destination" ? destination : null;
        var current = Request("-TV-", "destination") with
        {
            CleanupOnly = cleanup,
            Partial = hasOwnedPartial ? new() { Directories = [new() { ServerId = "destination" }] } : null
        };
        var incoming = Request("tv-hd", "source");
        var merged = RaceAnnounceMerge.Merge(current, incoming, Config, canChangeSection);
        Assert.Equal("-TV-", merged.Section);
        Assert.Equal("source", merged.KnownSourceServerId);
        Assert.Equal("tv-hd", Assert.Single(merged.SourceHints).Section);
        Assert.Same(merged, RaceAnnounceMerge.Merge(merged, incoming, Config, canChangeSection));
    }

    [Fact]
    public void Merge_preserves_all_lifecycle_ownership_and_prediction_state()
    {
        var partial = new RacePartialState
        {
            FirstUploadAtUtc = Now.AddHours(-2), HasLandedPayload = true,
            Directories = [new() { ServerId = "destination", RemotePath = "/incoming/" + Release, AccountIdentity = "proof" }]
        };
        var current = Request("tv", "destination") with
        {
            QueuedAtUtc = Now.AddHours(-3), Resumes = 2, WasActive = true, NextProbeAtUtc = Now.AddMinutes(3),
            WaitReason = "source incomplete", WaitAttempts = 4, KeepUntilAvailable = true, ResumeCount = 3,
            StopReason = "Stall", Partial = partial, CleanupOnly = true, CleanupAttempts = 5,
            ExpectedBytes = 12345, PredictedSeconds = 120, PredictionBasis = "historic"
        };
        var source = Server("source", sourceOnly: true);
        var destination = Server("destination", destinationOnly: true);
        var merged = RaceAnnounceMerge.Merge(current, Request("source-tv", "source"),
            id => id == "source" ? source : destination, true);
        // Compare the immutable record after replacing exactly the permitted advisory fields.
        Assert.Equal(current, merged with
        {
            ServerIds = current.ServerIds, SourceHints = current.SourceHints, Section = current.Section,
            KnownSourceServerId = current.KnownSourceServerId, KnownSourcePath = current.KnownSourcePath
        });
        Assert.Same(partial, merged.Partial);
        var restored = JsonSerializer.Deserialize<PersistedRace>(JsonSerializer.Serialize(merged))!;
        Assert.Equal(current.RequestId, restored.RequestId);
        Assert.Equal(current.QueuedAtUtc, restored.QueuedAtUtc);
        Assert.Equal(current.NextProbeAtUtc, restored.NextProbeAtUtc);
        Assert.Equal(5, restored.CleanupAttempts);
        Assert.Equal(3, restored.ResumeCount);
        Assert.Equal("proof", Assert.Single(restored.Partial!.Directories).AccountIdentity);
    }

    [Fact]
    public void Disabled_unknown_or_reconfigured_sources_are_removed_but_destinations_remain_participants()
    {
        var current = Request("tv", "disabled") with
        {
            ServerIds = ["disabled", "destination"],
            SourceHints = [new("missing", "tv"), new("destination", "tv")]
        };
        var disabled = Server("disabled", enabled: false);
        var destination = Server("destination", destinationOnly: true);
        var merged = RaceAnnounceMerge.Merge(current, Request("tv", "destination"),
            id => id == "disabled" ? disabled : id == "destination" ? destination : null, true);
        Assert.Empty(merged.SourceHints);
        Assert.Null(merged.KnownSourceServerId);
        Assert.Null(merged.KnownSourcePath);
        Assert.Equal(["disabled", "destination"], merged.ServerIds);
    }

    [Fact]
    public void Actual_section_mapper_trigger_resolves_source_alias()
    {
        var source = Server("source", sourceOnly: true);
        source.SpreadSite.SectionMappings =
        [
            new() { IrcSection = "tv-hd", RemoteSection = "UHD", Path = "/incoming/uhd", TriggerRegex = @"\.2160p\." },
            new() { IrcSection = "tv-hd", RemoteSection = "HD", Path = "/incoming/hd", TriggerRegex = @"\.1080p\." }
        ];
        Assert.Equal("/incoming/hd/" + Release,
            RaceAnnounceMerge.ResolveSourcePath(source, new("source", "TV-HD"), Release));
    }

    [Fact]
    public void Later_mapped_alias_repairs_same_server_hint_without_an_explicit_path()
    {
        var source = Server("source", sourceOnly: true);
        source.SpreadSite.Sections = new() { ["tv-hd"] = "/incoming/tv" };
        var current = Request("-TV-", "source");
        var incoming = Request("tv-hd", "source");
        var merged = RaceAnnounceMerge.Merge(current, incoming, _ => source, true);
        var hint = Assert.Single(merged.SourceHints);
        Assert.Equal("tv-hd", hint.Section);
        Assert.Equal("tv-hd", merged.Section);
        Assert.Equal("/incoming/tv/" + Release, RaceAnnounceMerge.ResolveSourcePath(source, hint, Release));
        Assert.Same(merged, RaceAnnounceMerge.Merge(merged, incoming, _ => source, true));
    }

    [Fact]
    public void Filtered_out_known_source_is_not_readmitted_by_announce_hint()
    {
        var source = Server("source", sourceOnly: true);
        var destination = Server("destination", destinationOnly: true);
        var current = Request("tv", "destination");
        // Admission removed the announcing source; its old hint cannot undo that filter.
        var incoming = Request("tv-hd", "source", "/incoming/" + Release) with { ServerIds = ["destination"] };
        var merged = RaceAnnounceMerge.Merge(current, incoming, id => id == "source" ? source : destination, true);
        Assert.Equal(["destination"], merged.ServerIds);
        Assert.Null(merged.KnownSourceServerId);
        Assert.Empty(merged.SourceHints);
    }

    [Theory]
    [InlineData("mp3")]
    [InlineData("0day")]
    public void Dated_roots_require_an_explicit_discovered_path(string section)
    {
        var source = Server("source");
        source.SpreadSite.Sections = new() { [section] = "/incoming/" + section };
        Assert.Null(RaceAnnounceMerge.ResolveSourcePath(source, new("source", section), Release));
        var explicitPath = $"/incoming/{section}/1028/{Release}";
        Assert.Equal(explicitPath,
            RaceAnnounceMerge.ResolveSourcePath(source, new("source", section, explicitPath), Release));
    }

    [Fact]
    public void Mapping_without_path_resolves_remote_section_dictionary_case_insensitively()
    {
        var source = Server("source");
        source.SpreadSite.SectionMappings = [new() { IrcSection = "irc-tv", RemoteSection = "remote-tv", Path = "" }];
        source.SpreadSite.Sections = new() { ["REMOTE-TV"] = "/incoming/tv/" };
        Assert.Equal("/incoming/tv/" + Release,
            RaceAnnounceMerge.ResolveSourcePath(source, new("source", "irc-tv"), Release));
        Assert.Equal("/incoming/tv/" + Release,
            RaceAnnounceMerge.ResolveSourcePath(source, new("source", "remote-tv"), Release));
    }

    [Theory]
    [InlineData("relative/")]
    [InlineData("/incoming/../")]
    [InlineData("/incoming/./")]
    [InlineData("/incoming\\")]
    [InlineData("/incoming\r\n/")]
    public void Unsafe_explicit_paths_do_not_override_mapping(string prefix)
    {
        var source = Server("source");
        source.SpreadSite.Sections = new() { ["tv"] = "/safe/tv" };
        Assert.Equal("/safe/tv/" + Release,
            RaceAnnounceMerge.ResolveSourcePath(source, new("source", "tv", prefix + Release), Release));
        source.SpreadSite.Sections.Clear();
        Assert.Null(RaceAnnounceMerge.ResolveSourcePath(source, new("source", "tv", prefix + Release), Release));
    }

    [Fact]
    public void Safe_explicit_path_wins_and_wrong_release_suffix_is_rejected()
    {
        var source = Server("source");
        Assert.Equal("/archive/" + Release,
            RaceAnnounceMerge.ResolveSourcePath(source, new("source", "tv", "/archive/" + Release), Release));
        Assert.Null(RaceAnnounceMerge.ResolveSourcePath(source, new("source", "tv", "/archive/Other.Release-GRP"), Release));
        source.SpreadSite.NeverSource = true;
        Assert.Null(RaceAnnounceMerge.ResolveSourcePath(source, new("source", "tv", "/archive/" + Release), Release));
    }

    [Theory]
    [InlineData("../Release")]
    [InlineData("nested/Release")]
    [InlineData("nested\\Release")]
    [InlineData(".hidden")]
    [InlineData("Release\r\nSITE X")]
    public void Release_identity_must_be_one_safe_directory_name(string release)
    {
        var source = Server("source");
        source.SpreadSite.Sections = new() { ["tv"] = "/safe/tv" };
        Assert.Null(RaceAnnounceMerge.ResolveSourcePath(source, new("source", "tv"), release));
    }
}
