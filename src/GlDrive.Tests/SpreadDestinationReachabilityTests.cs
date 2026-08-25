using System;
using System.IO;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Regression guard for v3.10.78 — the destination preflight that judged reachability
/// from configuration alone.
///
/// TryAutoRaceInternalAsync exists partly to reject a provably impossible topology
/// before spending a race slot: if no allowed participant can RECEIVE the release, it
/// logs an INFO skip instead of starting a job that must fail. Its three exclusions
/// (download-only, destination-blacklisted, affil-blocked) are all CONFIG facts. SYN's
/// FTP host died 2026-08-13; SYN carries no affils and is not download-only, so it kept
/// scoring as a viable receiver for eleven days. viableReceiverCount was therefore never
/// zero, the preflight fired ONCE in three days, and 128 jobs started and failed with the
/// exact "No viable destinations — affil-blocked (zephyr)" it exists to prevent — because
/// SpreadJob builds its participant map from the live pool registry, which drops SYN and
/// leaves only the affil-blocked peer.
///
/// The fix passes live connectivity (GetConnectedServerIds, i.e. the spread-pool
/// registry — the same source SpreadJob filters on) into the predicate, and fails OPEN
/// when nothing is connected so a startup window cannot silently suppress every race.
///
/// These are source-text guards because SpreadManager needs a live config, pools and
/// FTP factories to construct. They assert on CODE, never on comments.
/// </summary>
public class SpreadDestinationReachabilityTests
{
    private static string ReadSource(params string[] relative)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12 && dir != null; i++)
        {
            var parts = new string[relative.Length + 1];
            parts[0] = dir;
            Array.Copy(relative, 0, parts, 1, relative.Length);
            var candidate = Path.Combine(parts);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new InvalidOperationException(
            "Could not locate " + string.Join("/", relative) + " from " + AppContext.BaseDirectory);
    }

    private static string ExtractMethod(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature + " not found — was it renamed?");

        var open = source.IndexOf('{', start);
        Assert.True(open > 0, "Could not find method body opening brace.");

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0) return source[open..(i + 1)];
            }
        }

        throw new InvalidOperationException("Unbalanced braces after " + signature);
    }

    /// <summary>
    /// The predicate must take reachability as a REQUIRED parameter. A defaulted
    /// parameter would let a future call site silently reacquire the v3.10.78 bug,
    /// and would make the wiring assertion below vacuous.
    /// </summary>
    [Fact]
    public void CanReceiveRelease_declares_destinationReachable_without_a_default()
    {
        var source = ReadSource("src", "GlDrive", "Spread", "CandidatePredicates.cs");
        var method = source[source.IndexOf("internal static bool CanReceiveRelease", StringComparison.Ordinal)..];
        var signature = method[..method.IndexOf(')')];

        Assert.Contains("bool destinationReachable", signature);
        Assert.DoesNotContain("destinationReachable =", signature);
    }

    /// <summary>
    /// The preflight must read LIVE connectivity. Reading it from anywhere other than the
    /// spread-pool registry would reintroduce a proxy that can drift from the resource
    /// SpreadJob actually filters on (recurring pattern #8).
    /// </summary>
    [Fact]
    public void Destination_preflight_sources_reachability_from_the_live_pool_registry()
    {
        var source = ReadSource("src", "GlDrive", "Spread", "SpreadManager.cs");
        var method = ExtractMethod(source, "private async Task TryAutoRaceInternalAsync");

        Assert.Contains("GetConnectedServerIds()", method);
        Assert.Contains("destinationReachable", method);
    }

    /// <summary>
    /// Reachability must actually reach the predicate. The preflight could compute a
    /// perfectly correct connectivity set and still pass the old four arguments — which
    /// is precisely how a guard becomes inert.
    /// </summary>
    [Fact]
    public void Destination_preflight_passes_reachability_into_CanReceiveRelease()
    {
        var source = ReadSource("src", "GlDrive", "Spread", "SpreadManager.cs");
        var method = ExtractMethod(source, "private async Task TryAutoRaceInternalAsync");

        var call = method.IndexOf("CandidatePredicates.CanReceiveRelease", StringComparison.Ordinal);
        Assert.True(call >= 0, "The preflight no longer calls CanReceiveRelease.");

        var args = method[call..(method.IndexOf(')', call) + 1)];
        Assert.Contains("destinationReachable", args);
        // Must pass the computed LOCAL, not a literal. Asserting only that the argument
        // list mentions the name is vacuous: `destinationReachable: true` contains it too,
        // and that is precisely the inert-guard shape this test exists to catch. (Verified
        // by mutation — the substring-only assertion passed against that mutant.)
        Assert.DoesNotContain("destinationReachable: true", args);
        Assert.DoesNotContain("destinationReachable: false", args);
    }

    /// <summary>
    /// Fail-open guard. An empty pool registry cannot distinguish "still starting up"
    /// from "everything is down", so reachability may only EXCLUDE while at least one
    /// server is connected. Without this, a restart would suppress every auto-race and
    /// trade a noisy failure for a silent one (recurring pattern #3's inverse).
    /// </summary>
    [Fact]
    public void Destination_preflight_fails_open_when_no_server_is_connected()
    {
        var source = ReadSource("src", "GlDrive", "Spread", "SpreadManager.cs");
        var method = ExtractMethod(source, "private async Task TryAutoRaceInternalAsync");

        Assert.Contains("reachabilityKnown", method);
        Assert.Contains("connectedIds.Count > 0", method);
        // The exclusion must be disjunctive on the guard, not unconditional.
        Assert.Contains("!reachabilityKnown || connectedIds.Contains(serverId)", method);
    }

    /// <summary>
    /// An unreachable server must be REPORTED as unreachable. v3.10.78's whole cost was
    /// paid in misattribution: the surviving message blamed the affil list of a different
    /// site for a topology whose real defect was a dead host.
    /// </summary>
    [Fact]
    public void Destination_preflight_reports_not_connected_as_its_own_reason()
    {
        var source = ReadSource("src", "GlDrive", "Spread", "SpreadManager.cs");
        var method = ExtractMethod(source, "private async Task TryAutoRaceInternalAsync");

        Assert.Contains("\"not connected\"", method);
    }
}
