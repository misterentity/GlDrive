using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using GlDrive.Plex;
using Xunit;

namespace GlDrive.Tests;

public sealed class PlexLibraryClientTests
{
    [Fact]
    public async Task Catalog_read_paginates_and_preserves_identity_and_media_observations()
    {
        using var handler = new Handler((request, count, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Contains("/library/sections/2/all?type=4&includeGuids=1", request.RequestUri!.ToString());
            Assert.Contains($"X-Plex-Container-Start={count - 1}", request.RequestUri.Query);
            Assert.Equal("synthetic-token", request.Headers.GetValues("X-Plex-Token").Single());
            Assert.DoesNotContain("synthetic-token", request.RequestUri.ToString());
            return Task.FromResult(Json(new
            {
                MediaContainer = new
                {
                    size = 1, totalSize = 2, offset = count - 1,
                    Metadata = new[]
                    {
                        new
                        {
                            ratingKey = count.ToString(), title = "Synthetic Episode", type = "episode", year = 2024,
                            grandparentTitle = "Synthetic Series", parentIndex = 3, index = count,
                            guid = "plex://episode/synthetic", Guid = new[] { new { id = $"tvdb://{count}" } },
                            Media = new[] { new { videoResolution = "1080", Part = new[] { new { file = $"/synthetic/series/episode{count}.mkv" } } } }
                        }
                    }
                }
            }));
        });
        using var client = Client(handler);
        var rows = await client.GetLibraryItemsAsync("http://plex.example.test:32400",
            new PlexLibrary { Key = "2", Type = "show" }, CancellationToken.None);

        Assert.Equal(2, handler.Count);
        Assert.Equal(2, rows.Count);
        Assert.Equal("Synthetic Series", rows[0].SeriesTitle);
        Assert.Equal(3, rows[0].Season);
        Assert.Equal(1, rows[0].Episode);
        Assert.Equal(2024, rows[0].Year);
        Assert.Contains("tvdb://1", rows[0].Guids);
        Assert.Equal("1080", Assert.Single(rows[0].Files).VideoResolution);
        Assert.Equal("/synthetic/series/episode1.mkv", rows[0].Files[0].Path);
    }

    [Fact]
    public async Task Short_page_without_total_does_not_truncate_catalog()
    {
        using var handler = new Handler((request, count, _) =>
        {
            Assert.Contains($"X-Plex-Container-Start={count - 1}", request.RequestUri!.Query);
            return Task.FromResult(Json(new
            {
                MediaContainer = new
                {
                    size = count < 3 ? 1 : 0, offset = count - 1,
                    Metadata = count < 3
                        ? new[] { new { ratingKey = count.ToString(), title = "Synthetic Film", type = "movie" } }
                        : []
                }
            }));
        });
        using var client = Client(handler);
        var rows = await client.GetLibraryItemsAsync("https://plex.example.test",
            new PlexLibrary { Key = "1", Type = "movie" }, CancellationToken.None);
        Assert.Equal(2, rows.Count);
        Assert.Equal(3, handler.Count);
    }

