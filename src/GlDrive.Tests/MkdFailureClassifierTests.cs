using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

public class MkdFailureClassifierTests
{
    [Theory]
    [InlineData("550", "Not allowed to make directories here.", true)]
    [InlineData("550", "Permission denied", true)]
    [InlineData("550", "path-filter denied", true)]
    [InlineData("550", "you are not a member of this group", true)]
    [InlineData("550", "You cannot create that here", true)]
    [InlineData("550", "MKD Denied by dirscript.", true)]   // added v2.6.4
    [InlineData("553", "Error: out of disk space, contact the siteop!", true)] // v3.5.1
    [InlineData("553", "disk full", true)]                                     // v3.5.1
    [InlineData("550", "out of disk space, contact the siteop!", true)]        // either code
    public void IsPermanent_catches_permanent_mkd_denials(string code, string msg, bool expected)
        => Assert.Equal(expected, MkdFailureClassifier.IsPermanent(code, msg));

    [Theory]
    [InlineData("550", "Directory created")]                 // success-shaped message
    [InlineData("553", "Some other 553")]                    // 553 alone isn't permanent
    [InlineData("450", "Transient error")]                   // 4xx are transient
    [InlineData("550", "")]                                  // empty
    public void IsPermanent_ignores_transient_or_wrong_code(string code, string msg)
        => Assert.False(MkdFailureClassifier.IsPermanent(code, msg));

    [Theory]
    [InlineData("STOR failed: 553 Error: you have no upload rights for this directory!", true)]
    [InlineData("STOR failed: 553 .imdb: path-filter denied permission. (Filename deny)", true)]
    [InlineData("STOR failed: 553 Permission denied", true)]
    [InlineData("553 Error: you have no upload rights for this directory!", true)]
    [InlineData("STOR failed: 553 Error: out of disk space, contact the siteop!", true)] // v3.5.1
    public void IsPermanentUploadDenial_catches_stor_denials(string msg, bool expected)
        => Assert.Equal(expected, MkdFailureClassifier.IsPermanentUploadDenial(msg));

    [Theory]
    [InlineData("Unable to read data from the transport connection: forcibly closed")]
    [InlineData("The operation has timed out.")]
    [InlineData("Code: 530 Message: Sorry, your account is restricted to 4 simultaneous logins.")]
    [InlineData("")]
    [InlineData(null)]
    public void IsPermanentUploadDenial_ignores_transient(string? msg)
        => Assert.False(MkdFailureClassifier.IsPermanentUploadDenial(msg));

    [Theory]
    [InlineData("RETR failed: 550 Insufficient credits.", true)]   // observed 2026-05-29
    [InlineData("550 Insufficient credits.", true)]
    [InlineData("RETR failed: 550 Not enough credits", true)]
    [InlineData("550 You are out of credits", true)]
    [InlineData("RETR failed: 550 No credits left", true)]
    [InlineData("RETR failed: 550 No such file or directory", false)] // unrelated 550
    [InlineData("STOR failed: 553 disk full", false)]                 // disk-full, not credits
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsCreditExhaustion_catches_credit_denials(string? msg, bool expected)
        => Assert.Equal(expected, MkdFailureClassifier.IsCreditExhaustion(msg));
}

public class MkdFailureClassifier_SourceMissingTests
{
    [Theory]
    [InlineData("RETR failed: 550 No such file or directory", true)]
    [InlineData("RETR failed: 550 File not found", true)]
    [InlineData("550 file.rar: No such file or directory", true)]
    [InlineData("RETR failed: 550 Cannot find the file", true)]
    public void IsSourceFileMissing_catches_retr_not_found(string msg, bool expected)
        => Assert.Equal(expected, MkdFailureClassifier.IsSourceFileMissing(msg));

    [Theory]
    [InlineData("RETR failed: 550 Insufficient credits")]
    [InlineData("STOR failed: 553 no upload rights")]
    [InlineData("MKD failed: 550 No such file or directory")]
    [InlineData("data transfer timeout")]
    [InlineData("")]
    [InlineData(null)]
    public void IsSourceFileMissing_ignores_non_source_missing(string? msg)
        => Assert.False(MkdFailureClassifier.IsSourceFileMissing(msg));
}
