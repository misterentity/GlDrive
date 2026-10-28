using System.IO;
using GlDrive.Ftp;
using Xunit;

namespace GlDrive.Tests;

public sealed class LoginEvidenceLexingTests
{
    [Theory]
    [InlineData("read", false)]
    [InlineData("READING", false)]
    [InlineData("thread", true)]
    [InlineData("already", true)]
    [InlineData("read_more", true)]
    [InlineData("reading2", true)]
    [InlineData("read-ready", false)]
    public void Read_exclusions_are_whole_words_without_regex_deadlines(string context, bool timeout) =>
        Assert.Equal(timeout, LoginAttemptGovernor.IsTimeout(new IOException(context + ": connection timed out")));

    [Theory]
    [InlineData(" 421 rate limit exceeded", true)]
    [InlineData("450: throttled", true)]
    [InlineData("4210 rate limit exceeded", false)]
    [InlineData("421_local cooldown", false)]
    [InlineData("local 421 cooldown", false)]
    [InlineData("421 service unavailable", false)]
    public void Only_leading_complete_server_codes_and_throttle_text_are_evidence(string text, bool cooldown) =>
        Assert.Equal(cooldown, LoginAttemptGovernor.IsBncCooldown(new IOException(text)));

    [Fact]
    public void Long_failure_messages_preserve_the_original_failure_classification()
    {
        var message = new string('x', 100000) + " read: connection timed out";
        Assert.False(LoginAttemptGovernor.IsTimeout(new IOException(message)));
        Assert.False(LoginAttemptGovernor.IsBncCooldown(new IOException(message)));
    }
}
