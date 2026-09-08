using System;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Regression coverage for the v3.10.108 source-relocation cascade. When one
/// endpoint's pool failed before TYPE/PASV/CPSV/STOR/RETR, the generic catch
/// poisoned the successfully borrowed peer even though it had performed no I/O.
/// Four such setup failures on 2026-09-08 turned one emptied source pool into two.
/// </summary>
public sealed class FxpFailurePolicyTests
{
    [Fact]
    public void Peer_is_reusable_when_other_pool_fails_before_protocol_starts()
        => Assert.False(FxpFailurePolicy.ShouldPoisonPeers(transferProtocolStarted: false));

    [Fact]
    public void Peer_is_conservatively_poisoned_after_protocol_starts()
        => Assert.True(FxpFailurePolicy.ShouldPoisonPeers(transferProtocolStarted: true));

    [Theory]
    [InlineData("Pool exhausted: all connections discarded and new connections failed")]
    [InlineData("Pool exhausted after stale connection replacement")]
    [InlineData("Server in BNC cooldown — not attempting new connection")]
    [InlineData("Account login cap reached — no login permit available")]
    public void Expected_setup_pressure_is_a_retryable_deferral(string message)
        => Assert.True(FxpFailurePolicy.IsExpectedSetupDeferral(new InvalidOperationException(message)));

    [Fact]
    public void Unexpected_setup_fault_keeps_warning_visibility()
        => Assert.False(FxpFailurePolicy.IsExpectedSetupDeferral(new ObjectDisposedException("pool")));

    [Fact]
    public void Wrapped_pool_pressure_is_still_a_deferral()
        => Assert.True(FxpFailurePolicy.IsExpectedSetupDeferral(
            new Exception("outer", new InvalidOperationException("Pool exhausted"))));
}
