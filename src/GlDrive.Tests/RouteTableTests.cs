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

    [Fact]
    public void Matching_is_case_insensitive_on_method_and_literal_segments()
    {
        var t = new RouteTable();
        t.Map("GET", "/Status", Noop);

        Assert.True(t.TryMatch("get", "/status", out _, out _));
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
