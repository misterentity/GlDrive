using System;
using System.IO;
using GlDrive.Irc;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// The Verbose IRC trace used to redact exactly one command (PASS), so JOIN's channel-key
/// parameter — the FiSH key kept DPAPI-encrypted in fish-keys-{serverId}.json — was written
/// to gldrive-{date}.log in cleartext. Turning on Verbose to debug an IRC fault would have
/// dumped every channel key and services password to disk.
///
/// These tests pin the property that fixes it: redaction keys on WHICH PARAMETER of a
/// command is a credential, not on a list of commands somebody noticed leaking.
/// </summary>
public class IrcLineRedactorTests
{
    private const string Secret = "WhoOYxIf_-tz*O5ADy-OU";

    // ---- the original case -------------------------------------------------

    [Fact]
    public void Pass_is_still_redacted()
    {
        var line = IrcLineRedactor.Redact($"PASS {Secret}");
        Assert.DoesNotContain(Secret, line, StringComparison.Ordinal);
        Assert.StartsWith("PASS ", line, StringComparison.Ordinal);
    }

    // ---- the gap that motivated this ---------------------------------------

    [Fact]
    public void Join_key_is_redacted_but_the_channel_is_kept()
    {
        var line = IrcLineRedactor.Redact($"JOIN #ent {Secret}");
        Assert.DoesNotContain(Secret, line, StringComparison.Ordinal);
        Assert.Contains("#ent", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Join_without_a_key_is_untouched()
    {
        Assert.Equal("JOIN #ent", IrcLineRedactor.Redact("JOIN #ent"));
        Assert.Equal("JOIN #a,#b", IrcLineRedactor.Redact("JOIN #a,#b"));
    }

    // ---- services credential exchanges, target-agnostic --------------------

    [Theory]
    [InlineData("PRIVMSG NickServ :identify {0}")]
    [InlineData("PRIVMSG nickserv :IDENTIFY {0}")]
    [InlineData("PRIVMSG X@channels.undernet.org :login dave {0}")]
    [InlineData("PRIVMSG sitebot :auth dave {0}")]
    [InlineData("NICKSERV :identify {0}")]
    [InlineData("NS identify {0}")]
    [InlineData("OPER dave {0}")]
    public void Credential_exchanges_are_redacted(string template)
    {
        var line = IrcLineRedactor.Redact(string.Format(template, Secret));
        Assert.DoesNotContain(Secret, line, StringComparison.Ordinal);
    }

    [Fact]
    public void Oper_keeps_the_account_name_and_drops_only_the_password()
    {
        var line = IrcLineRedactor.Redact($"OPER dave {Secret}");
        Assert.Contains("dave", line, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, line, StringComparison.Ordinal);
    }

    // ---- the server echoes the key back at us ------------------------------

    [Fact]
    public void Inbound_mode_reply_324_does_not_leak_the_channel_key()
    {
        var line = IrcLineRedactor.Redact($":irc.kthx.info 324 entity0 #ent +k {Secret}");
        Assert.DoesNotContain(Secret, line, StringComparison.Ordinal);
        Assert.Contains("#ent", line, StringComparison.Ordinal);
        Assert.Contains("324", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Mode_setting_or_clearing_a_key_is_redacted_in_both_directions()
    {
        Assert.DoesNotContain(Secret, IrcLineRedactor.Redact($"MODE #ent +k {Secret}"),
            StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, IrcLineRedactor.Redact($"MODE #ent -k {Secret}"),
            StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, IrcLineRedactor.Redact($":dave!u@h MODE #ent +kl {Secret} 50"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Mode_without_a_key_is_untouched()
    {
        Assert.Equal("MODE #ent +o dave", IrcLineRedactor.Redact("MODE #ent +o dave"));
        Assert.Equal("MODE #ent +nt", IrcLineRedactor.Redact("MODE #ent +nt"));
    }

    // ---- ordinary traffic must stay readable, or the trace is useless ------

    [Fact]
    public void Ordinary_chat_and_announces_are_not_redacted()
    {
        const string announce = ":zephyr!bot@site PRIVMSG #ent :NEW RELEASE: -GAMES- HELL.GALAXY-RUNE by sha-zam";
        Assert.Equal(announce, IrcLineRedactor.Redact(announce));

        Assert.Equal("PRIVMSG #ent :hello there", IrcLineRedactor.Redact("PRIVMSG #ent :hello there"));
        Assert.Equal("PING :1786600000", IrcLineRedactor.Redact("PING :1786600000"));
        Assert.Equal("NICK entity0", IrcLineRedactor.Redact("NICK entity0"));
    }

    [Fact]
    public void A_bare_verb_with_no_argument_is_not_treated_as_a_credential()
    {
        // "did you identify?" carries nothing — masking it would train people to ignore the mask.
        Assert.Equal("PRIVMSG #ent :identify", IrcLineRedactor.Redact("PRIVMSG #ent :identify"));
    }

    // ---- fail closed --------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(":")]
    [InlineData("::::")]
    [InlineData("JOIN")]
    public void Degenerate_input_never_throws(string input)
    {
        var ex = Record.Exception(() => IrcLineRedactor.Redact(input));
        Assert.Null(ex);
    }

    [Fact]
    public void Null_is_tolerated()
    {
        Assert.Equal("", IrcLineRedactor.Redact(null));
    }

    // ---- the call sites actually use it ------------------------------------

    [Fact]
    public void IrcClient_redacts_both_directions_and_no_longer_special_cases_PASS()
    {
        var src = ReadSource("src/GlDrive/Irc/IrcClient.cs");

        Assert.Contains("Log.Verbose(\"[IRC >] {Line}\", IrcLineRedactor.Redact(line))", src,
            StringComparison.Ordinal);
        Assert.Contains("Log.Verbose(\"[IRC <] {Line}\", IrcLineRedactor.Redact(line))", src,
            StringComparison.Ordinal);

        // The old one-command check must be gone, not merely bypassed.
        Assert.DoesNotContain("\"PASS [REDACTED]\"", src, StringComparison.Ordinal);
    }

    private static string ReadSource(string relativePath)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12 && dir != null; i++)
        {
            var candidate = Path.Combine(dir, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException($"Could not locate {relativePath}");
    }
}
