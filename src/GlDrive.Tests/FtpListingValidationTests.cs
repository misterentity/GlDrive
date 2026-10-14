using System.IO;
using FluentFTP;
using GlDrive.Ftp;
using Xunit;

namespace GlDrive.Tests;

public sealed class FtpListingValidationTests
{
    private static FtpReply Reply(string code, string command = "", string message = "reply") => new()
    {
        // Model freshly parsed network strings, including otherwise identical replies.
        Code = new string(code.ToCharArray()), Command = new string(command.ToCharArray()),
        Message = new string(message.ToCharArray())
    };

    [Theory]
    [InlineData("226")]
    [InlineData("250")]
    public void Fresh_completion_accepts_an_empty_listing(string code) =>
        FtpListing.ValidateReply(Reply(code), "/empty");

    [Theory]
    [InlineData("550", true)]
    [InlineData("425", true)]
    [InlineData("421", false)]
    public void Negative_reply_reports_whether_the_borrow_can_be_reused(string code, bool reusable)
    {
        var error = Assert.Throws<DataCommandRejectedException>(() =>
            FtpListing.ValidateReply(Reply(code, "MLSD /denied"), "/denied"));
        Assert.Equal(code, error.Code);
        Assert.Equal(reusable, FtpCommandRejection.IsClean(error));
    }

    [Theory]
    [InlineData("150")]
    [InlineData("229")]
    [InlineData("200")]
    [InlineData("")]
    public void Missing_completion_cannot_be_treated_as_empty_success(string code) =>
        Assert.Throws<IOException>(() => FtpListing.ValidateReply(Reply(code), "/release"));

    [Fact]
    public void A_new_success_overrides_an_old_rejection_and_can_have_no_command()
    {
        var old = Reply("550", "MLSD /denied");
        var current = Reply("226");
        var selected = FtpListing.LatestListingReply([current, Reply("150", "MLSD /good"), old], old);
        FtpListing.ValidateReply(selected, "/good");
    }

    [Fact]
    public void Successful_restore_does_not_hide_the_current_listing_rejection()
    {
        var old = Reply("226");
        var selected = FtpListing.LatestListingReply(
            [Reply("250", "CWD /"), Reply("550", "MLSD /denied"), old], old);
        Assert.Throws<DataCommandRejectedException>(() => FtpListing.ValidateReply(selected, "/denied"));
    }

    [Fact]
    public void Absorbed_passive_open_failure_cannot_reuse_an_old_success()
    {
        var old = Reply("226", "MLSD /same");
        var selected = FtpListing.LatestListingReply([Reply("229", "EPSV"), old], old);
        Assert.Throws<IOException>(() => FtpListing.ValidateReply(selected, "/same"));
        selected = FtpListing.LatestListingReply([Reply("250", "CWD /"), old], old);
        Assert.Throws<IOException>(() => FtpListing.ValidateReply(selected, "/same"));
        selected = FtpListing.LatestListingReply([old], old);
        Assert.Throws<IOException>(() => FtpListing.ValidateReply(selected, "/same"));
    }

    [Fact]
    public void Identical_text_from_a_new_response_is_not_the_previous_reply()
    {
        var old = Reply("226", "MLSD /same");
        var selected = FtpListing.LatestListingReply([Reply("226", "MLSD /same"), old], old);
        FtpListing.ValidateReply(selected, "/same");
    }
}
