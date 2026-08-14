using System;
using System.IO;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Three give-up paths that never expired, all of the same shape: a decision made once and
/// never revisited, on a resource that was still perfectly healthy.
///
/// 2026-08-12: zephyr's IRC connected fine but could not enter #ent, because #ent is
/// invite-only and the SITE's announce bot was absent to send the INVITE. GlDrive retried
/// three times over ~30 seconds, gave up, and then sat OUT of the channel for 8h21m on a
/// live connection until a human invited it by hand. AutoJoinChannelsAsync runs only on
/// connect, so "gave up" meant gave up for the lifetime of the connection.
/// </summary>
public class IrcInviteRetryAndDeclineTests
{
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

    private const string IrcService = "src/GlDrive/Irc/IrcService.cs";
    private const string UpdateChecker = "src/GlDrive/Services/UpdateChecker.cs";

    // ---- 1. the invite-only retry no longer terminates -------------------------

    [Fact]
    public void The_invite_only_retry_never_stops_permanently()
    {
        var src = ReadSource(IrcService);

        // The old terminal branch dropped the channel from the pending map and told the user
        // to /join by hand. Nothing re-attempted after that for the life of the connection.
        Assert.DoesNotContain("_pendingInviteJoins.Remove(channel);\r\n                AddSystemMessage",
            src, StringComparison.Ordinal);
        Assert.DoesNotContain("Gave up joining", src, StringComparison.Ordinal);

        // What replaced it: a standing slow retry.
        Assert.Contains("SlowInviteRetryDelay", src, StringComparison.Ordinal);
    }

    [Fact]
    public void The_slow_retry_backs_off_but_never_to_infinity()
    {
        var src = ReadSource(IrcService);
        var body = src[src.IndexOf("SlowInviteRetryDelay(int attempts)", StringComparison.Ordinal)..];
        body = body[..body.IndexOf("private async Task RetryJoinAfterDelay", StringComparison.Ordinal)];

        // Climbs, then plateaus — a channel we are not invited to costs nothing to re-ask for.
        Assert.Contains("FromMinutes(5)", body, StringComparison.Ordinal);
        Assert.Contains("FromMinutes(15)", body, StringComparison.Ordinal);
        Assert.Contains("FromMinutes(30)", body, StringComparison.Ordinal);

        // No branch may return an unbounded or zero delay.
        Assert.DoesNotContain("Timeout.Infinite", body, StringComparison.Ordinal);
        Assert.DoesNotContain("TimeSpan.Zero", body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_slow_retry_re_issues_SITE_INVITE_not_just_JOIN()
    {
        var src = ReadSource(IrcService);

        // Re-sending JOIN alone can never succeed on a +i channel — the missing thing is the
        // invite. The fast burst only re-JOINed, which is why it could never have worked.
        var retryBody = src[src.IndexOf("private async Task RetryJoinAfterDelay", StringComparison.Ordinal)..];
        retryBody = retryBody[..retryBody.IndexOf("private async Task RequestSiteInviteAsync", StringComparison.Ordinal)];
        Assert.Contains("RequestSiteInviteAsync", retryBody, StringComparison.Ordinal);
    }

    // ---- 2. the invite path is no longer invisible -----------------------------

    [Fact]
    public void The_SITE_INVITE_path_writes_to_the_log_not_only_the_UI_tab()
    {
        var src = ReadSource(IrcService);
        var body = src[src.IndexOf("private async Task RequestSiteInviteAsync", StringComparison.Ordinal)..];
        body = body[..2000];

        // Previously every outcome went only to AddSystemMessage, so grepping the log for
        // "SITE INVITE" returned zero hits whether or not it had run — absence proved nothing.
        Assert.Contains("Log.Information", body, StringComparison.Ordinal);
        Assert.Contains("SITE INVITE", body, StringComparison.Ordinal);
    }

    [Fact]
    public void There_is_exactly_one_implementation_of_asking_for_an_invite()
    {
        var src = ReadSource(IrcService);

        // AutoJoinChannelsAsync and the standing retry must share it — two copies would drift.
        Assert.Equal(1, CountOccurrences(src, "private async Task RequestSiteInviteAsync"));
        Assert.True(CountOccurrences(src, "SiteInviteFunc(inviteNick") <= 1,
            "the SITE INVITE call must exist in exactly one place");
    }

    // ---- 3. a PM that cannot be sent says so ------------------------------------

    [Fact]
    public void SendMessage_reports_failure_instead_of_vanishing()
    {
        var src = ReadSource(IrcService);

        // Was: `if (_client == null || !_client.IsConnected) return;` — no send, no local
        // echo (AddMessage sits below the guard), no log. The message simply disappeared.
        Assert.Contains("public async Task<bool> SendMessage", src, StringComparison.Ordinal);

        var body = src[src.IndexOf("public async Task<bool> SendMessage", StringComparison.Ordinal)..];
        body = body[..body.IndexOf("public async Task SendAction", StringComparison.Ordinal)];

        Assert.Contains("return false", body, StringComparison.Ordinal);
        Assert.Contains("Log.Warning", body, StringComparison.Ordinal);
        Assert.Contains("was NOT sent", body, StringComparison.Ordinal);
    }

    // ---- 4. granting elevation clears the decline -------------------------------

    [Fact]
    public void A_successful_elevated_launch_clears_the_decline_marker()
    {
        var src = ReadSource(UpdateChecker);

        var start = src.IndexOf("Process.Start(psi);", StringComparison.Ordinal);
        var clear = src.IndexOf("ClearDeclinedUpdate();", StringComparison.Ordinal);

        Assert.True(start > 0 && clear > start,
            "the marker must be cleared after the elevated process actually starts");

        // It must NOT be cleared on the declined branch — that is where it gets written.
        var declineBranch = src.IndexOf("RecordDeclinedUpdate(tagName)", StringComparison.Ordinal);
        Assert.True(clear < declineBranch,
            "clearing must happen on the success path, above the decline handler");
    }

    [Fact]
    public void Clearing_the_marker_removes_the_file_and_tolerates_its_absence()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gldrive-decline-{Guid.NewGuid():N}.marker");

        File.WriteAllText(path, "v3.10.59\t2026-08-14T04:59:28.0000000Z");
        Assert.True(File.Exists(path));

        GlDrive.Services.UpdateChecker.ClearDeclinedUpdateAt(path);
        Assert.False(File.Exists(path));

        // Idempotent: clearing an absent marker must not throw.
        var ex = Record.Exception(() => GlDrive.Services.UpdateChecker.ClearDeclinedUpdateAt(path));
        Assert.Null(ex);
    }

    [Fact]
    public void A_cleared_marker_no_longer_suppresses_that_release()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gldrive-decline-{Guid.NewGuid():N}.marker");
        var now = new DateTime(2026, 8, 14, 6, 0, 0, DateTimeKind.Utc);

        GlDrive.Services.UpdateChecker.RecordDeclinedUpdateAt(path, "v3.10.59", now.AddHours(-1));
        Assert.True(GlDrive.Services.UpdateChecker.WasUpdateDeclinedAt(path, "v3.10.59", now));

        GlDrive.Services.UpdateChecker.ClearDeclinedUpdateAt(path);
        Assert.False(GlDrive.Services.UpdateChecker.WasUpdateDeclinedAt(path, "v3.10.59", now));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var n = 0;
        var i = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (i >= 0)
        {
            n++;
            i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal);
        }
        return n;
    }
}
