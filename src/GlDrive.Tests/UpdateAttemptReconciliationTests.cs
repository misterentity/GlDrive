using GlDrive.Services;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// The post-install reconciler asks "did we get OFF the old version?", not "did we land on
/// exactly this build". An auto-install pulls whatever GitHub calls `latest`, so a release
/// published between recording the tag and running the installer makes us legitimately
/// OVERSHOOT the attempted version.
///
/// Production 2026-08-31 10:12:30: attempted v3.10.90, ended up running 3.10.92.0, and the
/// equality predicate scored that success as "failure 1/3". Three of those latch
/// IsUpdateBlocked against the tag permanently AND never clear the failure ledger, because
/// the ledger is only deleted on the success branch.
/// </summary>
public class UpdateAttemptReconciliationTests
{
    [Fact]
    public void ExactLanding_IsSatisfied()
    {
        Assert.True(UpdateChecker.AttemptSatisfiedBy(new Version(3, 10, 90), new Version(3, 10, 90, 0)));
    }

    [Fact]
    public void Overshoot_IsSatisfied_TheProductionRegression()
    {
        // The exact production pair that was misjudged.
        Assert.True(UpdateChecker.AttemptSatisfiedBy(new Version(3, 10, 90), new Version(3, 10, 92, 0)));
    }

    [Fact]
    public void StillOnOlderVersion_IsNotSatisfied()
    {
        // The case the reconciler actually exists to catch: the install did not take.
        Assert.False(UpdateChecker.AttemptSatisfiedBy(new Version(3, 10, 90), new Version(3, 10, 85, 0)));
    }

    [Fact]
    public void OvershootAcrossMinorAndMajor_IsSatisfied()
    {
        Assert.True(UpdateChecker.AttemptSatisfiedBy(new Version(3, 10, 90), new Version(3, 11, 0, 0)));
        Assert.True(UpdateChecker.AttemptSatisfiedBy(new Version(3, 10, 90), new Version(4, 0, 0, 0)));
    }

    [Fact]
    public void RollbackAcrossMinorOrMajor_IsNotSatisfied()
    {
        Assert.False(UpdateChecker.AttemptSatisfiedBy(new Version(3, 11, 0), new Version(3, 10, 99, 0)));
        Assert.False(UpdateChecker.AttemptSatisfiedBy(new Version(4, 0, 0), new Version(3, 99, 99, 0)));
    }

    /// <summary>
    /// A tag parses with Revision -1 ("v3.10.90" -> 3.10.90.-1) while the running assembly
    /// version carries Revision 0. Raw Version comparison would call those unequal; the
    /// reconciler must not see a revision difference as a failed install.
    /// </summary>
    [Fact]
    public void RevisionDifferenceAlone_IsSatisfied()
    {
        var fromTag = Version.Parse("3.10.90");
        Assert.Equal(-1, fromTag.Revision);
        Assert.True(UpdateChecker.AttemptSatisfiedBy(fromTag, new Version(3, 10, 90, 0)));
    }

    /// <summary>
    /// Both halves of the update subsystem must compare through the same normalization.
    /// The detection half ("is there something newer") was already ordered and correct;
    /// the reconciliation half used equality. They are now the same function, so a version
    /// the detector calls newer is exactly a version the reconciler calls satisfied.
    /// </summary>
    [Theory]
    [InlineData(3, 10, 90, 3, 10, 92)]
    [InlineData(3, 10, 90, 3, 11, 0)]
    [InlineData(3, 10, 90, 4, 0, 0)]
    public void DetectorAndReconcilerAgree(int aMaj, int aMin, int aBld, int rMaj, int rMin, int rBld)
    {
        var attempted = new Version(aMaj, aMin, aBld);
        var running = new Version(rMaj, rMin, rBld, 0);

        // If the detector would have called `running` newer than `attempted`, then landing on
        // `running` after attempting `attempted` is an overshoot — and must count as satisfied.
        Assert.True(UpdateChecker.Normalize(running) > UpdateChecker.Normalize(attempted));
        Assert.True(UpdateChecker.AttemptSatisfiedBy(attempted, running));
    }
}
