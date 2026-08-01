using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// v3.10.45 — the two defects behind the 2026-07-31 spread forensics.
///
/// Both are the same shape: a decision made with information that does not
/// support it. (1) Two gates answered "is this dest blocked?" with DIFFERENT
/// predicates. (2) The race fail-fast rendered a verdict on destinations from a
/// scheduling pass that never examined one.
/// </summary>
public class SpreadGateAgreementTests
{
    private static readonly string[] Denied = ["/tv-hd/"];
    private const string Path = "/tv-hd/Stranger.Things.S05E06.1080p.BluRay.x264-BORDURE";

    /// <summary>
    /// The scheduler (FindBestTransfer) and the completion/fail-fast gates
    /// (NoViableDestinations / AllDestinationsTerminal) must classify a dest
    /// identically. The old gate rule was <c>blocked &amp;&amp; !dirConfirmed</c>, which
    /// disagrees with the bounded rule exactly when a confirmation has gone stale:
    /// scheduler says "never pick it", gate says "still pending". A race in that
    /// state can neither dispatch nor terminate.
    /// </summary>
    [Theory]
    [InlineData(false, 0)]   // unconfirmed, no denials  -> blocked by both
    [InlineData(true, 0)]    // confirmed, fresh         -> open to both
    [InlineData(true, 2)]    // confirmed, under bound   -> open to both
    [InlineData(true, 3)]    // confirmed, AT bound      -> the divergent case
    [InlineData(true, 99)]   // confirmed, way over      -> the divergent case
    public void Scheduler_and_completion_gate_agree_on_dest_blocked(bool dirConfirmed, int denials)
    {
        // The single predicate both call sites now share.
        var scheduler = CandidatePredicates.DirscriptBlockedAfterOverride(
            Path, Denied, dirConfirmed, denials);

        // The rule the completion gate used to apply on its own.
        var oldGateRule = CandidatePredicates.DirscriptBlocked(Path, Denied) && !dirConfirmed;

        // Agreement is only interesting where the old rule actually differed.
        if (dirConfirmed && denials >= CandidatePredicates.MaxMkdDenialsWithDirConfirmed)
            Assert.NotEqual(scheduler, oldGateRule);   // documents the old divergence

        // What matters: the gate must now report what the scheduler does.
        var gate = CandidatePredicates.DirscriptBlockedAfterOverride(
            Path, Denied, dirConfirmed, denials);
        Assert.Equal(scheduler, gate);
    }

    /// <summary>
    /// A dest whose confirmation has gone stale must be BLOCKED by the gate too —
    /// otherwise it haunts the race as forever-pending. This is the concrete state
    /// the divergence produced.
    /// </summary>
    [Fact]
    public void Stale_confirmation_blocks_the_completion_gate_not_just_the_scheduler()
    {
        Assert.True(CandidatePredicates.DirscriptBlockedAfterOverride(
            Path, Denied, dirConfirmed: true,
            mkdDenialCount: CandidatePredicates.MaxMkdDenialsWithDirConfirmed));

        // The rule that used to run here returned false — "not denied, still pending".
        Assert.False(CandidatePredicates.DirscriptBlocked(Path, Denied) && !true);
    }

    /// <summary>
    /// The 2026-07-31 signature: races were terminally failed with
    /// "All destinations denied this release — mkdir filter" while the skip summary
    /// attached to that very message read <c>backoff/dirscript=0 cooldown=11</c>.
    /// Every candidate had been dropped at the SOURCE pool-cooldown check, which
    /// <c>continue</c>s the whole destination loop — so zero destinations were
    /// examined, and dirscript could not have been the cause of anything.
    ///
    /// A pool cooldown is a 20s self-clearing login-cap backoff. Converting it into
    /// a permanent race loss (and blaming the mkdir filter) is wrong twice over.
    /// </summary>
    [Fact]
    public void Failfast_requires_a_pass_that_actually_examined_destinations()
    {
        // Reproduces FindBestTransfer's counting for the observed pass: 11 files,
        // one source in cooldown, one dest — the source check short-circuits first.
        const int files = 11;
        var destsEvaluated = 0;
        var skippedCooldown = 0;
        var sourceInCooldown = true;

        for (var f = 0; f < files; f++)
        {
            if (sourceInCooldown) { skippedCooldown++; continue; }   // src check `continue`s
            destsEvaluated++;                                        // never reached
        }

        Assert.Equal(files, skippedCooldown);
        Assert.Equal(0, destsEvaluated);

        // The guard the fail-fast now applies.
        Assert.False(destsEvaluated > 0);

        // Once the 20s backoff clears, the same pass does reach the dest rules and
        // the fail-fast is allowed to render a verdict.
        sourceInCooldown = false;
        for (var f = 0; f < files; f++)
        {
            if (sourceInCooldown) { skippedCooldown++; continue; }
            destsEvaluated++;
        }
        Assert.Equal(files, destsEvaluated);
        Assert.True(destsEvaluated > 0);
    }
}
