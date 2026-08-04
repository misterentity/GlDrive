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

/// <summary>
/// v3.10.47 — the dest-denied-MKD fast-skip predicate, shared by SpreadJob (which
/// decides whether to drop the dest for this release) and FxpTransfer (which decides
/// how loudly to log). Both MUST ask the same question: when those two gates used
/// different predicates the answers drifted and a race could neither dispatch nor
/// terminate (v3.10.45). Strings below are real production messages taken from
/// gldrive-20260803.log, where SYN was 0-for-902 on MKD across 8 sections.
/// </summary>
public class MkdFailureClassifier_ExpectedDenialTests
{
    [Theory]
    // The exact shape that produced 902 of the 937 daily "FXP transfer failed" warnings.
    [InlineData("MKD failed for /nsw/GRADIUS_ORIGINS_Update_v1.4.0_NSW-VENOM: 550 Error: Not allowed to make directories here.")]
    [InlineData("MKD failed for /mp3.today/VA-Deepened_Music_Vol._30-(VMCOMP1028)-WEB-2023-COS: 550 Error: Not allowed to make directories here.")]
    [InlineData("MKD failed for /x264-hd/Pops.2021.720p.WEB.H264-SHIIIT: 550 Error: Not allowed to make directories here.")]
    [InlineData("MKD failed: 550 MKD Denied by dirscript.")]
    [InlineData("MKD failed: 550 You cannot create that here")]
    public void IsExpectedReleaseScopedDenial_catches_dest_mkd_refusals(string msg)
        => Assert.True(MkdFailureClassifier.IsExpectedReleaseScopedDenial(msg));

    [Theory]
    // Genuine faults that MUST stay at Warning — quieting these would hide real breakage.
    [InlineData("Unable to read data from the transport connection: An existing connection was forcibly closed by the remote host..")]
    [InlineData("Unable to write data to the transport connection: An existing connection was forcibly closed by the remote host..")]
    [InlineData("STOR failed: 425 Can't build data connection (timeout).")]
    [InlineData("RETR failed: 550 /incoming/tv-sports/x.nfo: No such file or directory.")]
    [InlineData("RETR failed: 425 Can't build data connection: Connection refused.")]
    [InlineData("LIST failed: 425 Can't build data connection (timeout).")]
    // An upload denial is a DIFFERENT class: it drives a persistent (dst,section)
    // blacklist, so it must not be swallowed as an expected per-release skip.
    [InlineData("STOR failed: 553 .imdb: path-filter denied permission. (Filename deny)")]
    [InlineData("STOR failed: 553 Error: you have no upload rights for this directory!")]
    [InlineData("")]
    [InlineData(null)]
    public void IsExpectedReleaseScopedDenial_leaves_real_faults_loud(string? msg)
        => Assert.False(MkdFailureClassifier.IsExpectedReleaseScopedDenial(msg));

    /// <summary>
    /// Pins the shared predicate to its definition. If someone edits one of the three
    /// component predicates, this fails rather than letting the fast-skip gate and the
    /// logging gate silently disagree again.
    /// </summary>
    [Theory]
    [InlineData("MKD failed: 550 Error: Not allowed to make directories here.")]
    [InlineData("MKD failed: 550 MKD Denied by dirscript.")]
    [InlineData("STOR failed: 553 path-filter denied permission. (Filename deny)")]
    [InlineData("MKD failed: 550 Permission denied")]
    [InlineData("Unable to read data from the transport connection")]
    [InlineData("RETR failed: 550 Insufficient credits")]
    [InlineData("")]
    [InlineData(null)]
    public void IsExpectedReleaseScopedDenial_equals_its_component_predicates(string? msg)
    {
        var expected = MkdFailureClassifier.IsMkdError(msg)
                       && (MkdFailureClassifier.IsPermanentMkdPathDenial(msg)
                           || MkdFailureClassifier.IsReleaseScopedDirscriptDenial(msg));
        Assert.Equal(expected, MkdFailureClassifier.IsExpectedReleaseScopedDenial(msg));
    }

    [Theory]
    [InlineData("MKD failed for /nsw/X: 550 Error: Not allowed to make directories here.", true)]
    [InlineData("STOR failed: 553 path-filter denied permission. (Filename deny)", true)]
    [InlineData("MKD failed: 550 Permission denied", true)]
    [InlineData("Unable to read data from the transport connection", false)]
    [InlineData("RETR failed: 550 No such file or directory", false)]
    [InlineData(null, false)]
    public void IsMkdError_identifies_directory_creation_failures(string? msg, bool expected)
        => Assert.Equal(expected, MkdFailureClassifier.IsMkdError(msg));
}
