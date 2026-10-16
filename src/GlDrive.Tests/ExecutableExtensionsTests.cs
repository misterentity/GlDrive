using System.IO;
using System.Text.RegularExpressions;
using GlDrive.Downloads;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// The torrent gate allows archives — it must, that is how releases ship — and cannot see
/// inside them. The extractor is where their contents become files, and on this machine the
/// watch folders (E:\movies, D:\x265) are the very folders torrent downloads are saved to.
/// So Movie.2024.rar containing Sample/setup.exe would bypass the whole torrent policy unless
/// the extractor applies the same rule.
///
/// These tests cover the shared definition and pin, structurally, that every extraction loop
/// consults it — guarding one of them would repeat the "invariant enforced at a single call
/// site" mistake this codebase has already shipped three times (CPSV desync, _watchAbandoned,
/// the abandon-store gate).
/// </summary>
public sealed class ExecutableExtensionsTests
{
    /// <summary>
    /// One definition, two consumers. If someone re-inlines a private copy into the torrent
    /// policy the two will drift, and the drift is silent.
    /// </summary>
    [Fact]
    public void TorrentPolicy_and_extractor_share_one_definition() =>
        Assert.Same(ExecutableExtensions.Default,
            GlDrive.Player.TorrentContentPolicy.DefaultBlockedExtensions);

    // ── Structural: every extraction loop must consult the set ────────────────────

    private static string Source(params string[] relativePath)
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (; dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(relativePath).ToArray());
            if (File.Exists(candidate))
                return Regex.Replace(File.ReadAllText(candidate), @"//[^\n]*", "");
        }

        throw new FileNotFoundException("not found: " + string.Join('/', relativePath));
    }

    [Fact]
    public void ArchiveExtractor_skips_executable_entries()
    {
        var code = Source("src", "GlDrive", "Downloads", "ArchiveExtractor.cs");
        Assert.Contains("ExecutableExtensions.IsExecutable", code);
    }

    /// <summary>
    /// ExtractorWindow has TWO independent extraction loops — the archive-mode one and the
    /// reader-mode one for zip/7z/tar. Both must check.
    /// </summary>
    [Fact]
    public void BothExtractorWindowLoops_skip_executable_entries()
    {
        var code = Source("src", "GlDrive", "UI", "ExtractorWindow.xaml.cs");
        var hits = Regex.Matches(code, @"ExecutableExtensions\.IsExecutable").Count;

        Assert.True(hits >= 2,
            $"Expected the check in both extraction loops; found {hits}. " +
            "Guarding one loop leaves the other as a bypass.");
    }
}
