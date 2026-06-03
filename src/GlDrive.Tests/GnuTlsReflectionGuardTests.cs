using GlDrive.Ftp;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Guards the guard: if a FluentFTP/GnuTLS package bump renames an internal that
/// the native-crash-avoidance reflection depends on, Resolve() reports it missing
/// and this test goes red in CI before the broken build can ship. This is the
/// automated half of the startup VerifyOrFail check.
/// </summary>
public class GnuTlsReflectionGuardTests
{
    [Fact]
    public void Resolve_finds_every_reflected_member_against_pinned_packages()
    {
        var result = GnuTlsReflectionGuard.Resolve();
        Assert.True(result.Ok,
            "GnuTLS crash-guard reflection broke against the pinned FluentFTP/GnuTLS versions. " +
            "Missing: " + string.Join("; ", result.Missing));
        Assert.Empty(result.Missing);
    }
}
