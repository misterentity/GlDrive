using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// v3.10.46 — "Release not found on any server" was asserted without evidence.
///
/// Same shape as the v3.10.45 pair: a decision made from information that does
/// not support it. Phase 1 of <see cref="SpreadJob.RunAsync"/> probed each
/// candidate section path inside a bare <c>catch { }</c>. A borrow timeout, a
/// pool cooldown or a dropped control channel therefore produced exactly the
/// same observable state as a site answering "no such directory": zero entries
/// in sourceServers. The job then reported the release ABSENT and
/// SpreadManager parked it on the 60-minute release-not-found TTL — so one
/// transient connection failure silently suppressed a perfectly good release
/// for an hour, and the skip logged at Debug where nobody would see it.
///
/// Evidence of absence requires at least one probe that actually came back.
/// </summary>
public class SourceProbeEvidenceTests
{
    /// <summary>A located release is Found no matter how noisy the rest of the sweep was.</summary>
    [Theory]
    [InlineData(1, 5, 0)]
    [InlineData(1, 0, 9)]   // found on the one path that answered; everything else errored
    [InlineData(2, 3, 3)]
    public void A_located_release_is_always_Found(int servers, int clean, int errored)
    {
        Assert.Equal(SourceProbeVerdict.Found,
            CandidatePredicates.ClassifySourceProbe(servers, clean, errored));
    }

    /// <summary>
    /// The ONLY thing that licenses "absent" is a probe that returned a negative
    /// answer. One clean negative is enough — that is a site actually saying no.
    /// </summary>
    [Theory]
    [InlineData(1, 0)]
    [InlineData(16, 0)]
    [InlineData(4, 12)]     // some paths errored, but four sites still answered "no"
    public void A_clean_negative_answer_licenses_Absent(int clean, int errored)
    {
        Assert.Equal(SourceProbeVerdict.Absent,
            CandidatePredicates.ClassifySourceProbe(0, clean, errored));
    }

    /// <summary>
    /// The regression: every probe threw, so nothing ever told us the release was
    /// missing. This must NOT be reported as absent.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(16)]
    [InlineData(543)]
    public void All_probes_failing_is_Inconclusive_not_Absent(int errored)
    {
        var verdict = CandidatePredicates.ClassifySourceProbe(0, 0, errored);
        Assert.Equal(SourceProbeVerdict.Inconclusive, verdict);
        Assert.NotEqual(SourceProbeVerdict.Absent, verdict);
    }

    /// <summary>
    /// Nothing was probed at all (no sections configured, or the pool was missing
    /// for every server). We still have no evidence, so we may not claim absence.
    /// </summary>
    [Fact]
    public void Probing_nothing_is_Inconclusive()
    {
        Assert.Equal(SourceProbeVerdict.Inconclusive,
            CandidatePredicates.ClassifySourceProbe(0, 0, 0));
    }

    /// <summary>
    /// The consequence that made this expensive: an inconclusive sweep must not
    /// inherit the 60-minute release-not-found park. It gets the short
    /// source-scan-failed TTL so the next announce re-probes promptly.
    /// </summary>
    [Fact]
    public void Inconclusive_probe_is_not_parked_for_an_hour()
    {
        var absent = SpreadManager.ClassifyDeadRace(
            "Release not found on any server — check release name and section paths");
        var inconclusive = SpreadManager.ClassifyDeadRace(
            "Source probe inconclusive — 0 of 16 path probes returned an answer (connection failures)");

        Assert.NotNull(absent);
        Assert.NotNull(inconclusive);
        Assert.Equal("release-not-found", absent!.Value.reason);
        Assert.Equal("source-probe-inconclusive", inconclusive!.Value.reason);

        // The whole point: the unproven verdict must expire far sooner.
        Assert.True(inconclusive.Value.ttl < absent.Value.ttl);
        Assert.True(inconclusive.Value.ttl <= System.TimeSpan.FromMinutes(5));
    }
}
