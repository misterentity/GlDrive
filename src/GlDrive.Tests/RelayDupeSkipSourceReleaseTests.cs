using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// v3.11.33 — a Relay dupe-skip held the source login for 15 s.
///
/// Relay sends RETR before STOR (so a source rejection never leaves a 0-byte dest stub).
/// When the dest then answers STOR with a dupe 553, the source has already accepted the
/// RETR and is sitting in its data-TLS handshake, waiting for us as the TLS server. ABOR
/// got "225 ABOR command successful" in ~1 s, but the RETR's own final reply only came when
/// glftpd's handshake timed out: "435 Failed TLS negotiation on data channel (timeout),
/// disconnected [14.999s]" — 28 times on 2026-10-26/27, on a source capped at one login.
/// Closing our end of the source data socket first fails the handshake immediately.
/// </summary>
public sealed class RelayDupeSkipSourceReleaseTests
{
    [Fact]
    public void The_source_data_socket_closes_before_the_dupe_skip_waits_on_any_source_reply()
    {
        var fxp = CpsvDataCommandRejectionTests.ReadSource("Spread", "FxpTransfer.cs");
        var relay = fxp.IndexOf("private async Task<bool> ExecuteRelay(", StringComparison.Ordinal);
        var dupe = fxp.IndexOf("if (IsDupeRejection(storReply))", relay, StringComparison.Ordinal);
        var abor = fxp.IndexOf("await src.Execute(\"ABOR\", ct);", dupe, StringComparison.Ordinal);
        Assert.True(relay >= 0 && dupe > relay && abor > dupe, "the Relay dupe-skip branch moved");

        var beforeAbor = fxp[dupe..abor];
        Assert.Contains("srcTcp.Close();", beforeAbor.Split('\n').Select(l => l.Trim()));
    }
}
