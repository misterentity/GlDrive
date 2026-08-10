using System;
using System.IO;
using GlDrive.Config;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// The control API is the app's only remote-ish attack surface, so its three guarantees
/// are pinned here rather than left to review: loopback-only binding, a required bearer
/// token compared in fixed time, and off-by-default.
/// </summary>
public class ControlApiSecurityTests
{
    private static string ReadControlApiSource()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12 && dir != null; i++)
        {
            var candidate = Path.Combine(dir, "src", "GlDrive", "Services", "ControlApi.cs");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException("Could not locate src/GlDrive/Services/ControlApi.cs");
    }

    [Fact]
    public void Disabled_by_default()
    {
        var cfg = new AppConfig();
        Assert.False(cfg.ControlApi.Enabled);
        Assert.Equal("", cfg.ControlApi.Token);
    }

    [Fact]
    public void Token_is_generated_only_once_and_only_when_enabled()
    {
        var cfg = new AppConfig();

        // disabled -> never mints a token
        Assert.False(ControlApi_EnsureToken(cfg));
        Assert.Equal("", cfg.ControlApi.Token);

        cfg.ControlApi.Enabled = true;
        Assert.True(ControlApi_EnsureToken(cfg));
        var first = cfg.ControlApi.Token;
        Assert.Equal(64, first.Length);                 // 32 random bytes, hex
        Assert.Matches("^[0-9a-f]+$", first);

        // idempotent — an existing token is never rotated out from under a caller
        Assert.False(ControlApi_EnsureToken(cfg));
        Assert.Equal(first, cfg.ControlApi.Token);
    }

    private static bool ControlApi_EnsureToken(AppConfig cfg)
        => GlDrive.Services.ControlApi.EnsureToken(cfg);

    [Fact]
    public void Listener_binds_loopback_only()
    {
        var src = ReadControlApiSource();
        Assert.Contains("http://127.0.0.1:", src);
        // A wildcard prefix would expose the control surface to the LAN.
        Assert.DoesNotContain("http://+:", src);
        Assert.DoesNotContain("http://*:", src);
        Assert.DoesNotContain("http://0.0.0.0", src);
    }

    [Fact]
    public void Every_request_is_token_checked_and_loopback_checked()
    {
        var src = ReadControlApiSource();

        Assert.Contains("IPAddress.IsLoopback", src);
        Assert.Contains("FixedTimeEquals", src);

        // The remote-address and token gates must both precede any routing decision,
        // otherwise an endpoint could be reached unauthenticated.
        var loopbackGate = src.IndexOf("IPAddress.IsLoopback", StringComparison.Ordinal);
        var tokenGate    = src.IndexOf("FixedTimeEquals(presented", StringComparison.Ordinal);
        var routing      = src.IndexOf("switch (method, path)", StringComparison.Ordinal);
        Assert.True(loopbackGate > 0 && tokenGate > loopbackGate,
            "loopback check must come before the token check");
        Assert.True(routing > tokenGate, "routing must come after both gates");
    }

    [Fact]
    public void Refuses_to_listen_when_enabled_without_a_token()
    {
        var src = ReadControlApiSource();
        Assert.Contains("refusing to listen", src);
    }

    [Fact]
    public void Token_is_never_logged()
    {
        var src = ReadControlApiSource();
        foreach (var line in src.Split('\n'))
        {
            if (!line.Contains("Log.", StringComparison.Ordinal)) continue;
            Assert.DoesNotContain("Token", line);
            Assert.DoesNotContain("token}", line);
        }
    }
}