    [Fact]
    public async Task Catalog_item_without_media_is_observed_without_claiming_a_file()
    {
        using var handler = new Handler((_, _, _) => Task.FromResult(Json(new
        {
            MediaContainer = new
            {
                size = 1, totalSize = 1,
                Metadata = new[] { new { ratingKey = "41", title = "Synthetic Film", type = "movie" } }
            }
        })));
        using var client = Client(handler);
        var row = Assert.Single(await client.GetLibraryItemsAsync("https://plex.example.test",
            new PlexLibrary { Key = "1", Type = "movie" }, CancellationToken.None));
        Assert.Empty(row.Files);
        Assert.Empty(row.Guids);
        Assert.Null(row.Year);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"MediaContainer\":{\"size\":1,\"totalSize\":1}}")]
    [InlineData("{\"MediaContainer\":{\"size\":0,\"totalSize\":2}}")]
    [InlineData("{\"MediaContainer\":{\"size\":0,\"offset\":5}}")]
    public async Task Incomplete_catalog_is_an_error_instead_of_an_empty_ownership_claim(string body)
    {
        using var handler = new Handler((_, _, _) => Task.FromResult(Json(body)));
        using var client = Client(handler);
        await Assert.ThrowsAsync<PlexException>(() => client.GetLibraryItemsAsync("https://plex.example.test",
            new PlexLibrary { Key = "1", Type = "movie" }, CancellationToken.None));
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task Repeated_page_is_an_error_instead_of_an_infinite_or_partial_read()
    {
        using var handler = new Handler((_, _, _) => Task.FromResult(Json(new
        {
            MediaContainer = new
            {
                size = 1, totalSize = 2,
                Metadata = new[] { new { ratingKey = "41", title = "Synthetic Film", type = "movie" } }
            }
        })));
        using var client = Client(handler);
        await Assert.ThrowsAsync<PlexException>(() => client.GetLibraryItemsAsync("https://plex.example.test",
            new PlexLibrary { Key = "1", Type = "movie" }, CancellationToken.None));
        Assert.Equal(2, handler.Count);
    }

    [Fact]
    public async Task Targeted_scan_revalidates_advertised_root_and_posts_exact_folder_only()
    {
        const string folder = "/media/movies/Synthetic Film (2024)&part=one";
        using var handler = new Handler((request, count, _) =>
        {
            if (count == 1)
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("/library/sections", request.RequestUri!.AbsolutePath);
                return Task.FromResult(Sections("/media/movies"));
            }
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/library/sections/1/refresh", request.RequestUri!.AbsolutePath);
            Assert.Equal("?path=" + Uri.EscapeDataString(folder), request.RequestUri.Query);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
        });
        using var client = Client(handler);
        await client.ScanImportedFolderAsync("https://plex.example.test", 1, "/media/movies", folder, "movie", CancellationToken.None);
        Assert.Equal(2, handler.Count);
    }

    [Theory]
    [InlineData("/media/movies", "/media/movies")]
    [InlineData("/media/movies", "/media/movies-other/Synthetic")]
    [InlineData("/media/movies", "/media/movies/../Synthetic")]
    [InlineData("C:\\Library", "C:\\Library\\..\\Synthetic")]
    [InlineData("C:\\Library", "C:\\Library\\Synthetic. ")]
    public async Task Unsafe_or_full_root_scan_is_rejected_without_http(string root, string folder)
    {
        using var handler = new Handler((_, _, _) => throw new InvalidOperationException("Unexpected HTTP"));
        using var client = Client(handler);
        await Assert.ThrowsAsync<PlexException>(() => client.ScanImportedFolderAsync("https://plex.example.test", "1",
            root, folder, "movie", CancellationToken.None));
        Assert.Equal(0, handler.Count);
    }

    [Fact]
    public async Task Root_changed_or_unknown_stops_before_post_and_never_falls_back()
    {
        using var handler = new Handler((_, _, _) => Task.FromResult(Sections("/different/root")));
        using var client = Client(handler);
        await Assert.ThrowsAsync<PlexException>(() => client.ScanImportedFolderAsync("https://plex.example.test", "1",
            "/media/movies", "/media/movies/Synthetic", "movie", CancellationToken.None));
        Assert.Equal(1, handler.Count);
    }

    [Theory]
    [InlineData("movie", "show")]
    [InlineData("episode", "movie")]
    public async Task Imported_media_type_must_match_the_selected_section_before_post(string mediaKind, string sectionType)
    {
        var sections = new[] { new PlexLibrary { Key = "1", Type = sectionType, Roots = ["/media"] } };
        Assert.Null(PlexClient.FindImportedFolderTarget(sections, "/media/Synthetic", mediaKind));
        using var handler = new Handler((_, _, _) => Task.FromResult(Sections("/media", sectionType)));
        using var client = Client(handler);
        await Assert.ThrowsAsync<PlexException>(() => client.ScanImportedFolderAsync("https://plex.example.test", "1",
            "/media", "/media/Synthetic", mediaKind, CancellationToken.None));
        Assert.Equal(1, handler.Count);
    }

    [Theory]
    [InlineData("movie", "movie")]
    [InlineData("episode", "show")]
    public async Task Matching_media_type_is_required_at_target_selection_and_scan(string mediaKind, string sectionType)
    {
        var sections = new[]
        {
            new PlexLibrary { Key = "1", Type = sectionType, Roots = ["/media"] },
            new PlexLibrary { Key = "2", Type = sectionType == "movie" ? "show" : "movie", Roots = ["/media"] }
        };
        Assert.Equal(1, PlexClient.FindImportedFolderTarget(sections, "/media/Synthetic", mediaKind)!.Value.SectionId);
        using var handler = new Handler((_, count, _) => Task.FromResult(count == 1
            ? Sections("/media", sectionType) : new HttpResponseMessage(HttpStatusCode.Accepted)));
        using var client = Client(handler);
        await client.ScanImportedFolderAsync("https://plex.example.test", "1", "/media", "/media/Synthetic", mediaKind, CancellationToken.None);
        Assert.Equal(2, handler.Count);
    }

    [Fact]
    public async Task Failed_targeted_scan_does_not_retry_with_broader_scope()
    {
        using var handler = new Handler((_, count, _) => Task.FromResult(count == 1
            ? Sections("/media/movies") : new HttpResponseMessage(HttpStatusCode.BadRequest)));
        using var client = Client(handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.ScanImportedFolderAsync("https://plex.example.test", "1",
            "/media/movies", "/media/movies/Synthetic", "movie", CancellationToken.None));
        Assert.Equal(2, handler.Count);
    }

    [Fact]
    public async Task Cancellation_is_passed_to_transport_and_prevents_a_scan()
    {
        using var canceled = new CancellationTokenSource();
        using var handler = new Handler((_, _, ct) =>
        {
            canceled.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Sections("/media/movies"));
        });
        using var client = Client(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ScanImportedFolderAsync("https://plex.example.test", "1",
            "/media/movies", "/media/movies/Synthetic", "movie", canceled.Token));
        Assert.Equal(1, handler.Count);
    }

    [Theory]
    [InlineData("/media/movies", "/media/movies/Synthetic", true)]
    [InlineData("/media/movies", "/MEDIA/movies/Synthetic", false)]
    [InlineData("C:\\Library", "c:/library/Synthetic", true)]
    [InlineData("C:\\", "C:\\Synthetic", true)]
    [InlineData("\\\\server\\share", "\\\\SERVER\\share\\Synthetic", true)]
    [InlineData("/", "/Synthetic", true)]
    [InlineData("relative", "relative/Synthetic", false)]
    [InlineData("/media", "/media//Synthetic", false)]
    [InlineData("C:\\Library", "C:\\Library\\Synthetic:stream", false)]
    [InlineData("C:\\Library", "C:\\Library\\.\\Synthetic", false)]
    public void Root_containment_respects_server_path_semantics(string root, string folder, bool expected) =>
        Assert.Equal(expected, PlexClient.IsWithinPlexRoot(root, folder));

    [Fact]
    public void Target_lookup_requires_one_section_and_root_in_the_same_namespace()
    {
        var sections = new[] { new PlexLibrary { Key = "1", Type = "movie", Roots = ["C:\\Library"] } };
        var target = PlexClient.FindImportedFolderTarget(sections, "C:\\Library\\Synthetic", "movie");
        Assert.Equal(1, target!.Value.SectionId);
        Assert.Equal("C:\\Library", target.Value.Root);
        Assert.Null(PlexClient.FindImportedFolderTarget(sections, "/remote/Library/Synthetic", "movie"));
        sections[0].Roots.Add("C:\\Library\\Nested");
        Assert.Null(PlexClient.FindImportedFolderTarget(sections, "C:\\Library\\Nested\\Synthetic", "movie"));
    }

    [Fact]
    public async Task Sections_expose_roots_and_reject_truncated_section_lists()
    {
        using var handler = new Handler((_, count, _) => Task.FromResult(count == 1 ? Sections("/media/movies") :
            Json("{\"MediaContainer\":{\"size\":2,\"Directory\":[]}}")));
        using var client = Client(handler);
        var section = Assert.Single(await client.GetLibrarySectionsAsync("https://plex.example.test", CancellationToken.None));
        Assert.Equal("/media/movies", Assert.Single(section.Roots));
        await Assert.ThrowsAsync<PlexException>(() => client.GetLibrarySectionsAsync("https://plex.example.test", CancellationToken.None));
    }

    private static PlexClient Client(HttpMessageHandler handler) => new("synthetic-client", "GlDrive", "test", "synthetic-token", handler);

    private static HttpResponseMessage Sections(string root, string sectionType = "movie") => Json(new
    {
        MediaContainer = new { size = 1, Directory = new[] { new { key = "1", title = "Synthetic Library", type = sectionType, Location = new[] { new { path = root } } } } }
    });

    private static HttpResponseMessage Json(object body) => Json(JsonSerializer.Serialize(body));
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, ++Count, cancellationToken);
    }
}
