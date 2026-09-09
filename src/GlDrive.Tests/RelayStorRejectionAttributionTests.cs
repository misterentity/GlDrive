using System.IO;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Pins the connection hygiene of a rejected STOR in <c>FxpTransfer.ExecuteRelay</c>.
/// Relay issues RETR first and STOR second. When the destination refuses the STOR
/// (glftpd `425 Can't build data connection` — ~1% of 09-06..09-08 relay transfers),
/// the source is entangled (an accepted RETR is streaming into a socket we close),
/// but the destination's control channel is in sync: the rejection IS the final
/// reply, exactly the state the 553 dupe-skip path already reuses 200+ times a day.
/// Attributing <c>Both</c> discarded one healthy login per event against a 4-login cap.
/// </summary>
public sealed class RelayStorRejectionAttributionTests
{
    private static readonly string Source = ReadFxpTransfer();

    private static int RelayStorThrow()
    {
        var relay = Source.IndexOf("private async Task<bool> ExecuteRelay(", StringComparison.Ordinal);
        Assert.True(relay > 0, "ExecuteRelay not found.");
        var idx = Source.IndexOf("$\"STOR failed: {storReply.Code} {storReply.Message}\"", relay, StringComparison.Ordinal);
        Assert.True(idx > relay, "Relay STOR rejection throw site not found.");
        return idx;
    }

    [Fact]
    public void Relay_stor_rejection_attributes_the_entangled_source_only()
    {
        var idx = RelayStorThrow();
        var lineStart = Source.LastIndexOf('\n', idx);
        var line = Source.Substring(lineStart, idx - lineStart);
        Assert.Contains("Fault(FxpFaultSide.Source,", line);
        Assert.DoesNotContain("FxpFaultSide.Both", line);
    }

    [Fact]
    public void Relay_stor_rejection_clears_dests_pending_data_sequence_first()
    {
        var idx = RelayStorThrow();
        var end = Source.LastIndexOf("CpsvDataHelper.EndDataSequence(dst);", idx, StringComparison.Ordinal);
        Assert.True(end > 0 && idx - end < 400,
            "Dest's OpenDataTcp mark must be cleared right before the STOR-rejection throw, or the pool discards the in-sync dest anyway.");
        // The clear must belong to the rejection branch, not the earlier dupe-skip branch.
        var dupeReturn = Source.LastIndexOf("return true;", idx, StringComparison.Ordinal);
        Assert.True(end > dupeReturn, "EndDataSequence(dst) found only in the dupe-skip branch, not the rejection branch.");
    }

    private static string ReadFxpTransfer()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (; dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "src", "GlDrive", "Spread", "FxpTransfer.cs");
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        throw new FileNotFoundException("FxpTransfer.cs not found from " + Directory.GetCurrentDirectory());
    }
}
