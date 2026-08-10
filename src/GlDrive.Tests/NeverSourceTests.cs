using System;
using System.IO;
using GlDrive.Config;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// `NeverSource` marks a site you seed but never leech from — the exact mirror of
/// `DownloadOnly`, which bars a site from being a DESTINATION.
///
/// A site can become a source by three independent routes, so the flag has to be honoured
/// in all three or it leaks in through the side door:
///   1. SpreadJob Phase 1 discovery — the site simply has the release;
///   2. FindBestTransfer — per-file route scoring picks it as `srcId`;
///   3. SpreadManager alternate-source search — mid-race failover to another holder.
/// These are source-level assertions because all three sit inside long private methods
/// that need live pools to execute.
/// </summary>
public class NeverSourceTests
{
    private static string Read(params string[] parts)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12 && dir != null; i++)
        {
            var candidate = Path.Combine(new[] { dir }.Concat(parts).ToArray());
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException("Could not locate " + string.Join("/", parts));
    }

    [Fact]
    public void Defaults_to_off_so_existing_sites_are_unaffected()
    {
        Assert.False(new SiteSpreadConfig().NeverSource);
    }

    [Fact]
    public void Is_independent_of_DownloadOnly()
    {
        // The two flags bar opposite roles; setting one must not imply the other.
        var s = new SiteSpreadConfig { NeverSource = true };
        Assert.False(s.DownloadOnly);
        var d = new SiteSpreadConfig { DownloadOnly = true };
        Assert.False(d.NeverSource);
    }

    [Fact]
    public void Route_1_phase_one_discovery_does_not_add_a_never_source_site()
    {
        var src = Read("src", "GlDrive", "Spread", "SpreadJob.cs");
        var guard = src.IndexOf("if (config.SpreadSite.NeverSource)", StringComparison.Ordinal);
        Assert.True(guard > 0, "Phase 1 discovery must check NeverSource before sourceServers.Add");

        // The unguarded Add must be inside the else branch, i.e. after the guard.
        var add = src.IndexOf("sourceServers.Add(serverId);", StringComparison.Ordinal);
        Assert.True(add > guard,
            "sourceServers.Add(serverId) must sit behind the NeverSource guard");
    }

    [Fact]
    public void Route_2_find_best_transfer_skips_never_source_as_src()
    {
        var src = Read("src", "GlDrive", "Spread", "SpreadJob.cs");
        Assert.Contains("_serverConfigs[srcId].SpreadSite.NeverSource", src);
        Assert.Contains("skippedNeverSource", src);
        // Surfaced in the skip summary, so a stalled race says why rather than just "0 candidates".
        Assert.Contains("neverSource={skippedNeverSource}", src);
    }

    [Fact]
    public void Route_3_alternate_source_search_skips_never_source()
    {
        var src = Read("src", "GlDrive", "Spread", "SpreadManager.cs");
        Assert.Contains("sc.SpreadSite.NeverSource", src);
    }

    [Fact]
    public void Is_editable_from_the_server_dialog()
    {
        var xaml = Read("src", "GlDrive", "UI", "ServerEditDialog.xaml");
        Assert.Contains("SpreadNeverSourceBox", xaml);
        var code = Read("src", "GlDrive", "UI", "ServerEditDialog.xaml.cs");
        Assert.Contains("_serverConfig.SpreadSite.NeverSource = SpreadNeverSourceBox.IsChecked == true;", code);
        Assert.Contains("SpreadNeverSourceBox.IsChecked = existing.SpreadSite.NeverSource;", code);
    }
}
