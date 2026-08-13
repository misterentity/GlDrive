using System.Linq;
using System.Text.Json.Nodes;
using GlDrive.AiAgent;
using Xunit;

namespace GlDrive.Tests;

public class ConfigSecretPointersTests
{
    [Theory]
    [InlineData("/servers/0/irc/channels/0/key")]
    [InlineData("/servers/0/connection/password")]
    [InlineData("/controlApi/token")]
    [InlineData("/downloads/omdbApiKey")]
    [InlineData("/downloads/tmdbApiKey")]
    [InlineData("/agent/openRouterApiKey")]
    [InlineData("/servers/0/connection/proxy/password")]
    public void Secret_pointers_are_recognised(string pointer)
        => Assert.True(ConfigSecretPointers.IsSecret(pointer));

    [Theory]
    [InlineData("/servers/0/spreadSite/sections/x265")]
    [InlineData("/servers/0/pool/loginCap")]
    [InlineData("/spread/maxConcurrentRaces")]
    [InlineData("/logging/level")]
    [InlineData("/servers/0/irc/channels/0/name")]
    [InlineData("")]
    public void Ordinary_pointers_are_not_secret(string pointer)
        => Assert.False(ConfigSecretPointers.IsSecret(pointer));

    [Fact]
    public void Mask_is_stable_and_reveals_nothing()
    {
        var a = ConfigSecretPointers.Mask("hunter2");
        var b = ConfigSecretPointers.Mask("hunter2");

        Assert.Equal(a, b);                              // stable: change detection still works
        Assert.StartsWith("sha256:", a);
        Assert.DoesNotContain("hunter2", a!);
        Assert.NotEqual(a, ConfigSecretPointers.Mask("hunter3"));
    }

    [Fact]
    public void Mask_preserves_null_so_added_and_removed_stay_distinguishable()
        => Assert.Null(ConfigSecretPointers.Mask(null));

    [Fact]
    public void MaskValue_returns_null_for_null_value()
        => Assert.Null(ConfigSecretPointers.MaskValue("/servers/0/connection/password", null));

    [Fact]
    public void MaskValue_masks_the_whole_value_when_the_pointer_is_secret()
    {
        var masked = ConfigSecretPointers.MaskValue("/servers/0/connection/password", "\"hunter2\"");

        Assert.StartsWith("sha256:", masked);
        Assert.DoesNotContain("hunter2", masked!);
    }

    [Fact]
    public void MaskValue_passes_through_a_non_json_scalar_unchanged()
    {
        const string value = "not-json-{{{";
        Assert.Equal(value, ConfigSecretPointers.MaskValue("/logging/level", value));
    }

    [Fact]
    public void MaskValue_passes_through_an_ordinary_json_scalar_unchanged()
    {
        const string value = "\"debug\"";
        Assert.Equal(value, ConfigSecretPointers.MaskValue("/logging/level", value));
    }

    [Fact]
    public void MaskValue_masks_secret_leaves_inside_a_whole_object_value()
    {
        const string value = """{"name":"#staff","key":"supersecretkey","autoJoin":true}""";
        var masked = ConfigSecretPointers.MaskValue("/servers/0/irc/channels/0", value);

        Assert.NotNull(masked);
        Assert.DoesNotContain("supersecretkey", masked!);
        Assert.Contains("#staff", masked);
        Assert.Contains("sha256:", masked);
    }

    [Fact]
    public void MaskValue_masks_secret_leaves_inside_nested_array_elements()
    {
        const string value = """{"channels":[{"name":"#a","key":"key-one"},{"name":"#b","key":"key-two"}]}""";
        var masked = ConfigSecretPointers.MaskValue("/servers/0/irc", value);

        Assert.DoesNotContain("key-one", masked!);
        Assert.DoesNotContain("key-two", masked!);
        Assert.Contains("#a", masked);
        Assert.Contains("#b", masked);
    }

    [Fact]
    public void ConfigDiff_whole_channel_add_does_not_leak_the_key_once_masked()
    {
        // Regression for Finding 1: ConfigDiff emits a whole added/removed subtree as ONE
        // blob whose pointer's leaf is an array index ("0"), not a field name, so IsSecret
        // alone can't catch it. MaskValue must still find and mask the "key" field inside.
        var before = JsonNode.Parse("""{"servers":[{"irc":{"channels":[]}}]}""");
        var after  = JsonNode.Parse("""{"servers":[{"irc":{"channels":[{"name":"#staff","key":"supersecretkey","autoJoin":true}]}}]}""");

        var diffs = ConfigDiff.Diff(before, after).ToList();
        Assert.NotEmpty(diffs);

        var maskedValues = diffs
            .SelectMany(d => new[]
            {
                ConfigSecretPointers.MaskValue(d.pointer, d.before),
                ConfigSecretPointers.MaskValue(d.pointer, d.after)
            })
            .Where(v => v != null)
            .ToList();

        Assert.DoesNotContain(maskedValues, v => v!.Contains("supersecretkey"));
        Assert.Contains(maskedValues, v => v!.Contains("#staff"));
    }

    [Fact]
    public void ConfigManager_masks_secret_values_before_recording_them()
    {
        var src = ReadSource("src/GlDrive/Config/ConfigManager.cs");

        Assert.Contains("ConfigSecretPointers.MaskValue(ptr, b)", src, System.StringComparison.Ordinal);
        Assert.Contains("ConfigSecretPointers.MaskValue(ptr, a)", src, System.StringComparison.Ordinal);

        // The unguarded assignment must be gone, not merely shadowed.
        Assert.DoesNotContain("BeforeValue = b,", src, System.StringComparison.Ordinal);
        Assert.DoesNotContain("AfterValue  = a", src, System.StringComparison.Ordinal);
    }

    [Fact]
    public void AgentViewModel_masks_secret_values_before_recording_the_undo_event()
    {
        var src = ReadSource("src/GlDrive/UI/AgentViewModel.cs");

        Assert.Contains("ConfigSecretPointers.MaskValue(row.Target,", src, System.StringComparison.Ordinal);

        // The unguarded assignments must be gone, not merely shadowed.
        Assert.DoesNotContain("BeforeValue = row.After?.ToString(),", src, System.StringComparison.Ordinal);
        Assert.DoesNotContain("AfterValue = row.Before?.ToString(),", src, System.StringComparison.Ordinal);
    }

    private static string ReadSource(string relativePath)
    {
        var dir = System.AppContext.BaseDirectory;
        for (var i = 0; i < 12 && dir != null; i++)
        {
            var candidate = System.IO.Path.Combine(
                dir, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            if (System.IO.File.Exists(candidate)) return System.IO.File.ReadAllText(candidate);
            dir = System.IO.Directory.GetParent(dir)?.FullName;
        }
        throw new System.InvalidOperationException($"Could not locate {relativePath}");
    }
}
