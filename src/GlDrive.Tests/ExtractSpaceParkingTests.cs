using System.IO;
using System.Text.RegularExpressions;
using GlDrive.Downloads;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Regression cover for 2026-09-24/25: D: had less room than a 23 GB set needed. The first
/// attempt wrote the volume to zero bytes free, the failure was retried as Transient five times
/// in ten minutes, and the set was abandoned against a fingerprint that freeing space can never
/// change. A full disk must park the path on the DRIVE, consume no retry, and release it exactly
/// when the drive has room again.
/// </summary>
public sealed class ExtractSpaceParkingTests
{
    private const long GB = 1024L * 1024 * 1024;

    private sealed class FakeDisk
    {
        public long? Free;
        public long? Probe(string _) => Free;
    }

    [Fact]
    public void Preflight_refuses_a_set_larger_than_free_space_minus_headroom()
    {
        var disk = new FakeDisk { Free = 23 * GB };
        var parking = new ExtractSpaceParking(disk.Probe, headroomBytes: 1 * GB);

        Assert.False(parking.Fits(@"D:\x265\Rel", 23 * GB, out var free));
        Assert.Equal(23 * GB, free);
        Assert.True(parking.Fits(@"D:\x265\Rel", 22 * GB, out _));
    }

    [Fact]
    public void Preflight_does_not_block_on_an_unmeasurable_drive()
    {
        var parking = new ExtractSpaceParking(new FakeDisk { Free = null }.Probe);
        Assert.True(parking.Fits(@"D:\x265\Rel", 100 * GB, out var free));
        Assert.Null(free);
    }

    [Fact]
    public void Parked_path_is_released_only_once_the_drive_has_room()
    {
        var disk = new FakeDisk { Free = 784 * 1024 };
        var parking = new ExtractSpaceParking(disk.Probe, headroomBytes: 1 * GB);
        const string path = @"D:\x265\Rel\rel.rar";

        Assert.True(parking.Park(path, @"D:\x265\Rel", 23 * GB));
        Assert.False(parking.Park(path, @"D:\x265\Rel", 23 * GB)); // second park is not "first"
        Assert.Empty(parking.TakeReady());
        Assert.True(parking.IsParked(path));

        disk.Free = 23 * GB + GB / 2; // fits the set but not the headroom
        Assert.Empty(parking.TakeReady());

        disk.Free = 23 * GB + 1 * GB;
        Assert.Equal(new[] { path }, parking.TakeReady());
        Assert.False(parking.IsParked(path));
        Assert.Empty(parking.TakeReady());
    }

    [Fact]
    public void Unmeasurable_drive_keeps_a_parked_path_parked()
    {
        var disk = new FakeDisk { Free = null };
        var parking = new ExtractSpaceParking(disk.Probe);
        parking.Park(@"D:\a.rar", @"D:\out", 1);

        Assert.Empty(parking.TakeReady());
        Assert.True(parking.IsParked(@"D:\a.rar"));
    }

    [Fact]
    public void Each_path_is_released_against_its_own_requirement()
    {
        var disk = new FakeDisk { Free = 10 * GB };
        var parking = new ExtractSpaceParking(disk.Probe, headroomBytes: 0);
        parking.Park(@"D:\small.rar", @"D:\out", 5 * GB);
        parking.Park(@"D:\big.rar", @"D:\out", 50 * GB);

        Assert.Equal(new[] { @"D:\small.rar" }, parking.TakeReady());
        Assert.True(parking.IsParked(@"D:\big.rar"));
    }

    [Theory]
    [InlineData(unchecked((int)0x80070070))] // ERROR_DISK_FULL — the production failure
    [InlineData(unchecked((int)0x80070027))] // ERROR_HANDLE_DISK_FULL
    public void Disk_full_is_recognised_by_win32_code(int hresult)
    {
        var ex = new IOException("There is not enough space on the disk. : 'D:\\x.gldrive-tmp'.", hresult);
        Assert.True(ExtractSpaceParking.IsDiskFull(ex));
        Assert.True(ExtractSpaceParking.IsDiskFull(new InvalidOperationException("wrap", ex)));
    }

    [Fact]
    public void Disk_full_is_recognised_from_unrar_write_error_exit()
    {
        Assert.True(ExtractSpaceParking.IsDiskFull(new IOException("UnRAR.exe failed (exit 5)")));
        Assert.True(ExtractSpaceParking.IsDiskFull(new IOException("Rar.exe failed (exit 5)")));
    }

