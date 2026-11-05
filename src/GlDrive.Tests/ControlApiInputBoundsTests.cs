using System;
using System.IO;
using System.Text.Json;
using GlDrive.Services.Automation;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Bounds found by driving the live control API against the running app (v3.10.59), not by
/// reading code. Each test pins one thing that was observed to actually happen.
///
/// The headline one: the since-retired POST /races accepted a 2,000,000-character `section`,
/// started a real race with it, and wrote it to gldrive-{date}.log as a single 2,000,172-byte
/// line. The log rolls at 10 MB keeping 3 files, so five such calls erase every trace of what
/// the app was doing. Race start now goes only through the spread.start automation tool;
/// these tests pin the same guarantees there.
/// </summary>
public class ControlApiInputBoundsTests
{
    private const int MaxSectionLength = 128;
    private const int MaxReleaseLength = 512;

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

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    // ---- the limits themselves -------------------------------------------------

    [Fact]
    public void Section_and_release_limits_are_generous_but_finite()
    {
        const string realRelease = "That.Time.I.Got.Reincarnated.as.a.Slime.S04E18.1080p.WEB.H264-SENPAI";
        Assert.Equal(realRelease, AutomationService.Required(Json(new { releaseName = realRelease }), "releaseName", MaxReleaseLength));
        Assert.Equal("TV_1080", AutomationService.Required(Json(new { section = "TV_1080" }), "section", MaxSectionLength));

        Assert.Throws<ArgumentException>(() =>
            AutomationService.Required(Json(new { section = new string('x', MaxSectionLength + 1) }), "section", MaxSectionLength));
        Assert.Throws<ArgumentException>(() =>
            AutomationService.Required(Json(new { releaseName = new string('x', MaxReleaseLength + 1) }), "releaseName", MaxReleaseLength));
    }

    [Fact]
    public void Body_cap_is_finite_and_far_above_any_real_payload()
    {
        var cap = GlDrive.Services.Control.ControlRequest.MaxBodyBytes;
        Assert.Equal(64 * 1024, cap);

        // The largest real body is a race start: two short strings plus JSON punctuation.
        var biggestReal = MaxSectionLength + MaxReleaseLength + 64;
        Assert.True(cap > biggestReal * 10,
            "body cap should leave an order of magnitude over the largest legitimate payload");
    }

    // ---- the call sites actually enforce them ----------------------------------

    [Fact]
    public void StartRace_bounds_section_and_release_before_the_engine()
    {
        var src = ReadSource("src/GlDrive/Services/Automation/AutomationService.IrcSpread.cs");

        var sectionCheck = src.IndexOf($"Required(a, \"section\", {MaxSectionLength})", StringComparison.Ordinal);
        var releaseCheck = src.IndexOf($"Required(a, \"releaseName\", {MaxReleaseLength})", StringComparison.Ordinal);
        var startRace = src.IndexOf("Spread.StartRaceDetailed(section, release", StringComparison.Ordinal);

        Assert.True(sectionCheck > 0, "section length must be validated");
        Assert.True(releaseCheck > 0, "release length must be validated");
        Assert.True(startRace > sectionCheck && startRace > releaseCheck,
            "length checks must precede StartRaceDetailed");
    }

    [Fact]
    public void An_oversized_body_is_refused_with_413_not_parsed_as_bad_json()
    {
        var src = ReadSource("src/GlDrive/Services/Control/Endpoints/AutomationEndpoints.cs");

        // A truncated read would surface as "Malformed JSON", which sends the caller
        // hunting a syntax error that is not there.
        var nullCheck = src.IndexOf("body == null", StringComparison.Ordinal);
        var refusal = src.IndexOf("ErrorAsync(413", StringComparison.Ordinal);
        var parse = src.IndexOf("JsonDocument.Parse", StringComparison.Ordinal);
        Assert.True(nullCheck > 0 && refusal > nullCheck, "an over-cap body must be answered 413");
        Assert.True(refusal < parse, "the size refusal must come before parsing");
    }

    [Fact]
    public void ReadBody_is_bounded_and_never_reads_to_end()
    {
        var src = ReadSource("src/GlDrive/Services/Control/ControlRequest.cs");

        // The unbounded read is what took the app 238 MB -> 585 MB on a 20 MB body.
        // Anchor on the CALL form: the doc comment names the old method in prose, and
        // matching that would make this assertion depend on comment wording.
        Assert.DoesNotContain("ReadToEndAsync()", src, StringComparison.Ordinal);
        Assert.Contains("MaxBodyBytes", src, StringComparison.Ordinal);
        Assert.Contains("ContentLength64 > MaxBodyBytes", src, StringComparison.Ordinal);
    }

    [Fact]
    public void Stop_verifies_the_race_exists_before_reporting_success()
    {
        // Observed live: the old POST /races/totally-made-up/stop answered 200 {"stopped":"..."}
        // while GET /races/totally-made-up answered 404 for the same id. The tool path resolves
        // the job first, and an unknown id surfaces as 404 from the HTTP layer.
        var src = ReadSource("src/GlDrive/Services/Automation/AutomationService.IrcSpread.cs");
        var existence = src.IndexOf("?? throw new KeyNotFoundException($\"No active race: {id}\")", StringComparison.Ordinal);
        var stopJob = src.IndexOf("spread.StopJob(id)", StringComparison.Ordinal);
        Assert.True(existence > 0, "stop must check the race exists");
        Assert.True(existence < stopJob, "the existence check must precede StopJob");

        var endpoint = ReadSource("src/GlDrive/Services/Control/Endpoints/AutomationEndpoints.cs");
        var notFound = endpoint.IndexOf("catch (KeyNotFoundException)", StringComparison.Ordinal);
        Assert.True(notFound > 0 && endpoint.IndexOf("ErrorAsync(404", notFound, StringComparison.Ordinal) > notFound,
            "an unknown race id must answer 404");
    }
}
