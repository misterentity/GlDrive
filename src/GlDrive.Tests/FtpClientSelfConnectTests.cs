using FluentFTP;
using GlDrive.Ftp;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// v3.10.125 — FluentFTP's <c>SelfConnectMode</c> was never configured, so every
/// pooled client ran on the library default, <c>OnConnectionLost</c>: any
/// <c>Execute</c> on a control channel FluentFTP believed had dropped performed a
/// full reconnect — AUTH TLS, USER, PASS — transparently, inside the command.
///
/// That login is issued by the library, so <see cref="ServerLoginGate"/> never sees
/// it and never counts it. Dave's accounts allow 4 simultaneous logins, so a login
/// the gate does not know about is a login the gate cannot reserve against: the
/// account overshoots its cap, the BNC answers 530, and the pool — whose own
/// counter still reads <c>created=0</c> — reports a server problem.
///
/// Production evidence (one machine, v3.10.124):
///   * 2026-09-18: 62 exception stacks whose frames are
///     <c>AsyncFtpClient.Execute -> Connect(reConnect) -> HandshakeAsync -> GetReply</c>,
///     i.e. a silent re-login that happened to FAIL. Rising: 31 (09-16) -> 62 (09-18).
///   * Those are only the ones that threw. A silent reconnect that SUCCEEDS logs
///     nothing at all, so the true rate is unmeasurable from our own logs — which is
///     precisely why it stayed invisible while its consequences were attributed
///     elsewhere (borrow timeouts, "pool empty", ghost sessions).
///
/// The pool already owns every other "do not act behind the owner's back" setting
/// (<c>Noop</c>, <c>DisconnectWithQuit</c>); reconnection belongs in the same set.
/// With <c>Never</c>, a dropped connection makes the command throw, the caller
/// disposes the <c>PooledConnection</c>, the pool discards it, and the replacement
/// is created through the gate — the path the pool already takes today whenever the
/// silent reconnect fails.
/// </summary>
public class FtpClientSelfConnectTests
{
    [Fact]
    public void OwnerExclusiveConfig_ForbidsLibraryInitiatedReconnect()
    {
        var config = new FtpConfig();
        FtpClientFactory.ApplyOwnerExclusiveConfig(config);

        Assert.Equal(FtpSelfConnectMode.Never, config.SelfConnectMode);
    }

    /// <summary>
    /// Guards against the vacuous-test trap: if FluentFTP's default were already
    /// <c>Never</c>, the assertion above would pass no matter what the factory did.
    /// A fresh <see cref="FtpConfig"/> must NOT be Never, so the assertion above can
    /// only pass because our code set it.
    /// </summary>
    [Fact]
    public void FluentFtpDefault_IsNotNever_SoTheSettingIsNotANoOp()
    {
        Assert.NotEqual(FtpSelfConnectMode.Never, new FtpConfig().SelfConnectMode);
    }

    /// <summary>
    /// The other two settings in the same set. They were already applied inline in
    /// <c>Create()</c>; moving them into the helper keeps all three in one place and
    /// keeps this test honest about what "owner-exclusive" means.
    /// </summary>
    [Fact]
    public void OwnerExclusiveConfig_LeavesKeepaliveAndQuitToThePool()
    {
        var config = new FtpConfig();
        FtpClientFactory.ApplyOwnerExclusiveConfig(config);

        Assert.False(config.Noop);
        Assert.False(config.DisconnectWithQuit);
    }
}
