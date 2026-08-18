using GlDrive.AiAgent;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Regression cover for the AI loop's failure being reported as the WRONG cause for 40+ runs.
///
/// The retry chain is primary → healed slug → paid fallback. The primary 404s (retired ":free"
/// slug), the healed and fallback attempts then both fail with HTTP 400 "maximum context length".
/// RunAsync returned the FIRST outcome, so AgentRunner logged `status=HTTP 404` and told the user
/// to "Check the OpenRouter model slug and credit balance" — two things that were both fine. The
/// actual blocker was a 1.5M-token prompt.
///
/// Same shape as the v3.10.48 borrow-timeout misattribution and the v3.10.56 shared-state
/// misattribution: the message was fine, the STATE it read was the wrong one.
/// </summary>
public sealed class AgentFailureAttributionTests
{
    private const string Free = "openai/gpt-oss-120b:free";
    private const string Paid = "anthropic/claude-sonnet-4-6";

    private const string ContextLength400 =
        """{"error":{"message":"This endpoint's maximum context length is 131072 tokens. However, you requested about 1550627 tokens (1518627 of text input, 32000 in the output). Please reduce the length of either one, or use the context-compression plugin to compress your prompt automatically.","code":400}}""";

    private const string Retired404 =
        """{"error":{"message":"This model is unavailable for free. The paid version is available now - use this slug instead: openai/gpt-oss-120b","code":404}}""";

    private static AgentRunOutcome Fail(string msg, string? body = null) =>
        new() { ErrorMessage = msg, ErrorBody = body };

    [Fact]
    public void ContextLengthOverflow_IsRecognised()
    {
        Assert.True(AgentClient.IsContextLengthExceeded("HTTP 400", ContextLength400));
    }

    [Theory]
    [InlineData("HTTP 400", """{"error":{"message":"Invalid model name","code":400}}""")]
    [InlineData("HTTP 404", Retired404)]
    [InlineData("HTTP 429", null)]
    [InlineData(null, null)]
    public void OtherFailures_AreNotMistakenForContextOverflow(string? msg, string? body) =>
        Assert.False(AgentClient.IsContextLengthExceeded(msg, body));

    [Fact]
    public void ReportedFailure_IsTheContextOverflow_NotTheLeadingSlug404()
    {
        // The exact 2026-08-14..17 chain.
        var reported = AgentClient.ChooseReportedFailure(
            Fail("HTTP 404", Retired404),   // primary, retired slug
            Fail("HTTP 400", ContextLength400),   // healed slug
            Fail("HTTP 400", ContextLength400));  // paid fallback

        Assert.Equal("HTTP 400", reported.ErrorMessage);
        Assert.True(AgentClient.IsContextLengthExceeded(reported.ErrorMessage, reported.ErrorBody));
    }

    [Fact]
    public void ReportedFailure_KeepsThe404_WhenThatIsTheOnlyThingThatWentWrong()
    {
        // A genuine retired slug with nothing more specific behind it must still say 404,
        // otherwise this fix just swaps one misattribution for another.
        var reported = AgentClient.ChooseReportedFailure(Fail("HTTP 404", Retired404));
        Assert.Equal("HTTP 404", reported.ErrorMessage);
    }

    [Fact]
    public void ReportedFailure_PrefersTheTerminalAttempt_OverTheLeadingOne()
    {
        var reported = AgentClient.ChooseReportedFailure(
            Fail("HTTP 404", Retired404),
            Fail("HTTP 402", """{"error":{"message":"requires more credits","code":402}}"""));

        Assert.Equal("HTTP 402", reported.ErrorMessage);
    }

    [Fact]
    public void ReportedFailure_IgnoresSuccesses()
    {
        var ok = new AgentRunOutcome { Result = new AgentRunResult() };
        var reported = AgentClient.ChooseReportedFailure(Fail("HTTP 404", Retired404), ok);
        Assert.NotNull(reported.Result);
    }

    [Fact]
    public void OperatorGuidance_ForContextOverflow_DoesNotBlameSlugOrCredits()
    {
        // The ERR line sent Dave to check the model slug and credit balance for 40 runs.
        var advice = AgentClient.DescribeFailureForOperator("HTTP 400", ContextLength400);

        Assert.Contains("too large", advice, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credit", advice, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("slug", advice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OperatorGuidance_ForRetiredSlug_StillNamesTheSlug()
    {
        var advice = AgentClient.DescribeFailureForOperator("HTTP 404", Retired404);
        Assert.Contains("slug", advice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ContextOverflow_DoesNotBurnAPaidFallbackCall()
    {
        // Once the prompt is clamped this should not arise, but if it ever does, retrying the
        // identical oversized prompt on another model is guaranteed waste — the same lesson as
        // HTTP 402. The 2026-08-14 chain burned three calls per run, 24 times a day, for days.
        Assert.False(AgentClient.ShouldFallback("HTTP 400", Free, Paid));
    }
}
