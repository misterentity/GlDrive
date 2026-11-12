using System.Globalization;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

public class RequestAnnounceParserTests
{
    private const string Release = "Some.Show.S02E03.HDR.2160p.WEB.h265-GRP";
    private const string Requested = "REQUESTED by user0 : [ 3:] " + Release + " : Please try to help user0 out.";
    private const string Filled = Release + " was REQFILLED thanks to user1!";

    [Theory]
    [InlineData("3:")]
    [InlineData("3")]
    [InlineData(" 3 : ")]
    public void Parses_requested_line_with_optional_colon(string number)
    {
        var parser = new RequestAnnounceParser();

        var result = parser.Parse($"REQUESTED by user0 : [ {number}] {Release} : Please try to help user0 out.");

        Assert.Equal(new RequestEvent(RequestEventKind.Requested, Release, 3, "user0"), result);
    }

    [Fact]
    public void Parses_filled_sample()
    {
        Assert.Equal(new RequestEvent(RequestEventKind.Filled, Release, null, "user1"),
            new RequestAnnounceParser().Parse(Filled));
    }

    [Fact]
    public void Filled_line_does_not_require_nick()
    {
        Assert.Equal(new RequestEvent(RequestEventKind.Filled, Release, null, null),
            new RequestAnnounceParser().Parse(Release + " was REQFILLED"));
    }

