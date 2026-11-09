using System.Collections.Generic;
using System.IO;
using GlDrive.Ftp;
using GlDrive.Services;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// 2026-11-08/09: sitec reaped every pooled login 300-330 s after connect even though the
/// monitor NOOPed every 30 s (glftpd does not count NOOP as activity). Each reaped login
/// answered the next probe "421 Timeout (300 seconds)" and the monitor declared the whole
/// server lost — 250 false outages, a fresh login each, and a login-cap lockout at 10:38.
/// Separately, a release denied by rules on EVERY site was logged as "no configured server
/// has section [tv-hd]".
/// </summary>
public sealed class IdleReapAndSkipReasonTests
{
    [Fact]
    public void Keepalive_is_a_command_glftpd_counts_as_activity() =>
        Assert.Equal("PWD", FtpConnectionPool.KeepaliveCommand);

    [Fact]
    public async Task One_reaped_login_is_not_a_server_outage()
    {
        var probes = 0;
        Exception? reported = null;
        var reaped = new IOException("421 Timeout (300 seconds): closing control connection.");
        await ConnectionMonitor.ProbeWithOneRetryAsync(
            _ => Task.FromResult<Exception?>(++probes == 1 ? reaped : null),
            ex => reported = ex, CancellationToken.None);
        Assert.Equal(2, probes);
        Assert.Same(reaped, reported);
    }

    [Fact]
    public async Task Fresh_login_failing_too_is_a_real_outage()
    {
        var probes = 0;
        var second = new IOException("connection refused");
        var thrown = await Assert.ThrowsAsync<IOException>(() => ConnectionMonitor.ProbeWithOneRetryAsync(
            _ => Task.FromResult<Exception?>(++probes == 1 ? new IOException("421 reaped") : second),
            _ => { }, CancellationToken.None));
        Assert.Same(second, thrown);
        Assert.Equal(2, probes);
    }

    [Fact]
    public async Task Healthy_probe_does_not_borrow_twice()
    {
        var probes = 0;
        await ConnectionMonitor.ProbeWithOneRetryAsync(
            _ => { probes++; return Task.FromResult<Exception?>(null); },
            _ => Assert.Fail("no failure to report"), CancellationToken.None);
        Assert.Equal(1, probes);
    }

    [Fact]
    public async Task Borrow_failure_is_not_retried()
    {
        var probes = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => ConnectionMonitor.ProbeWithOneRetryAsync(
            _ => { probes++; throw new InvalidOperationException("Account login cap reached"); },
            _ => { }, CancellationToken.None));
        Assert.Equal(1, probes);
    }

    [Fact]
    public void Rules_denying_every_site_are_named_as_the_reason()
    {
        var why = CandidatePredicates.DescribeNoEligibleSection(0,
            new List<string> { "siteb: rules", "sited: rules", "sitec: rules" }, "tv-hd");
        Assert.Equal("denied on every server (siteb: rules, sited: rules, sitec: rules)", why);
        Assert.DoesNotContain("no configured server has section", why);
    }

    [Fact]
    public void Missing_section_stays_the_reason_when_sites_were_allowed() =>
        Assert.Equal("no configured server has section [tv-sports] (denied: sited: rules)",
            CandidatePredicates.DescribeNoEligibleSection(2, new List<string> { "sited: rules" }, "tv-sports"));

    [Fact]
    public void Missing_section_without_denials_is_unchanged() =>
        Assert.Equal("no configured server has section [ebook]",
            CandidatePredicates.DescribeNoEligibleSection(3, new List<string>(), "ebook"));
}
