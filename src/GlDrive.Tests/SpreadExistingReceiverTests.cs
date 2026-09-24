using System.Collections.Generic;
using GlDrive.Config;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

public sealed class SpreadExistingReceiverTests
{
    private static Dictionary<string, ServerConfig> Sites(bool peerDownloadOnly = true) => new()
    {
        ["receiver"] = new ServerConfig { Id = "receiver" },
        ["peer"] = new ServerConfig { Id = "peer", SpreadSite = new() { DownloadOnly = peerDownloadOnly } }
    };

    [Fact]
    public void Sole_receiver_holds_release_and_peer_cannot_receive_is_no_work()
    {
        Assert.True(SpreadJob.AllReceivingSitesAlreadyPresent(new HashSet<string> { "receiver" }, Sites()));
    }

    [Fact]
    public void Release_only_on_download_only_source_still_needs_transfer()
    {
        Assert.False(SpreadJob.AllReceivingSitesAlreadyPresent(new HashSet<string> { "peer" }, Sites()));
    }

    [Fact]
    public void Another_receiving_site_missing_release_must_not_be_hidden()
    {
        // Includes destinations absent from discovery due to mapping, blacklist or probe failure.
        Assert.False(SpreadJob.AllReceivingSitesAlreadyPresent(new HashSet<string> { "receiver" }, Sites(false)));
    }

    [Fact]
    public void No_discovered_source_is_not_success()
    {
        Assert.False(SpreadJob.AllReceivingSitesAlreadyPresent(new HashSet<string>(), Sites()));
    }

    [Fact]
    public void No_receiving_sites_is_not_success()
    {
        var sites = Sites();
        sites["receiver"].SpreadSite.DownloadOnly = true;
        Assert.False(SpreadJob.AllReceivingSitesAlreadyPresent(new HashSet<string> { "receiver", "peer" }, sites));
        Assert.False(SpreadJob.AllReceivingSitesAlreadyPresent(new HashSet<string>(), new Dictionary<string, ServerConfig>()));
    }

    [Fact]
    public void Never_source_site_is_still_a_receiver_that_needs_work()
    {
        var sites = Sites(false);
        sites["peer"].SpreadSite.NeverSource = true;
        Assert.False(SpreadJob.AllReceivingSitesAlreadyPresent(new HashSet<string> { "receiver" }, sites));
    }
}
