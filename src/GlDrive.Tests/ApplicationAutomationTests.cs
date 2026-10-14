using System.IO;
using System.Text.Json;
using GlDrive.Services.Automation;
using Xunit;

namespace GlDrive.Tests;

public sealed class ApplicationAutomationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-cleanup-test-" + Guid.NewGuid().ToString("N"));
    public ApplicationAutomationTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Cleanup_only_removes_selected_archives_next_to_media()
    {
        var archive = Path.Combine(_root, "movie.rar");
        var media = Path.Combine(_root, "movie.mkv");
        var unrelated = Path.Combine(_root, "keep.txt");
        File.WriteAllText(archive, "synthetic archive");
        File.WriteAllText(media, "synthetic media");
        File.WriteAllText(unrelated, "keep");
        var module = new ApplicationAutomation(() => throw new InvalidOperationException());
        await Assert.ThrowsAsync<ArgumentException>(() => module.ExecuteAsync("cleanup.delete",
            JsonSerializer.SerializeToElement(new { root = _root, files = new[] { media } }), default));
        Assert.True(File.Exists(media)); Assert.True(File.Exists(archive));
        var result = JsonSerializer.SerializeToElement(await module.ExecuteAsync("cleanup.delete",
            JsonSerializer.SerializeToElement(new { root = _root, files = new[] { archive } }), default));
        Assert.Single(result.GetProperty("deleted").EnumerateArray());
        Assert.False(File.Exists(archive)); Assert.True(File.Exists(media)); Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public void Cleanup_rejects_sibling_with_matching_prefix()
    {
        var outside = _root + "-outside.rar";
        File.WriteAllText(outside, "keep");
        try { Assert.False(ApplicationAutomation.IsContainedRegularFile(_root, outside)); }
        finally { File.Delete(outside); }
    }

    [Fact]
    public async Task Cleanup_requires_media_before_an_archive_is_eligible()
    {
        var archive = Path.Combine(_root, "movie.rar"); File.WriteAllText(archive, "keep");
        var module = new ApplicationAutomation(() => throw new InvalidOperationException());
        await Assert.ThrowsAsync<ArgumentException>(() => module.ExecuteAsync("cleanup.delete",
            JsonSerializer.SerializeToElement(new { root = _root, files = new[] { archive } }), default));
        Assert.True(File.Exists(archive));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
