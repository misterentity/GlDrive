using System.Text.Json;
using GlDrive.Plex;
using GlDrive.Services.Automation;
using Xunit;

namespace GlDrive.Tests;

public sealed class PlexAutomationTests
{
    private static readonly PlexLibrary[] Libraries =
    [
        new() { Key = "1", Title = "Movies", Type = "movie" },
        new() { Key = "2", Title = "TV", Type = "show" }
    ];

    [Fact]
    public void UnknownLibraryCannotCausePartialPermissionReplacement()
    {
        using var arguments = JsonDocument.Parse("{\"libraryTitles\":[\"Movies\",\"Typo\"]}");
        Assert.Throws<ArgumentException>(() => PlexAutomation.ValidateLibraryTitles(arguments.RootElement, Libraries));
    }

    [Fact]
    public void EmptyLibrarySelectionCannotAccidentallyShareAllLibraries()
    {
        using var arguments = JsonDocument.Parse("{\"libraryTitles\":[]}");
        Assert.Throws<ArgumentException>(() => PlexAutomation.ValidateLibraryTitles(arguments.RootElement, Libraries));
    }

    [Fact]
    public void UnresolvableSectionIdCannotCauseRevocationBeforeAnInvalidInvite()
    {
        using var arguments = JsonDocument.Parse("{\"libraryTitles\":[\"Movies\"]}");
        Assert.Throws<ArgumentException>(() => PlexAutomation.ValidateLibraryTitles(arguments.RootElement,
            [new PlexLibrary { Key = "invalid", Title = "Movies" }]));
    }

    [Theory]
    [InlineData("{\"libraryTitles\":[\"\"]}")]
    [InlineData("{\"libraryTitles\":[42]}")]
    [InlineData("{\"libraryTitles\":\"Movies\"}")]
    [InlineData("{}")]
    public void MalformedSelectionIsRejectedBeforeSharingChanges(string json)
    {
        using var arguments = JsonDocument.Parse(json);
        Assert.Throws<ArgumentException>(() => PlexAutomation.ValidateLibraryTitles(arguments.RootElement, Libraries));
    }

    [Fact]
    public void TitlesMatchPlexCaseInsensitiveSemanticsAndAreDeduplicated()
    {
        using var arguments = JsonDocument.Parse("{\"libraryTitles\":[\"movies\",\"MOVIES\",\"TV\"]}");
        Assert.Equal(["movies", "TV"], PlexAutomation.ValidateLibraryTitles(arguments.RootElement, Libraries));
    }
}
