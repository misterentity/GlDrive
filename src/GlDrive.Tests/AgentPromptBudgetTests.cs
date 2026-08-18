using System.Text.Json;
using GlDrive.AiAgent;
using System.Text.Json.Nodes;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Regression cover for the AI self-tuning loop being bricked by ONE pathological telemetry row.
///
/// 2026-08-14 15:18 a race was recorded with release "x" and a section of exactly 2,000,000 'A'
/// characters. SectionActivityRollup copied that section verbatim into section-activity, the
/// digester emitted it verbatim into the digest, and AgentPrompt serialized the digest straight
/// into the user prompt. Every run from 2026-08-14 onward sent ~1,518,600 tokens of text input
/// and was refused by every model with HTTP 400 "maximum context length ... you requested about
/// 1550627 tokens". 40+ consecutive failed runs, ~24/day, each burning three API calls.
///
/// The tell that this was structural and not jitter: the requested token count was invariant to
/// within a handful of tokens across every run for days — a fixed blob, not live telemetry.
///
/// Two independent guards, because the poison is ALREADY on disk and stays inside the 7-day
/// window until it ages out: clamping at the prompt boundary is what actually heals existing
/// data, and the recorder guard is what stops the next one being written at all.
/// </summary>
public sealed class AgentPromptBudgetTests
{
    private static DigestBundle PoisonedDigest(int sectionLength)
    {
        var d = new DigestBundle { WindowStart = "2026-08-10", WindowEnd = "2026-08-17" };
        d.SectionActivity.PerServerSection.Add(new SectionActivityDigest.Row
        {
            ServerId = "bb90928a",
            Section = new string('A', sectionLength),
            FilesIn = 0,
            OurRaces = 1,
            OurWinRate = 0
        });
        return d;
    }

    private static string Compose(DigestBundle digest, string memo = "memo") =>
        new AgentPrompt().Compose(digest, memo, [], JsonNode.Parse("""{"a":1}""")!, []);

    [Fact]
    public void Prompt_StaysWithinBudget_WhenOneTelemetryFieldIsTwoMillionChars()
    {
        // The exact shape of the 2026-08-14 poison row.
        var prompt = Compose(PoisonedDigest(2_000_000));

        Assert.True(prompt.Length <= AgentPrompt.MaxPromptChars,
            $"prompt was {prompt.Length} chars, budget is {AgentPrompt.MaxPromptChars}");
    }

    [Fact]
    public void OversizedFields_AreClamped_NotPassedThrough()
    {
        var prompt = Compose(PoisonedDigest(2_000_000));

        // The single longest run of 'A' left in the prompt must be bounded by the field clamp.
        var longestRun = 0;
        var run = 0;
        foreach (var c in prompt)
        {
            if (c == 'A') { run++; if (run > longestRun) longestRun = run; }
            else run = 0;
        }

        Assert.True(longestRun <= AgentPrompt.MaxFieldChars,
            $"a {longestRun}-char field survived clamping (max {AgentPrompt.MaxFieldChars})");
    }

    [Fact]
    public void ClampedFields_AreMarked_SoTheModelKnowsItIsTruncated()
    {
        // Silent truncation would let the agent reason over a mangled section name as if it were real.
        Assert.Contains(AgentPrompt.TruncationMarker, Compose(PoisonedDigest(2_000_000)));
    }

    [Fact]
    public void NormalTelemetry_IsPassedThroughVerbatim()
    {
        // The clamp must not disturb real data — this is the regression that matters most.
        var d = new DigestBundle { WindowStart = "2026-08-10", WindowEnd = "2026-08-17" };
        d.SectionActivity.PerServerSection.Add(new SectionActivityDigest.Row
        {
            ServerId = "bb90928a", Section = "tv-hd", FilesIn = 1565, OurRaces = 135, OurWinRate = 0
        });

        var prompt = Compose(d);

        Assert.Contains("tv-hd", prompt);
        Assert.Contains("bb90928a", prompt);
        Assert.DoesNotContain(AgentPrompt.TruncationMarker, prompt);
    }

