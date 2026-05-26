using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

public class ClassifyFailureTests
{
    [Theory]
    [InlineData("Release is NUKED — aborting race", "nuked")]
    [InlineData("Release not found on any server — check release name and section paths", "not-found")]
    [InlineData("STOR failed: 553 Error: you have no upload rights for this directory!", "upload-denied")]
    [InlineData("MKD failed: 550 MKD Denied by dirscript.", "upload-denied")]
    [InlineData("MKD failed: 553 Error: out of disk space, contact the siteop!", "site-full")]
    [InlineData("STOR failed: 553 Error: out of disk space, contact the siteop!", "site-full")]
    [InlineData("Code: 530 Message: Sorry, your account is restricted to 4 simultaneous logins.", "bnc-pressure")]
    [InlineData("Server in BNC cooldown — not attempting new connection", "bnc-pressure")]
    [InlineData("No connection to the server exists.", "transport")]
    [InlineData("Unable to read data from the transport connection: forcibly closed", "transport")]
    [InlineData("No activity for 60 seconds, no viable transfers", "no-activity")]
    [InlineData("Need 2+ servers — found release on 1, no eligible destination for [mp3]", "config")]
    [InlineData("No viable destinations — all targets are affil-blocked (zephyr)", "config")]
    public void Classifies_known_failures(string msg, string expected)
        => Assert.Equal(expected, SpreadJob.ClassifyFailure(msg));

    [Fact]
    public void Empty_message_is_empty_category()
        => Assert.Equal("", SpreadJob.ClassifyFailure(""));

    [Fact]
    public void Unknown_message_is_other()
        => Assert.Equal("other", SpreadJob.ClassifyFailure("something we have never seen"));
}

public class RaceSummaryTests
{
    [Fact]
    public void CleanRate_is_zero_when_no_finished_races()
    {
        var s = new RaceSummary(0, 0, 0, new Dictionary<string, int>());
        Assert.Equal(0.0, s.CleanRate);
    }

    [Fact]
    public void CleanRate_computes_fraction()
    {
        var s = new RaceSummary(Finished: 10, Clean: 7, Failed: 3, new Dictionary<string, int>());
        Assert.Equal(0.7, s.CleanRate, 3);
    }
}
