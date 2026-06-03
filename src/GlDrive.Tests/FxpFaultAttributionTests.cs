using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// The attribution narrowing is catastrophic if it under-poisons a corrupt session
/// (native crash), so the load-bearing invariant is the CONSERVATIVE DEFAULT: a
/// transfer that never set a fault side must report None, which the SpreadJob maps
/// to Both. Full per-throw-site attribution is exercised by the manual runtime
/// check (force STOR-553 ⇒ dest only) since it needs live FXP connections.
/// </summary>
public class FxpFaultAttributionTests
{
    [Fact]
    public void Fresh_transfer_defaults_to_None_so_caller_poisons_both()
    {
        var transfer = new FxpTransfer();
        Assert.Equal(FxpFaultSide.None, transfer.FaultSide);
    }

    [Fact]
    public void FaultSide_enum_has_the_four_expected_values()
    {
        // Locks the contract SpreadJob.ApplyPoisonAttribution switches on.
        Assert.Equal(0, (int)FxpFaultSide.None);
        Assert.True(System.Enum.IsDefined(FxpFaultSide.Source));
        Assert.True(System.Enum.IsDefined(FxpFaultSide.Dest));
        Assert.True(System.Enum.IsDefined(FxpFaultSide.Both));
    }
}
