using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using GlDrive.Downloads;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Regression cover for 2026-09-24 19:47:41: a client writing one volume at a time left
/// <c>.rar</c> + <c>.r31–.r39</c> on disk with <c>.r00–.r30</c> not yet started. Nothing was
/// locked and two samples 2s apart agreed, so the motion-only gate called 10 of 41 parts
/// "settled"; SharpCompress threw "unpacked file size does not match header: expected
/// 2024379567 found 474383163" and the set was recorded unrecoverable. Absence is a property
/// of the set's shape, so the gate now counts provably-missing members.
/// </summary>
public sealed class VolumeSetCompletenessTests
{
    private const string Base = "the.murder.detective.s01e03.1080p.web.h264-codswallop";

    private static string[] Names(params string[] suffixes) =>
        suffixes.Select(s => $"{Base}.{s}").ToArray();

    private static string[] RVolumes(int from, int toInclusive) =>
        Enumerable.Range(from, toInclusive - from + 1).Select(i => $"{Base}.r{i:00}").ToArray();

    [Fact]
    public void IncidentShape_GapBeforeR31_CountsThirtyOneMissing()
    {
        var present = Names("rar").Concat(RVolumes(31, 39));

        Assert.Equal(31, VolumeSetReadiness.CountMissingVolumes(Base, present, null));
    }

    [Fact]
    public void IncidentShape_WithItsSfv_IsNotReadyHoweverStill()
    {
        var present = Names("rar").Concat(RVolumes(31, 39)).ToArray();
        var sfv = Names("rar").Concat(RVolumes(0, 39));
        var missing = VolumeSetReadiness.CountMissingVolumes(Base, present, sfv);
        Assert.Equal(31, missing);

        var still = new VolumeSetReadiness.Snapshot(10, 474_384_323, 0, missing);
        Assert.False(VolumeSetReadiness.IsReady(still, still));
        // Quiet-but-incomplete must not read as "arriving": it has to reach the inactivity
        // budget and end as a retryable stall, never as Ready.
        Assert.False(VolumeSetReadiness.IsStillArriving(still, still));
    }

    [Fact]
    public void MissingTailVolumes_LeaveNoGap_OnlyTheSfvProvesThem()
    {
        var present = Names("rar").Concat(RVolumes(0, 20)).ToArray();

        Assert.Equal(0, VolumeSetReadiness.CountMissingVolumes(Base, present, null));
        Assert.Equal(19, VolumeSetReadiness.CountMissingVolumes(
            Base, present, Names("rar").Concat(RVolumes(0, 39))));
    }

    [Fact]
    public void LoneFirstVolume_WithSfvDeclaringMore_IsIncomplete()
    {
        Assert.Equal(40, VolumeSetReadiness.CountMissingVolumes(
            Base, Names("rar"), Names("rar").Concat(RVolumes(0, 39))));
    }

    [Fact]
    public void CompleteSet_HasNothingMissing()
    {
        var all = Names("rar").Concat(RVolumes(0, 39)).ToArray();

        Assert.Equal(0, VolumeSetReadiness.CountMissingVolumes(Base, all, all));
        var settled = new VolumeSetReadiness.Snapshot(41, 1_931_000_000, 0, 0);
        Assert.True(VolumeSetReadiness.IsReady(settled, settled));
    }

    [Fact]
    public void SfvEntriesOutsideTheSet_AreIgnored()
    {
        var all = Names("rar").Concat(RVolumes(0, 3)).ToArray();
        var sfv = all.Concat(new[]
        {
            $"sample-{Base}.mkv", $"{Base}.nfo", "other.release.rar", "other.release.r00",
        });

        Assert.Equal(0, VolumeSetReadiness.CountMissingVolumes(Base, all, sfv));
    }

    [Fact]
    public void SfvNamesMatchCaseInsensitively()
    {
        var present = Names("rar").Concat(RVolumes(0, 1)).ToArray();
        var sfv = present.Select(n => n.ToUpperInvariant());

        Assert.Equal(0, VolumeSetReadiness.CountMissingVolumes(Base, present, sfv));
    }

    [Fact]
    public void ThreeDigitVolumeGaps_AreCounted()
    {
        var present = Names("rar", "r000", "r002");

        Assert.Equal(1, VolumeSetReadiness.CountMissingVolumes(Base, present, null));
    }

