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
    public void IsPermanent_catches_permanent_mkd_denials(string code, string msg, bool expected)
        => Assert.Equal(expected, MkdFailureClassifier.IsPermanent(code, msg));

    [Theory]
    [InlineData("550", "Directory created")]                 // not a denial
    [InlineData("553", "Not allowed to make directories")]   // wrong code → not MKD-permanent
    [InlineData("450", "Transient error")]
    [InlineData("550", "")]
    public void IsPermanent_ignores_transient_or_wrong_code(string code, string msg)
        => Assert.False(MkdFailureClassifier.IsPermanent(code, msg));

    [Theory]
    [InlineData("STOR failed: 553 Error: you have no upload rights for this directory!", true)]
    [InlineData("STOR failed: 553 .imdb: path-filter denied permission. (Filename deny)", true)]
    [InlineData("STOR failed: 553 Permission denied", true)]
    [InlineData("553 Error: you have no upload rights for this directory!", true)]
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
}
