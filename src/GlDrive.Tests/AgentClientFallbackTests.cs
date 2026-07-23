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

    // --- Retired-slug self-heal (2026-07-22) -------------------------------------------
    // The 404 body names the successor slug; ignoring it sent every run to the paid
    // fallback, which then 402'd, so the loop stayed dead.

    private const string Retired404 =
        """{"error":{"message":"This model is unavailable for free. The paid version is available now - use this slug instead: openai/gpt-oss-120b","code":404},"user_id":"user_x"}""";

    [Fact]
    public void SuggestedModel_IsParsedFrom404Body() =>
        Assert.Equal("openai/gpt-oss-120b", AgentClient.TryParseSuggestedModel(Retired404));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("""{"error":{"message":"No endpoints found for openai/gpt-oss-120b:free","code":404}}""")]
    public void SuggestedModel_IsNullWhenBodyNamesNoReplacement(string? body) =>
        Assert.Null(AgentClient.TryParseSuggestedModel(body));

    // --- Credit-aware max_tokens downshift (2026-07-22) ---------------------------------
    // 402 did not mean "broke": the balance covered ~27k tokens while every request asked
    // for a flat 32k, so OpenRouter refused it up front.

    private const string Afford402 =
        """{"error":{"message":"This request requires more credits, or fewer max_tokens. You requested up to 32000 tokens, but can only afford 27229. To increase, visit https://openrouter.ai/settings/credits","code":402}}""";

    [Fact]
    public void AffordableTokens_AreParsedFrom402Body() =>
        Assert.Equal(27229, AgentClient.TryParseAffordableTokens(Afford402));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("""{"error":{"message":"Insufficient credits","code":402}}""")]
    public void AffordableTokens_AreNullWhenBodyStatesNoBudget(string? body) =>
        Assert.Null(AgentClient.TryParseAffordableTokens(body));

    [Fact]
    public void CapTokensToBudget_LeavesHeadroomForBalanceDrift()
    {
        var capped = AgentClient.CapTokensToBudget(27229);
        Assert.NotNull(capped);
        Assert.True(capped < 27229, "must ask for less than the quoted ceiling");
        Assert.True(capped >= AgentClient.MinUsefulOutputTokens);
        // The quote moves between calls (24753 was quoted moments before 27229), so the
        // headroom has to absorb a real drift, not a token or two.
        Assert.True(27229 - capped >= 500);
    }

    [Theory]
    [InlineData(null)]        // no budget stated
    [InlineData(0)]
    [InlineData(1000)]        // too small to produce a usable change set
    [InlineData(4200)]        // 10% headroom drops it under the useful floor
    public void CapTokensToBudget_RefusesPointlessRetries(int? affordable) =>
        Assert.Null(AgentClient.CapTokensToBudget(affordable));

    [Fact]
    public void Http402_DoesNotBurnAPaidFallbackCall()
    {
        // Falling back to a *paid* model because we're out of credits is guaranteed waste.
        Assert.False(AgentClient.ShouldFallback("HTTP 402", Free, Paid));
        Assert.True(AgentClient.IsInsufficientCredit("HTTP 402"));
        Assert.False(AgentClient.IsInsufficientCredit("HTTP 404"));
    }
}
