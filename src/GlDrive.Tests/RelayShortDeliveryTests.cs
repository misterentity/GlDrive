using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// 2026-10-05 19:29:48: a Relay of senna.s01e04.hdr...r15 read EOF from the source data
/// channel after 81,821,696 of 400,000,000 bytes, the completion reply then timed out, and
/// the "bytes were relayed, the reply is only confirmation" forgiveness logged
/// <c>FXP complete</c>. Ownership, the delivered count, the speed tracker (400 MB credited)
/// and telemetry all recorded a truncated file as delivered; only a later rescan re-raced it.
/// A source EOF is proof of delivery only when the byte count reaches the listed size.
/// </summary>
public sealed class RelayShortDeliveryTests
{
    [Theory]
    [InlineData(400_000_000, 400_000_000)]
    [InlineData(400_000_100, 400_000_000)] // file grew on a still-racing source
    [InlineData(1, 0)]                     // size unknown — keep the existing forgiveness
    public void Full_or_unknown_size_delivery_counts_as_delivered(long relayed, long expected)
    {
        Assert.True(FxpTransfer.RelayDeliveredInFull(relayed, expected));
    }

    [Theory]
    [InlineData(81_821_696, 400_000_000)]  // the production event
    [InlineData(399_999_999, 400_000_000)]
    [InlineData(0, 400_000_000)]
    [InlineData(0, 0)]
    public void Short_or_empty_delivery_is_not_delivered(long relayed, long expected)
    {
        Assert.False(FxpTransfer.RelayDeliveredInFull(relayed, expected));
    }

    [Fact]
    public void Completion_reply_forgiveness_is_gated_on_full_delivery_and_relay_receives_the_size()
    {
        var src = CpsvDataCommandRejectionTests.ReadSource("Spread", "FxpTransfer.cs");
        var start = src.IndexOf("private async Task<bool> ExecuteRelay(", StringComparison.Ordinal);
        Assert.True(start > 0);
        var end = src.IndexOf("internal void AcceptRelayRetrReply(", start, StringComparison.Ordinal);
        var body = src[start..end];

        Assert.Contains("when (RelayDeliveredInFull(totalRelayed, expectedBytes))", body);
        Assert.DoesNotContain("when (totalRelayed > 0)", body);
        Assert.DoesNotContain("ExecuteRelay(source.Client, dest.Client, srcPath, dstPath, transferTimeoutSeconds, ct, pasvSw)", src);
    }
}
