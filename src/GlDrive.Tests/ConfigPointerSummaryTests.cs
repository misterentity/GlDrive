using GlDrive.Services.Operations;
using Xunit;

namespace GlDrive.Tests;

public sealed class ConfigPointerSummaryTests
{
    [Fact]
    public void Change_set_keeps_pointer_identity_and_has_no_value_input()
    {
        var summary = new ConfigPointerSummary(); summary.Add("/logging/level"); summary.Add("/servers/by-id/source/spreadSite/downloadOnly");
        Assert.False(summary.Partial); Assert.Contains("/logging/level", summary.Detail); Assert.Contains("/servers/by-id/source", summary.Detail);
    }

    [Fact]
    public void Oversized_and_excess_pointers_are_omitted_whole_with_explicit_coverage()
    {
        var summary = new ConfigPointerSummary(); summary.Add(new string('x', 513));
        for (var i = 0; i < 200; i++) summary.Add("/fixture/" + i);
        Assert.True(summary.Partial); Assert.DoesNotContain(new string('x', 20), summary.Detail); Assert.DoesNotContain("/fixture/32", summary.Detail);
        Assert.Contains("partial", summary.Detail); Assert.InRange(summary.Detail.Length, 0, 4096);
    }

    [Fact]
    public void Total_text_budget_is_bounded_independently_of_pointer_count()
    {
        var summary = new ConfigPointerSummary();
        for (var i = 0; i < 32; i++) summary.Add("/fixture/" + new string('x', 490) + i);
        Assert.True(summary.Partial); Assert.InRange(summary.Detail.Length, 0, 4096);
    }
}