    [Fact]
    public void SVolume_ProvesTheEntirePrecedingRRange()
    {
        Assert.Equal(100, VolumeSetReadiness.CountMissingVolumes(Base, Names("rar", "s00"), null));
        Assert.Equal(99, VolumeSetReadiness.CountMissingVolumes(Base, Names("rar", "r00", "s00"), null));
        Assert.Equal(1, VolumeSetReadiness.CountMissingVolumes(Base,
            Names("rar", "s00", "s02").Concat(RVolumes(0, 99)), null));
        Assert.Equal(0, VolumeSetReadiness.CountMissingVolumes(Base,
            Names("rar", "s00", "s01").Concat(RVolumes(0, 99)), null));
    }

    [Theory]
    [InlineData("part1", "part3")]
    [InlineData("part01", "part03")]
    [InlineData("part001", "part003")]
    public void ModernVolumeGaps_AreCounted(string first, string last)
    {
        Assert.Equal(1, VolumeSetReadiness.CountMissingVolumes($"{Base}.{first}",
            Names($"{first}.rar", $"{last}.rar"), null));
    }

    [Fact]
    public void ModernSfv_CountsMissingTailOnce_AndIgnoresOtherSets()
    {
        var sfv = Names("part01.rar", "part02.rar", "part03.rar", "part04.rar",
            "part04.rar", "extras.part05.rar", "r00", "rar");
        Assert.Equal(2, VolumeSetReadiness.CountMissingVolumes($"{Base}.part01",
            Names("part01.rar", "part03.rar"), sfv));
        Assert.Equal(0, VolumeSetReadiness.CountMissingVolumes($"{Base}.part01",
            Names("part01.rar", "part02.rar", "part03.rar", "part04.rar"), sfv));
    }

    [Fact]
    public void ModernLargeSuffix_DoesNotRequireEnumeratingEveryAbsentPart()
    {
        Assert.Equal(int.MaxValue - 2, VolumeSetReadiness.CountMissingVolumes($"{Base}.part01",
            Names("part01.rar", $"part{int.MaxValue}.rar", "part999999999999999999999999.rar"), null));
    }

    [Fact]
    public void LargeGapAndAdditionalSfvNames_DoNotOverflowToReady()
    {
        var missing = VolumeSetReadiness.CountMissingVolumes($"{Base}.part1",
            Names("part1.rar", $"part{int.MaxValue}.rar"), Names("part01.rar", "part001.rar", "part0001.rar"));
        Assert.Equal(int.MaxValue, missing);
        var snapshot = new VolumeSetReadiness.Snapshot(2, 2, 0, missing);
        Assert.False(VolumeSetReadiness.IsReady(snapshot, snapshot));
    }

    [Fact]
    public void ReadSfvDeclaredNames_SkipsCommentsAndBlankLines()
    {
        var dir = Directory.CreateTempSubdirectory("gldrive-sfv-").FullName;
        try
        {
            File.WriteAllLines(Path.Combine(dir, "x.sfv"), new[]
            {
                "; generated by zipscript", "", $"{Base}.rar 1A2B3C4D", $"{Base}.r00\tDEADBEEF", "garbage",
                $"{Base}.r01 incomplete", $"{Base}.r02 DEADBEE", $"{Base}.r03 DEADBEEFF",
                $"{Base}.r04 DEADBEEG", "file with spaces.rar \t 12345678",
            });

            var names = VolumeSetReadiness.ReadSfvDeclaredNames(dir);

            Assert.Equal(new[] { $"{Base}.rar", $"{Base}.r00", "file with spaces.rar" }, names);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ReadSfvDeclaredNames_MissingDirectory_IsEmptyNotThrown()
    {
        Assert.Empty(VolumeSetReadiness.ReadSfvDeclaredNames(
            Path.Combine(Path.GetTempPath(), "gldrive-no-such-dir-" + Guid.NewGuid())));
    }

    /// <summary>
    /// The predicate is inert unless the sampler actually feeds it. Assert the snapshot is
    /// built from the computed local, not a literal — a substring check for the argument name
    /// alone would also pass <c>MissingCount: 0</c> (the v3.10.78 guard trap).
    /// </summary>
    [Fact]
    public void Sampler_FeedsTheComputedMissingCount_IntoTheSnapshot()
    {
        var code = ExtractorCode();
        var body = code[code.IndexOf("VolumeSetReadiness.Snapshot SampleVolumeSet(", StringComparison.Ordinal)..];
        body = body[..body.IndexOf("\n    }", StringComparison.Ordinal)];

        Assert.Matches(@"var\s+missing\s*=\s*VolumeSetReadiness\.CountMissingVolumes\(", body);
        Assert.Matches(@"new\s+VolumeSetReadiness\.Snapshot\(\s*volumes\.Count,\s*totalBytes,\s*locked,\s*missing\s*\)", body);
        Assert.Contains("ReadSfvDeclaredNames", body);
    }

    private static string ExtractorCode()
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir != null; dir = dir.Parent)
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
}
