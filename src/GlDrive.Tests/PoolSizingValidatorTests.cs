using GlDrive.AiAgent;
using GlDrive.Config;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Locks the PoolSizingValidator login-cap clamp. The AI agent ratcheted
/// maxConcurrentRaces up to 5 against a loginCap-4 source on 2026-06-17, which
/// oversubscribed the source's shared login budget and exhausted both the main
/// and spread pools (971 "main pool exhausted" warnings + scan failures + FXP
/// borrow timeouts). maxConcurrentRaces must stay ≤ (LoginCap − LoginHeadroom − 1).
/// </summary>
public class PoolSizingValidatorTests
{
    private static AppConfig ConfigWith(int maxConcurrent, int loginCap = 4, int headroom = 1)
    {
        var cfg = new AppConfig();
        cfg.Spread.MaxConcurrentRaces = maxConcurrent;
        cfg.Servers.Add(new ServerConfig
        {
            Id = "srv1",
            Name = "Site1",
            Pool = new PoolConfig { LoginCap = loginCap, LoginHeadroom = headroom },
        });
        return cfg;
    }

    private static AgentChange MaxRacesChange(int after) => new()
    {
        Category = AgentCategories.PoolSizing,
        Target = "/spread/maxConcurrentRaces",
        After = after,
        Confidence = 0.9,
    };

    [Fact]
    public void MaxConcurrentRaces_clamped_to_login_budget()
    {
        // loginCap 4, headroom 1 → usable 3 → ceiling 2. Agent proposes 5.
        var cfg = ConfigWith(maxConcurrent: 4);
        var result = new PoolSizingValidator().Validate(MaxRacesChange(5), cfg);

        Assert.True(result.Ok);
        Assert.NotNull(result.Mutate);
        result.Mutate!(cfg);
        Assert.Equal(2, cfg.Spread.MaxConcurrentRaces);
    }

    [Fact]
    public void MaxConcurrentRaces_within_budget_is_unchanged()
    {
        // ceiling is 2; proposing 2 must pass through untouched.
        var cfg = ConfigWith(maxConcurrent: 2);
        var result = new PoolSizingValidator().Validate(MaxRacesChange(2), cfg);

        Assert.True(result.Ok);
        result.Mutate!(cfg);
        Assert.Equal(2, cfg.Spread.MaxConcurrentRaces);
    }

    [Fact]
    public void MaxConcurrentRaces_ceiling_follows_smallest_login_cap()
    {
        // Two servers; the smaller cap (loginCap 3 → usable 2 → ceiling 1) wins.
        var cfg = ConfigWith(maxConcurrent: 2, loginCap: 4);
        cfg.Servers.Add(new ServerConfig
        {
            Id = "srv2",
            Name = "Site2",
            Pool = new PoolConfig { LoginCap = 3, LoginHeadroom = 1 },
        });

        var result = new PoolSizingValidator().Validate(MaxRacesChange(2), cfg);

        Assert.True(result.Ok);
        result.Mutate!(cfg);
        Assert.Equal(1, cfg.Spread.MaxConcurrentRaces);
    }
}
