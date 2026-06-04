using GlDrive.AiAgent;
using GlDrive.Config;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Locks the SectionMappingValidator routability guard (section→folder learning feature).
/// Appended/patched mappings must point at a RemoteSection the site actually has, otherwise
/// SectionMapper.Resolve silently fails to route at race time. Also pins the unchanged
/// trigger-regex-compiles check and the default-trigger-only patch invariant.
/// </summary>
public class SectionMappingValidatorTests
{
    private const string ServerId = "srv1";

    private static AppConfig ConfigWithSection()
    {
        var cfg = new AppConfig();
        var server = new ServerConfig { Id = ServerId, Name = "Site1" };
        server.SpreadSite.Sections["X264-1080"] = "/site/x264-1080";
        cfg.Servers.Add(server);
        return cfg;
    }

    private static AgentChange AppendChange(SectionMapping after) => new()
    {
        Category = AgentCategories.SectionMapping,
        Target = $"/servers/{ServerId}/spread/sectionMappings/-",
        After = after,
        Confidence = 0.9,
    };

    private static AgentChange PatchChange(int index, SectionMapping after) => new()
    {
        Category = AgentCategories.SectionMapping,
        Target = $"/servers/{ServerId}/spread/sectionMappings/{index}",
        After = after,
        Confidence = 0.9,
    };

    [Fact]
    public void Append_with_known_remote_section_and_valid_trigger_is_ok()
    {
        var cfg = ConfigWithSection();
        var change = AppendChange(new SectionMapping
        {
            IrcSection = "TV-1080P",
            RemoteSection = "X264-1080",      // exists in Sections
            TriggerRegex = @"(?i).*\.1080p\..*",
        });

        var result = new SectionMappingValidator().Validate(change, cfg);

        Assert.True(result.Ok);
        Assert.Null(result.RejectionReason);
        Assert.NotNull(result.Mutate);

        // Mutation appends the mapping when applied.
        result.Mutate!(cfg);
        Assert.Single(cfg.Servers[0].SpreadSite.SectionMappings);
        Assert.Equal("X264-1080", cfg.Servers[0].SpreadSite.SectionMappings[0].RemoteSection);
    }

    [Fact]
    public void Append_with_unknown_remote_section_is_rejected()
    {
        var cfg = ConfigWithSection();
        var change = AppendChange(new SectionMapping
        {
            IrcSection = "TV-1080P",
            RemoteSection = "DOES-NOT-EXIST",
            TriggerRegex = @"(?i).*\.1080p\..*",
        });

        var result = new SectionMappingValidator().Validate(change, cfg);

        Assert.False(result.Ok);
        Assert.Equal("remote-section-unknown", result.RejectionReason);
    }

    [Fact]
    public void Append_with_empty_remote_section_is_rejected()
    {
        var cfg = ConfigWithSection();
        var change = AppendChange(new SectionMapping
        {
            IrcSection = "TV-1080P",
            RemoteSection = "",               // unroutable
            TriggerRegex = @"(?i).*\.1080p\..*",
        });

        var result = new SectionMappingValidator().Validate(change, cfg);

        Assert.False(result.Ok);
        Assert.Equal("remote-section-empty", result.RejectionReason);
    }

    [Fact]
    public void Append_with_bad_regex_is_rejected()
    {
        var cfg = ConfigWithSection();
        var change = AppendChange(new SectionMapping
        {
            IrcSection = "TV-1080P",
            RemoteSection = "X264-1080",
            TriggerRegex = "(unclosed",       // does not compile
        });

        var result = new SectionMappingValidator().Validate(change, cfg);

        Assert.False(result.Ok);
        Assert.Equal("trigger-bad-regex", result.RejectionReason);
    }

    [Fact]
    public void Append_remote_section_match_is_case_insensitive()
    {
        var cfg = ConfigWithSection();
        var change = AppendChange(new SectionMapping
        {
            IrcSection = "TV-1080P",
            RemoteSection = "x264-1080",      // lowercase variant of the configured key
            TriggerRegex = ".*",
        });

        var result = new SectionMappingValidator().Validate(change, cfg);

        Assert.True(result.Ok);
        Assert.Null(result.RejectionReason);
    }

    [Fact]
    public void Patch_of_default_trigger_row_still_works()
    {
        var cfg = ConfigWithSection();
        // Seed an existing row whose trigger is the default ".*" so the patch is allowed.
        cfg.Servers[0].SpreadSite.SectionMappings.Add(new SectionMapping
        {
            IrcSection = "TV-1080P",
            RemoteSection = "X264-1080",
            TriggerRegex = ".*",
        });

        var change = PatchChange(0, new SectionMapping
        {
            IrcSection = "TV-1080P",
            RemoteSection = "X264-1080",
            TriggerRegex = @"(?i).*\.1080p\..*",
        });

        var result = new SectionMappingValidator().Validate(change, cfg);

        Assert.True(result.Ok);
        Assert.NotNull(result.Mutate);

        result.Mutate!(cfg);
        Assert.Equal(@"(?i).*\.1080p\..*", cfg.Servers[0].SpreadSite.SectionMappings[0].TriggerRegex);
    }

    [Fact]
    public void Patch_does_not_overwrite_user_edited_trigger()
    {
        var cfg = ConfigWithSection();
        // Existing row already has a user-edited (non-default) trigger — must be preserved.
        cfg.Servers[0].SpreadSite.SectionMappings.Add(new SectionMapping
        {
            IrcSection = "TV-1080P",
            RemoteSection = "X264-1080",
            TriggerRegex = @"(?i).*\.user\..*",
        });

        var change = PatchChange(0, new SectionMapping
        {
            IrcSection = "TV-1080P",
            RemoteSection = "X264-1080",
            TriggerRegex = @"(?i).*\.ai\..*",
        });

        var result = new SectionMappingValidator().Validate(change, cfg);

        Assert.True(result.Ok);
        result.Mutate!(cfg);
        // Invariant: user-edited trigger preserved.
        Assert.Equal(@"(?i).*\.user\..*", cfg.Servers[0].SpreadSite.SectionMappings[0].TriggerRegex);
    }

    [Fact]
    public void Patch_with_unknown_remote_section_is_rejected()
    {
        var cfg = ConfigWithSection();
        cfg.Servers[0].SpreadSite.SectionMappings.Add(new SectionMapping
        {
            IrcSection = "TV-1080P",
            RemoteSection = "X264-1080",
            TriggerRegex = ".*",
        });

        var change = PatchChange(0, new SectionMapping
        {
            IrcSection = "TV-1080P",
            RemoteSection = "DOES-NOT-EXIST",
            TriggerRegex = ".*",
        });

        var result = new SectionMappingValidator().Validate(change, cfg);

        Assert.False(result.Ok);
        Assert.Equal("remote-section-unknown", result.RejectionReason);
    }
}
