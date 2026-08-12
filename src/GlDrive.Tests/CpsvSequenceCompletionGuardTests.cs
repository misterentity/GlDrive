using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// v3.10.55 — source-level guard for the CPSV mark/clear invariant.
///
/// <c>CpsvDataHelper.OpenDataTcp</c> marks the control channel as owing a reply,
/// and ONLY <c>CompleteDataSequence</c> clears it. That makes the pairing a
/// cross-file obligation: any code that composes its own CPSV sequence must
/// finish with <c>CompleteDataSequence</c>, never a raw <c>GetReply</c>.
///
/// Getting this wrong is not a no-op, it inverts the fix. <c>FxpTransfer</c>'s
/// Relay mode opens CPSV data channels on BOTH endpoints and originally read
/// its 226s with raw GetReply calls — which would have left both connections
/// flagged after every SUCCESSFUL relay, so the pool discarded them on return.
/// That is exactly the v3.10.14 failure (a per-file probe poisoning both
/// connections on success, ~2,871 needless quarantines/day), reintroduced from
/// the opposite direction.
///
/// A unit test cannot reach these paths without live FTP endpoints, so this
/// asserts the invariant against the source itself — the same approach
/// GnuTlsReflectionGuardTests uses for its native-teardown rule.
/// </summary>
public class CpsvSequenceCompletionGuardTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "GlDrive")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string[] SourceFiles() =>
        Directory.GetFiles(Path.Combine(RepoRoot(), "src", "GlDrive"), "*.cs", SearchOption.AllDirectories);

    [Fact]
    public void EveryFileThatOpensACpsvDataChannel_CompletesTheSequenceProperly()
    {
        var offenders = new List<string>();

        foreach (var path in SourceFiles())
        {
            var text = File.ReadAllText(path);

            // Files that never open a CPSV data channel carry no obligation.
            if (!text.Contains("OpenDataTcp(")) continue;

            // CpsvDataHelper itself defines both halves of the pairing.
            if (Path.GetFileName(path) == "CpsvDataHelper.cs") continue;

            // A raw GetReply is only a defect on a control channel that was
            // marked. Non-CPSV branches in the same file legitimately use it, so
            // require that the file at least reaches for CompleteDataSequence —
            // a file that opens a CPSV channel and never completes one cannot
            // possibly be clearing its marks.
            if (!text.Contains("CompleteDataSequence("))
                offenders.Add($"{Path.GetFileName(path)} calls OpenDataTcp but never CompleteDataSequence");
        }

        Assert.True(offenders.Count == 0, string.Join("\n", offenders));
    }

    [Fact]
    public void RelayMode_ClearsBothEndpointsOnASuccessfulTransfer()
    {
        // The specific regression: Relay marks src AND dst via two OpenDataTcp
        // calls, so a successful relay must clear BOTH. One clear is as broken
        // as none — the unpaired endpoint gets discarded on every success.
        var fxp = File.ReadAllText(Path.Combine(RepoRoot(), "src", "GlDrive", "Spread", "FxpTransfer.cs"));

        var opens = Regex.Matches(fxp, @"OpenDataTcp\(").Count;
        var completes = Regex.Matches(fxp, @"CompleteDataSequence\(").Count;

        Assert.Equal(2, opens);
        Assert.True(completes >= opens,
            $"FxpTransfer opens {opens} CPSV data channel(s) but completes only {completes}");
    }

    [Fact]
    public void FxpTransfer_DoesNotReadRelayCompletionWithARawGetReply()
    {
        var fxp = File.ReadAllText(Path.Combine(RepoRoot(), "src", "GlDrive", "Spread", "FxpTransfer.cs"));

        // The only surviving raw GetReply belongs to ReadReplyManagedTimeout,
        // which reads a PASV/PORT-mode reply on an unmarked channel.
        var raw = Regex.Matches(fxp, @"\bawait\s+(src|dst)\.GetReply\(").Count;

        Assert.Equal(0, raw);
    }
}