    [Fact]
    public void Other_io_failures_are_not_disk_full()
    {
        Assert.False(ExtractSpaceParking.IsDiskFull(null));
        Assert.False(ExtractSpaceParking.IsDiskFull(new IOException("The process cannot access the file", unchecked((int)0x80070020))));
        Assert.False(ExtractSpaceParking.IsDiskFull(new IOException("UnRAR.exe failed (exit 3)")));
        Assert.False(ExtractSpaceParking.IsDiskFull(new IOException("UnRAR.exe failed (exit 50)")));
    }

    // ── Structural wiring (the extractor is a WPF window and cannot be built under xUnit) ──

    private static string ExtractorCode()
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "GlDrive", "UI", "ExtractorWindow.xaml.cs");
            if (File.Exists(candidate))
            {
                var s = File.ReadAllText(candidate);
                s = Regex.Replace(s, @"/\*[\s\S]*?\*/", "");
                return Regex.Replace(s, @"//[^\n]*", "");
            }
        }
        throw new FileNotFoundException("ExtractorWindow.xaml.cs not found");
    }

    private static string MethodBody(string code, string name)
    {
        var decl = Regex.Match(code,
            $@"(?:private|internal|public|protected)[^\n(]*\b{Regex.Escape(name)}\s*\([^)]*\)\s*\n?\s*\{{");
        Assert.True(decl.Success, $"Could not locate the declaration of {name}.");
        var start = decl.Index + decl.Length - 1;
        for (int i = start, depth = 0; i < code.Length; i++)
        {
            if (code[i] == '{') depth++;
            else if (code[i] == '}' && --depth == 0) return code.Substring(start, i - start + 1);
        }
        throw new Xunit.Sdk.XunitException($"Unbalanced braces in {name}.");
    }

    [Fact]
    public void AutoExtractItem_preflights_free_space_before_extracting()
    {
        var body = MethodBody(ExtractorCode(), "AutoExtractItem");
        var preflight = body.IndexOf("_spaceParked.Fits(", StringComparison.Ordinal);
        var work = body.IndexOf("ExtractArchiveAsync(", StringComparison.Ordinal);

        Assert.True(preflight >= 0, "AutoExtractItem must check free space before extracting.");
        Assert.True(preflight < work, "The free-space preflight must run before ExtractArchiveAsync.");
        Assert.Matches(@"if\s*\(\s*!_spaceParked\.Fits\(outputDir, requiredBytes, out var freeBytes\)\)\s*\{\s*ParkForSpace\([^;]*;\s*return;", body);
    }

    [Fact]
    public void Disk_full_failure_parks_instead_of_consuming_retries()
    {
        var body = MethodBody(ExtractorCode(), "AutoExtractItem");
        var catchBody = body[body.LastIndexOf("catch (Exception ex)", StringComparison.Ordinal)..];

        var diskFull = catchBody.IndexOf("ExtractSpaceParking.IsDiskFull(ex)", StringComparison.Ordinal);
        var park = catchBody.IndexOf("ParkForSpace(", StringComparison.Ordinal);
        var retry = catchBody.IndexOf("ScheduleWatchRetry(", StringComparison.Ordinal);

        Assert.True(diskFull >= 0 && park > diskFull, "A disk-full failure must be routed to ParkForSpace.");
        Assert.Matches(@"else\s*\{\s*Log\.Warning\(ex,[^;]*;\s*ScheduleWatchRetry\(", catchBody);
        Assert.True(retry > park, "ScheduleWatchRetry must be the non-disk-full branch only.");
    }

    [Fact]
    public void Sweep_releases_space_parked_paths()
    {
        var body = MethodBody(ExtractorCode(), "SweepAbandonedAsync");
        Assert.Contains("_spaceParked.TakeReady()", body);
        Assert.Matches(@"_spaceParked\.TakeReady\(\)\)[\s\S]*?HandleWatchedFileAsync\(path\)", body);
    }

    [Fact]
    public void ParkForSpace_never_writes_an_abandon_verdict()
    {
        var body = MethodBody(ExtractorCode(), "ParkForSpace");
        Assert.DoesNotContain("AbandonWatchedPath", body);
        Assert.DoesNotContain("_abandonStore", body);
        Assert.DoesNotContain("_watchAbandoned", body);
        Assert.Contains("_watchRetryCounts.Remove(path)", body);
        Assert.Contains("StartAbandonSweep()", body);
    }
}
