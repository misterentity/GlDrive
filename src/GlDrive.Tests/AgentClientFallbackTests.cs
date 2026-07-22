using GlDrive.AiAgent;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Regression cover for the dead AI self-tuning loop: openai/gpt-oss-120b:free was retired
/// from OpenRouter, so every scheduled run returned HTTP 404 and fell through the
/// 429/parse-failure fallback gate. 12+ consecutive failures over several days, all logged
/// at INF, so the agent silently applied nothing.
/// </summary>
public sealed class AgentClientFallbackTests
{
    private const string Free = "openai/gpt-oss-120b:free";
    private const string Paid = "anthropic/claude-sonnet-4-6";

    [Fact]
    public void Http404_TriggersFallback_ForFreeModel() =>
        Assert.True(AgentClient.ShouldFallback("HTTP 404", Free, Paid));

    [Fact]
    public void Http404_TriggersFallback_ForPaidModelToo()
    {
        // A retired paid slug is just as dead — the ":free" gate must not apply to 404.
        Assert.True(AgentClient.ShouldFallback("HTTP 404", "some/retired-paid-model", Paid));
    }

    [Fact]
    public void Http404_DoesNotRecurse_WhenPrimaryIsAlreadyTheFallback() =>
        Assert.False(AgentClient.ShouldFallback("HTTP 404", Paid, Paid));

    [Theory]
    [InlineData("HTTP 429")]
    [InlineData("upstream:429 rate limited")]
    [InlineData("failed-to-parse-json")]
    public void FreeTierSymptoms_TriggerFallback_ForFreeModel(string error) =>
        Assert.True(AgentClient.ShouldFallback(error, Free, Paid));

    [Theory]
    [InlineData("HTTP 429")]
    [InlineData("failed-to-parse-json")]
    public void FreeTierSymptoms_DoNotBurnPaidCalls_ForNonFreeModel(string error) =>
        Assert.False(AgentClient.ShouldFallback(error, "some/paid-model", Paid));

    [Theory]
    [InlineData(null)]
    [InlineData("HTTP 500")]
    [InlineData("HTTP 401")]
    public void OtherOutcomes_DoNotTriggerFallback(string? error) =>
        Assert.False(AgentClient.ShouldFallback(error, Free, Paid));

    [Fact]
    public void Reason_NamesTheActualFailure()
    {
        Assert.Contains("404", AgentClient.DescribeFallbackReason("HTTP 404"));
        Assert.Contains("429", AgentClient.DescribeFallbackReason("HTTP 429"));
        Assert.Contains("JSON", AgentClient.DescribeFallbackReason("failed-to-parse-json"));
    }
}
