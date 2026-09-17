using System.Collections.Generic;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// v3.10.122 — the auto-race "No viable destination" skip named only the sites that
/// survived the earlier section/rules filters. On 2026-09-16 every tv-sports announce
/// logged "No viable destination (siteb.example.net: download-only)": sited had been
/// dropped a step earlier for having no [tv-sports] section, so the one fixable cause was
/// never printed and the message read as if siteb were the only candidate.
/// </summary>
public class NoViableDestinationReasonTests
{
    [Fact]
    public void Names_sites_dropped_for_missing_section()
    {
        var reason = CandidatePredicates.DescribeNoViableDestination(
            new[] { "siteb: download-only" }, new[] { "sited" }, new List<string>(), "tv-sports");

        Assert.Equal("No viable destination (siteb: download-only; no [tv-sports] on: sited)", reason);
    }

    [Fact]
    public void Names_sites_denied_by_rules()
    {
        var reason = CandidatePredicates.DescribeNoViableDestination(
            new[] { "siteb: download-only" }, new List<string>(), new[] { "sited: rules" }, "tv-hd");

        Assert.Equal("No viable destination (siteb: download-only; denied: sited: rules)", reason);
    }

    [Fact]
    public void Unchanged_when_every_site_reached_the_receiver_check()
    {
        var reason = CandidatePredicates.DescribeNoViableDestination(
            new[] { "siteb: download-only", "sited: affil-blocked" }, new List<string>(), new List<string>(), "tv-hd");

        Assert.Equal("No viable destination (siteb: download-only, sited: affil-blocked)", reason);
    }

    [Fact]
    public void Auto_race_preflight_uses_the_formatter()
    {
        var source = System.IO.File.ReadAllText(FindRepoFile("src", "GlDrive", "Spread", "SpreadManager.cs"));
        Assert.Contains("CandidatePredicates.DescribeNoViableDestination(receiverExclusions, sectionMissing, denials, category)", source);
        Assert.DoesNotContain("$\"No viable destination ({string.Join", source);
    }

    private static string FindRepoFile(params string[] relative)
    {
        var dir = System.AppContext.BaseDirectory;
        for (var i = 0; i < 12 && dir != null; i++)
        {
            var candidate = System.IO.Path.Combine(dir, System.IO.Path.Combine(relative));
            if (System.IO.File.Exists(candidate)) return candidate;
            dir = System.IO.Directory.GetParent(dir)?.FullName;
        }
        throw new System.InvalidOperationException("repo file not found");
    }
}