    [Fact]
    public void Budget_IsEnforced_EvenWhenBulkIsSpreadAcrossManyLegalSizedFields()
    {
        // Many individually-legal fields can still blow the budget in aggregate. Clamping
        // per-field alone is not sufficient; there must be a whole-prompt backstop.
        var d = new DigestBundle { WindowStart = "2026-08-10", WindowEnd = "2026-08-17" };
        for (var i = 0; i < 20_000; i++)
        {
            d.SectionActivity.PerServerSection.Add(new SectionActivityDigest.Row
            {
                ServerId = $"srv-{i}", Section = new string('B', 400), FilesIn = i
            });
        }

        var prompt = Compose(d);

        Assert.True(prompt.Length <= AgentPrompt.MaxPromptChars,
            $"prompt was {prompt.Length} chars, budget is {AgentPrompt.MaxPromptChars}");
    }

    [Fact]
    public void Budget_FitsTheSmallestModelInTheFallbackChain()
    {
        // openai/gpt-oss-120b tops out at 131,072 tokens and we ask for 32,000 of output,
        // leaving ~99k for input. At the conservative ~3.5 chars/token that JSON telemetry
        // actually achieves, the budget must not exceed that.
        const int smallestModelContext = 131_072;
        var inputTokenCeiling = smallestModelContext - AgentClient.MaxOutputTokens;

        Assert.True(AgentPrompt.MaxPromptChars / 3.5 <= inputTokenCeiling,
            $"{AgentPrompt.MaxPromptChars} chars can exceed the {inputTokenCeiling}-token input ceiling");
    }

    /// <summary>
    /// The first cut of the budget backstop string-truncated the serialized digest, which brought
    /// the prompt under budget but handed the model an unparseable JSON document — telemetry that
    /// reads as corrupt rather than as sampled. Trimming must be structural.
    /// </summary>
    [Fact]
    public void OverBudgetDigest_IsTrimmedStructurally_NotCutMidJson()
    {
        var d = new DigestBundle { WindowStart = "2026-08-10", WindowEnd = "2026-08-17" };
        for (var i = 0; i < 20_000; i++)
        {
            d.SectionActivity.PerServerSection.Add(new SectionActivityDigest.Row
            {
                ServerId = $"srv-{i}", Section = new string('B', 400), FilesIn = i
            });
        }

        var prompt = Compose(d);
        var digestBlob = prompt
            .Split("=== TELEMETRY DIGEST (N-day compact) ===")[1]
            .Split("=== CURRENT CONFIG")[0]
            .Trim();

        var ex = Record.Exception(() => { using var _ = JsonDocument.Parse(digestBlob); });
        Assert.Null(ex);
    }

    [Fact]
    public void OverBudgetConfig_StaysParseable()
    {
        // Every proposed change carries a JSON Pointer into the config and the Applier
        // cross-checks `before` against it, so a mangled config yields unapplyable changes.
        var big = new JsonObject();
        for (var i = 0; i < 5_000; i++) big[$"key-{i}"] = new string('C', 100);

        var prompt = new AgentPrompt().Compose(
            new DigestBundle { WindowStart = "a", WindowEnd = "b" }, "memo", [], big, []);

        var configBlob = prompt
            .Split("frozen paths masked as ***FROZEN***) ===")[1]
            .Split("Emit STRICT JSON")[0]
            .Trim();

        var ex = Record.Exception(() => { using var _ = JsonDocument.Parse(configBlob); });
        Assert.Null(ex);
    }

    [Fact]
    public void Clamp_IsIdempotent()
    {
        var once = AgentPrompt.ClampJsonStrings(JsonSerializer.Serialize(PoisonedDigest(2_000_000)));
        var twice = AgentPrompt.ClampJsonStrings(once);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void Clamp_PreservesJsonValidity()
    {
        var clamped = AgentPrompt.ClampJsonStrings(JsonSerializer.Serialize(PoisonedDigest(2_000_000)));
        var ex = Record.Exception(() => { using var _ = JsonDocument.Parse(clamped); });
        Assert.Null(ex);
    }
}
