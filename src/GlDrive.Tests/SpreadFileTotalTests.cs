using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Regression coverage for the production race-accounting invariant found in v3.10.98:
/// FilesOwned/FilesDelivered could exceed FilesTotal because an SFV replaced, rather
/// than bounded, the discovered release-file count.
/// </summary>
public sealed class SpreadFileTotalTests
{
    [Fact]
    public void Ancillary_files_raise_total_above_sfv_archive_count()
    {
        // Ten archive entries + the SFV, plus NFO and sample discovered by LIST.
        Assert.Equal(13, SpreadJob.ResolveFileTotal(sfvExpected: 11, discovered: 13));
    }

    [Fact]
    public void Sfv_count_remains_lower_bound_while_release_is_landing()
    {
        Assert.Equal(21, SpreadJob.ResolveFileTotal(sfvExpected: 21, discovered: 7));
    }

    [Fact]
    public void Discovery_is_authoritative_when_no_sfv_exists()
    {
        Assert.Equal(6, SpreadJob.ResolveFileTotal(sfvExpected: 0, discovered: 6));
    }
}
