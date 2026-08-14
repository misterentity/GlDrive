using System;
using System.IO;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Bounds found by driving the live control API against the running app (v3.10.59), not by
/// reading code. Each test pins one thing that was observed to actually happen.
///
/// The headline one: POST /races accepted a 2,000,000-character `section`, started a real
/// race with it, and wrote it to gldrive-{date}.log as a single 2,000,172-byte line. The log
/// rolls at 10 MB keeping 3 files, so five such calls erase every trace of what the app was
/// doing — the same evidence-destruction that already cost this project a day of history
/// twice (v3.10.47, v3.10.54), except driven by a caller instead of by noisy logging.
/// </summary>
public class ControlApiInputBoundsTests
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

    // ---- the limits themselves -------------------------------------------------

    [Fact]
    public void Section_and_release_limits_are_generous_but_finite()
    {
        // Big enough that no real caller trips them, small enough that the log survives.
        Assert.Equal(128, GlDrive.Services.Control.Endpoints.SpreadEndpoints.MaxSectionLength);
        Assert.Equal(512, GlDrive.Services.Control.Endpoints.SpreadEndpoints.MaxReleaseLength);

        // A real scene release must fit comfortably.
        const string realRelease = "That.Time.I.Got.Reincarnated.as.a.Slime.S04E18.1080p.WEB.H264-SENPAI";
        Assert.True(realRelease.Length < GlDrive.Services.Control.Endpoints.SpreadEndpoints.MaxReleaseLength);
        Assert.True("TV_1080".Length < GlDrive.Services.Control.Endpoints.SpreadEndpoints.MaxSectionLength);
    }

    [Fact]
    public void Body_cap_is_finite_and_far_above_any_real_payload()
    {
        var cap = GlDrive.Services.Control.ControlRequest.MaxBodyBytes;
        Assert.Equal(64 * 1024, cap);

        // The largest real body is a race start: two short strings plus JSON punctuation.
        var biggestReal = GlDrive.Services.Control.Endpoints.SpreadEndpoints.MaxSectionLength
                        + GlDrive.Services.Control.Endpoints.SpreadEndpoints.MaxReleaseLength + 64;
        Assert.True(cap > biggestReal * 10,
            "body cap should leave an order of magnitude over the largest legitimate payload");
    }

    // ---- the call sites actually enforce them ----------------------------------

    [Fact]
    public void StartRace_rejects_an_oversized_section_before_the_engine_or_the_log()
    {
        var src = ReadSource("src/GlDrive/Services/Control/Endpoints/SpreadEndpoints.cs");

        var sectionCheck = src.IndexOf("section!.Length > MaxSectionLength", StringComparison.Ordinal);
        var releaseCheck = src.IndexOf("release!.Length > MaxReleaseLength", StringComparison.Ordinal);
        var startRace = src.IndexOf("spread.StartRace(", StringComparison.Ordinal);
        var logLine = src.IndexOf("Control API started race", StringComparison.Ordinal);

        Assert.True(sectionCheck > 0, "section length must be validated");
        Assert.True(releaseCheck > 0, "release length must be validated");

        // Order is the whole point: an unbounded value must reach neither the race engine
        // nor the logger. Validating after either would leave the damage done.
        Assert.True(startRace > sectionCheck && startRace > releaseCheck,
            "length checks must precede StartRace");
        Assert.True(logLine > sectionCheck && logLine > releaseCheck,
            "length checks must precede the log write");
    }

    [Fact]
    public void An_oversized_body_is_refused_with_413_not_parsed_as_bad_json()
    {
        var src = ReadSource("src/GlDrive/Services/Control/Endpoints/SpreadEndpoints.cs");

        // A truncated read would surface as "invalid JSON body", which sends the caller
        // hunting a syntax error that is not there.
        Assert.Contains("body == null", src, StringComparison.Ordinal);
        Assert.Contains("413", src, StringComparison.Ordinal);
        Assert.Contains("payload_too_large", src, StringComparison.Ordinal);

        var nullCheck = src.IndexOf("body == null", StringComparison.Ordinal);
        var parse = src.IndexOf("JsonDocument.Parse", StringComparison.Ordinal);
        Assert.True(nullCheck < parse, "the size refusal must come before parsing");
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
        var src = ReadSource("src/GlDrive/Services/Control/Endpoints/SpreadEndpoints.cs");

        // Observed live: POST /races/totally-made-up/stop answered 200 {"stopped":"..."}
        // while GET /races/totally-made-up answered 404 for the same id.
        var existence = src.IndexOf("spread.ActiveJobs.All(j => j.Id != id)", StringComparison.Ordinal);
        var stopJob = src.IndexOf("spread.StopJob(id)", StringComparison.Ordinal);

        Assert.True(existence > 0, "stop must check the race exists");
        Assert.True(existence < stopJob, "the existence check must precede StopJob");

        // And it must report the same way the read endpoint does for an unknown id.
        var notFoundCount = CountOccurrences(src, "\"no such active race\"");
        Assert.True(notFoundCount >= 2,
            "GET /races/{id} and POST /races/{id}/stop must agree on what an unknown id is");
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
