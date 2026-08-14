using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using GlDrive.Services.Control;
using Xunit;

namespace GlDrive.Tests;

public class RouteTableTests
{
    private static Func<ControlRequest, Task> Noop => _ => Task.CompletedTask;

    [Fact]
    public void Matches_a_literal_route()
    {
        var t = new RouteTable();
        t.Map("GET", "/status", Noop);

        Assert.True(t.TryMatch("GET", "/status", out var handler, out var ps));
        Assert.NotNull(handler);
        Assert.Empty(ps);
    }

    [Fact]
    public void Captures_a_parameter_segment()
    {
        var t = new RouteTable();
        t.Map("GET", "/irc/{serverId}", Noop);

        Assert.True(t.TryMatch("GET", "/irc/zephyr", out _, out var ps));
        Assert.Equal("zephyr", ps["serverId"]);
    }

    [Fact]
    public void Captures_multiple_parameters()
    {
        var t = new RouteTable();
        t.Map("POST", "/downloads/{id}/{action}", Noop);

        Assert.True(t.TryMatch("POST", "/downloads/abc123/retry", out _, out var ps));
        Assert.Equal("abc123", ps["id"]);
        Assert.Equal("retry", ps["action"]);
    }

    [Fact]
    public void A_literal_route_wins_over_a_parameter_route()
    {
        var t = new RouteTable();
        t.Map("GET", "/races/{id}", _ => Task.FromResult("param") as Task ?? Task.CompletedTask);
        t.Map("GET", "/races/active", Noop);

        Assert.True(t.TryMatch("GET", "/races/active", out _, out var ps));
        Assert.Empty(ps);   // matched the literal, captured nothing
    }

    [Fact]
    public void Segment_count_must_match_exactly()
    {
        var t = new RouteTable();
        t.Map("GET", "/irc/{serverId}", Noop);

        Assert.False(t.TryMatch("GET", "/irc", out _, out _));
        Assert.False(t.TryMatch("GET", "/irc/zephyr/messages", out _, out _));
    }

    [Fact]
    public void Method_is_part_of_the_match()
    {
        var t = new RouteTable();
        t.Map("GET", "/races", Noop);

        Assert.False(t.TryMatch("POST", "/races", out _, out _));
        Assert.True(t.MethodNotAllowed("/races"));
        Assert.False(t.MethodNotAllowed("/nonexistent"));
    }

    /// <summary>
    /// The dispatch this router replaced was case-sensitive on the path (C# tuple-switch
    /// equality, StringComparison.Ordinal StartsWith) but folded the HTTP method via
    /// ToUpperInvariant. The router must match that split exactly, or a caller who typed
    /// "GET /STATUS" would reach a route that never used to exist — see
    /// A_case_variant_of_a_mutating_route_does_not_match for the concrete regression this
    /// pins.
    /// </summary>
    [Fact]
    public void Method_is_case_insensitive_but_the_path_is_not()
    {
        var t = new RouteTable();
        t.Map("GET", "/status", Noop);

        Assert.True(t.TryMatch("get", "/status", out _, out _));    // method folds
        Assert.False(t.TryMatch("GET", "/STATUS", out _, out _));   // path does not
        Assert.False(t.TryMatch("GET", "/Status", out _, out _));
    }

    /// <summary>
    /// Regression test: RouteTable.TryBind originally compared literal segments with
    /// OrdinalIgnoreCase, so "POST /RACES" reached SpreadEndpoints.StartRace and could start
    /// a real FXP transfer, even though the switch-based dispatch it replaced returned 404
    /// for anything but an exact-case "/races". Auth (loopback + token) was never affected —
    /// this was a surface-equivalence break, not an auth bypass.
    /// </summary>
    [Fact]
    public void A_case_variant_of_a_mutating_route_does_not_match()
    {
        var t = new RouteTable();
        t.Map("POST", "/races", Noop);

        Assert.False(t.TryMatch("POST", "/RACES", out _, out _));
        Assert.False(t.MethodNotAllowed("/RACES"));
    }

    [Fact]
    public void A_parameter_never_matches_an_empty_segment()
    {
        var t = new RouteTable();
        t.Map("GET", "/irc/{serverId}", Noop);

        Assert.False(t.TryMatch("GET", "/irc/", out _, out _));
    }

    [Fact]
    public void Routes_are_enumerable_for_the_index_endpoint()
    {
        var t = new RouteTable();
        t.Map("GET", "/status", Noop);
        t.Map("POST", "/races", Noop);

        Assert.Contains(("GET", "/status"), t.Routes);
        Assert.Contains(("POST", "/races"), t.Routes);
    }

    [Fact]
    public void Duplicate_registration_is_rejected_loudly()
    {
        var t = new RouteTable();
        t.Map("GET", "/status", Noop);

        Assert.Throws<InvalidOperationException>(() => t.Map("GET", "/status", Noop));
    }
}
