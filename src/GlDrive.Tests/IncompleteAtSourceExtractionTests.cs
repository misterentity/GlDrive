using System.IO;
using System.Text.RegularExpressions;
using GlDrive.Downloads;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// 2026-10-13 → 10-15: a download of Small.Prophets.S01E03.DV.2160p taken while zephyr held
/// 20% of it brought the site's zipscript stubs down as files (<c>*.r00-missing</c>,
/// <c>[##] -  20% Complete - [zephyr]</c>). The download side was fixed in 8d1611f, but the
/// folder already on disk held 3 of 15 parts. Every process start replayed the full
/// stall → retry → abandon cycle against it: 6 × 300s readiness waits and 13 WRN lines per
/// restart, 70+ WRN over three days, the top WRN class on 10-14 and 10-15.
///
/// The parts were never coming: the folder carries the SOURCE's own statement that it lacked
/// them. That is evidence a retry cannot change, so the stall is a durable verdict keyed to
/// the volume-set fingerprint (which lapses the moment a missing part does arrive).
/// </summary>
public sealed class IncompleteAtSourceExtractionTests
{
    [Theory]
    [InlineData("small.prophets.s01e03.dv.2160p.web.h265-cakes.r00-missing")]
    [InlineData("[##] -  20% Complete - [zephyr]")]
    [InlineData("-missing-release.r04")]
    [InlineData("[incomplete]")]
    public void Zipscript_incomplete_markers_are_found(string marker)
    {
        var names = new[]
        {
            @"E:\tv\Rel\rel.rar", @"E:\tv\Rel\rel.r09", @"E:\tv\Rel\rel.sfv",
            Path.Combine(@"E:\tv\Rel", marker),
        };

        Assert.Equal(marker, ReleaseSourceState.FindIncompleteMarker(names));
    }

    [Fact]
    public void A_complete_release_folder_has_no_incomplete_marker()
    {
        var names = new[]
        {
            @"E:\tv\Rel\rel.rar", @"E:\tv\Rel\rel.r00", @"E:\tv\Rel\rel.sfv", @"E:\tv\Rel\rel.nfo",
            @"E:\tv\Rel\[##] - 100% Complete - [zephyr]",
        };

        Assert.Null(ReleaseSourceState.FindIncompleteMarker(names));
    }

    private static string ExtractorCode()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (; dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "GlDrive", "UI", "ExtractorWindow.xaml.cs");
            if (File.Exists(candidate))
            {
                var source = File.ReadAllText(candidate);
                source = Regex.Replace(source, @"/\*[\s\S]*?\*/", "");
                return Regex.Replace(source, @"//[^\n]*", "");
            }
        }

        throw new FileNotFoundException("ExtractorWindow.xaml.cs not found");
    }

    private static string MethodBody(string code, string name)
    {
        var declaration = Regex.Match(code,
            $@"(?:private|internal|public|protected)[^\n(]*\b{Regex.Escape(name)}\s*\([^)]*\)\s*\n?\s*\{{");
        Assert.True(declaration.Success, $"Could not locate the declaration of {name}.");

        var start = declaration.Index + declaration.Length - 1;
        var depth = 0;
        for (var i = start; i < code.Length; i++)
        {
            if (code[i] == '{') depth++;
            else if (code[i] == '}' && --depth == 0) return code.Substring(start, i - start + 1);
        }

        throw new Xunit.Sdk.XunitException($"Unbalanced braces walking the body of {name}.");
    }

    /// <summary>
    /// The watcher route and the AutoExtractItem route each had their own copy of the stall
    /// branch. A check added to one copy is the v3.10.120 shape (a gate wired into 1 of 3
    /// call sites), so the stall is handled in ONE place and both routes call it.
    /// </summary>
    [Fact]
    public void Every_stall_is_handled_by_the_one_helper()
    {
        var code = ExtractorCode();

        Assert.Single(Regex.Matches(code, @"archive stalled before it was ready"));
        Assert.Contains("archive stalled before it was ready", MethodBody(code, "HandleStalledArchive"));
        Assert.True(Regex.Matches(code, @"HandleStalledArchive\s*\(").Count >= 3,
            "Both readiness call sites must route a stall through HandleStalledArchive.");
    }

    [Fact]
    public void A_stall_with_a_source_marker_is_a_durable_verdict_not_a_retry()
    {
        var body = MethodBody(ExtractorCode(), "HandleStalledArchive");

        var check = body.IndexOf("IncompleteAtSourceMarker", System.StringComparison.Ordinal);
        var abandon = body.IndexOf("durable: true", System.StringComparison.Ordinal);
        var retry = body.IndexOf("ScheduleWatchRetry", System.StringComparison.Ordinal);

        Assert.True(check >= 0, "HandleStalledArchive must consult IncompleteAtSourceMarker.");
        Assert.True(abandon > check, "A source-declared incomplete set must be abandoned durably.");
        Assert.True(retry > abandon, "Only a stall without source evidence may consume a retry.");
        Assert.DoesNotContain("durable: false", body);
    }
}
