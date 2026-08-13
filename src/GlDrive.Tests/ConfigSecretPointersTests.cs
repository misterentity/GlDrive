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
    public void ConfigManager_masks_secret_values_before_recording_them()
    {
        var src = ReadSource("src/GlDrive/Config/ConfigManager.cs");

        Assert.Contains("ConfigSecretPointers.IsSecret(ptr)", src, System.StringComparison.Ordinal);
        Assert.Contains("ConfigSecretPointers.Mask(b)", src, System.StringComparison.Ordinal);
        Assert.Contains("ConfigSecretPointers.Mask(a)", src, System.StringComparison.Ordinal);

        // The unguarded assignment must be gone, not merely shadowed.
        Assert.DoesNotContain("BeforeValue = b,", src, System.StringComparison.Ordinal);
        Assert.DoesNotContain("AfterValue  = a", src, System.StringComparison.Ordinal);
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
