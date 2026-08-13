using System.Collections.Generic;
using System.Collections.Specialized;
using GlDrive.Services.Control;
using Xunit;

namespace GlDrive.Tests;

public class ControlRequestTests
{
    private static ControlRequest Make(
        IReadOnlyDictionary<string, string>? parameters = null,
        NameValueCollection? query = null)
        => ControlRequest.ForTesting(
            parameters ?? new Dictionary<string, string>(),
            query ?? new NameValueCollection());

    [Fact]
    public void Param_returns_a_captured_segment()
    {
        var r = Make(new Dictionary<string, string> { ["serverId"] = "zephyr" });
        Assert.Equal("zephyr", r.Param("serverId"));
    }

    [Fact]
    public void Param_returns_null_when_absent()
        => Assert.Null(Make().Param("nope"));

    [Fact]
    public void QueryInt_clamps_into_range()
    {
        var q = new NameValueCollection { { "limit", "9999" } };
        Assert.Equal(500, Make(query: q).QueryInt("limit", fallback: 25, min: 1, max: 500));
    }

    [Fact]
    public void QueryInt_falls_back_when_missing_or_unparseable()
    {
        Assert.Equal(25, Make().QueryInt("limit", 25, 1, 500));

        var q = new NameValueCollection { { "limit", "banana" } };
        Assert.Equal(25, Make(query: q).QueryInt("limit", 25, 1, 500));
    }

    [Fact]
    public void QueryInt_clamps_a_negative_up_to_min()
    {
        var q = new NameValueCollection { { "since", "-5" } };
        Assert.Equal(0, Make(query: q).QueryInt("since", 0, 0, int.MaxValue));
    }
}