    [Fact]
    public void Filled_pattern_is_tried_before_requested_pattern()
    {
        Assert.Equal(RequestEventKind.Filled,
            new RequestAnnounceParser().Parse(Requested + " " + Filled)!.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public void Empty_settings_use_defaults(string? setting)
    {
        var parser = new RequestAnnounceParser(setting, setting, setting);

        Assert.Equal(RequestEventKind.Requested, parser.Parse(Requested)!.Kind);
        Assert.Equal(RequestEventKind.Filled, parser.Parse(Filled)!.Kind);
        Assert.Equal("REQUEST-" + Release, parser.DirectoryNameFor(Release));
        Assert.Empty(parser.InvalidPatterns);
    }

    [Theory]
    [InlineData(RequestAnnounceParser.LegacyCommandPattern)]
    [InlineData(" \t" + RequestAnnounceParser.LegacyCommandPattern + " \t")]
    public void Legacy_requested_pattern_uses_default(string pattern)
    {
        var parser = new RequestAnnounceParser(requestedPattern: pattern);

        Assert.Equal(RequestEventKind.Requested, parser.Parse(Requested)!.Kind);
        Assert.Null(parser.Parse("!request " + Release));
        Assert.Empty(parser.InvalidPatterns);
    }

    [Fact]
    public void Legacy_pattern_comparison_is_ordinal()
    {
        var parser = new RequestAnnounceParser(requestedPattern: @"!REQUEST\s+(?<release>\S+)");

        Assert.Equal(Release, parser.Parse("!request " + Release)!.Release);
        Assert.Null(parser.Parse(Requested));
        Assert.Empty(parser.InvalidPatterns);
    }

    [Fact]
    public void Legacy_filled_pattern_is_a_custom_pattern()
    {
        var parser = new RequestAnnounceParser(filledPattern: RequestAnnounceParser.LegacyCommandPattern);

        Assert.Equal(new RequestEvent(RequestEventKind.Filled, Release, null, null),
            parser.Parse("!request " + Release));
        Assert.Empty(parser.InvalidPatterns);
    }

    [Theory]
    [InlineData("[")]
    [InlineData(@"REQUESTED (?<name>\S+)")]
    [InlineData(@"REQUESTED (?<Release>\S+)")]
    public void Invalid_requested_pattern_is_reported_and_uses_default(string pattern)
    {
        var parser = new RequestAnnounceParser(requestedPattern: pattern);

        Assert.Equal(pattern, Assert.Single(parser.InvalidPatterns));
        Assert.Equal(new RequestEvent(RequestEventKind.Requested, Release, 3, "user0"), parser.Parse(Requested));
    }

    [Theory]
    [InlineData("(")]
    [InlineData(@"(?<name>\S+) was REQFILLED")]
    public void Invalid_filled_pattern_is_reported_and_uses_default(string pattern)
    {
        var parser = new RequestAnnounceParser(filledPattern: pattern);

        Assert.Equal(pattern, Assert.Single(parser.InvalidPatterns));
        Assert.Equal(new RequestEvent(RequestEventKind.Filled, Release, null, "user1"), parser.Parse(Filled));
    }

    [Fact]
    public void Reports_both_invalid_patterns()
    {
        var parser = new RequestAnnounceParser("[", "(");

        Assert.Equal(new[] { "[", "(" }, parser.InvalidPatterns);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Custom_patterns_can_omit_number_and_nick(bool filled)
    {
        const string pattern = @"^sitea (?<release>\S+)$";
        var parser = filled ? new RequestAnnounceParser(filledPattern: pattern)
            : new RequestAnnounceParser(requestedPattern: pattern);

        Assert.Equal(new RequestEvent(filled ? RequestEventKind.Filled : RequestEventKind.Requested,
            Release, null, null), parser.Parse("sitea " + Release));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Custom_patterns_read_optional_metadata(bool filled)
    {
        const string pattern = @"^siteb (?<nick>\S+) (?<number>\d+) (?<release>\S+)$";
        var parser = filled ? new RequestAnnounceParser(filledPattern: pattern)
            : new RequestAnnounceParser(requestedPattern: pattern);

        Assert.Equal(new RequestEvent(filled ? RequestEventKind.Filled : RequestEventKind.Requested,
            Release, 7, "bot0"), parser.Parse("SITEB bot0 7 " + Release));
    }

    [Theory]
    [InlineData("")]
    [InlineData("user0")]
    [InlineData("2147483648")]
    public void Unparseable_number_is_null(string number)
    {
        var parser = new RequestAnnounceParser(@"^sitea (?<number>\S*) (?<release>\S+)(?: (?<nick>\S+))?$");

        var result = parser.Parse($"sitea {number} {Release}");

        Assert.NotNull(result);
        Assert.Null(result.Number);
        Assert.Null(result.Nick);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Case_insensitive_matching_is_culture_invariant(bool filled)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            const string pattern = @"^SITEA (?<release>\S+)$";
            var parser = filled ? new RequestAnnounceParser(filledPattern: pattern)
                : new RequestAnnounceParser(requestedPattern: pattern);

            Assert.Equal(Release, parser.Parse("sitea " + Release)!.Release);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Catastrophic_backtracking_returns_null(bool filled)
    {
        const string pattern = @"^(?<release>(a+)+)$";
        var parser = filled ? new RequestAnnounceParser(filledPattern: pattern)
            : new RequestAnnounceParser(requestedPattern: pattern);

        Assert.Empty(parser.InvalidPatterns);
        Assert.Null(parser.Parse(new string('a', 30_000) + "!"));
    }

    [Theory]
    [InlineData(",", "")]
    [InlineData(";", "")]
    [InlineData(":", "")]
    [InlineData("!", "")]
    [InlineData(",;:!", "")]
    [InlineData(".", ".")]
    [InlineData(".!", ".")]
    [InlineData(".,;:!", ".")]
    public void Trims_trailing_punctuation_but_preserves_period(string suffix, string expectedSuffix)
    {
        var parser = new RequestAnnounceParser();

        Assert.Equal(Release + expectedSuffix, parser.Parse($"REQUESTED by user0 : [3] {Release}{suffix}")!.Release);
        Assert.Equal(Release + expectedSuffix, parser.Parse($"{Release}{suffix} was REQFILLED")!.Release);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Abcd")]
    [InlineData(".Some.Show")]
    [InlineData("-Some.Show")]
    [InlineData("Some..Show")]
    [InlineData("Some/Show")]
    [InlineData("Some\\Show")]
    [InlineData("Some*Show")]
    [InlineData("Some?Show")]
    [InlineData("Some\"Show")]
    [InlineData("Some<Show")]
    [InlineData("Some>Show")]
    [InlineData("Some|Show")]
    [InlineData("Some Show")]
    [InlineData("Some\tShow")]
    [InlineData("Some\nShow")]
    [InlineData("Some\0Show")]
    [InlineData("Some\u007fShow")]
    [InlineData("Some\u00a0Show")]
    [InlineData("_____")]
    public void Rejects_implausible_releases(string? release)
    {
        var parser = new RequestAnnounceParser(@"^(?<release>[\s\S]*)$");

        Assert.False(RequestAnnounceParser.IsPlausibleRelease(release));
        Assert.Null(parser.Parse(release));
    }

    [Theory]
    [InlineData(4, false)]
    [InlineData(5, true)]
    [InlineData(200, true)]
    [InlineData(201, false)]
    public void Enforces_release_length_boundaries(int length, bool expected)
    {
        var release = new string('a', length);
        var parser = new RequestAnnounceParser(@"^(?<release>\S+)$");

        Assert.Equal(expected, RequestAnnounceParser.IsPlausibleRelease(release));
        Assert.Equal(expected, parser.Parse(release) is not null);
    }

    [Theory]
    [InlineData(Release)]
    [InlineData("12345")]
    [InlineData("____1")]
    [InlineData("_Some.Show")]
    [InlineData("Some.Show.")]
    public void Accepts_plausible_releases(string release)
    {
        Assert.True(RequestAnnounceParser.IsPlausibleRelease(release));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    [InlineData("unrelated text")]
    [InlineData("!request " + Release)]
    public void Unrecognized_text_returns_null(string? text)
    {
        Assert.Null(new RequestAnnounceParser().Parse(text));
    }

    [Theory]
    [InlineData("REQUEST-")]
    [InlineData("REQ-")]
    [InlineData(" REQUEST-")]
    public void Directory_name_round_trips_with_case_insensitive_prefix(string prefix)
    {
        var parser = new RequestAnnounceParser(directoryPrefix: prefix);

        Assert.Equal(prefix + Release, parser.DirectoryNameFor(Release));
        Assert.Equal(Release, parser.ReleaseFromDirectoryName(parser.DirectoryNameFor(Release)));
        Assert.Equal(Release, parser.ReleaseFromDirectoryName(prefix.ToLowerInvariant() + Release));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("REQUEST-")]
    [InlineData(Release)]
    [InlineData("FILLED-" + Release)]
    [InlineData("xREQUEST-" + Release)]
    [InlineData("REQUEST-Some/Show")]
    [InlineData("REQUEST-Some..Show")]
    [InlineData("REQUEST-Abcd")]
    [InlineData("REQUEST-.Some.Show")]
    public void Rejects_non_request_or_implausible_directory_names(string? name)
    {
        Assert.Null(new RequestAnnounceParser().ReleaseFromDirectoryName(name));
    }

    [Fact]
    public void Directory_name_for_concatenates_without_validation()
    {
        Assert.Equal("REQUEST-Abcd", new RequestAnnounceParser().DirectoryNameFor("Abcd"));
    }
}
