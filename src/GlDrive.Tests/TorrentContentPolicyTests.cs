using System.Globalization;
using System.Linq;
using GlDrive.Player;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Cover for the 2026-08-15 request: "do not allow it to download .exe files … or play".
///
/// Two independent controls live here and only one of them is about the request:
///
///  * The EXTENSION gate is hygiene, not security. GlDrive never executes downloaded
///    content; the realistic risk is the user browsing the save folder and double-clicking
///    something they took for the movie. It is default-on and overridable.
///  * The CONTAINMENT gate is a real fix and is NOT configurable. MonoTorrent 3.0.2's
///    PathValidator rejects leading "/", "X:", "/../", "\..\", and leading "..\" / "../" —
///    but NOT a leading "\" or "\\", and it only matches same-separator traversal so
///    "a/..\..\x" walks straight through. TorrentManager.SetMetadata then builds each path
///    with Path.Combine, which DISCARDS the left side when the right is rooted. The v2 file
///    tree loader never calls PathValidator at all. That is an arbitrary write outside the
///    save directory, and it exists whether or not the user cares about .exe files.
/// </summary>
public sealed class TorrentContentPolicyTests
{
    private const string Root = @"E:\movies";

    private static TorrentContentPolicy Blocking() => new(blockExecutables: true);
    private static TorrentContentPolicy NotBlocking() => new(blockExecutables: false);

    private static TorrentFileVerdict Verdict(TorrentContentPolicy p, string path) =>
        p.Evaluate(path, Root).Verdict;

    // ── Extension gate ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("evil.exe")]
    [InlineData("evil.EXE")]
    [InlineData("evil.eXe")]
    [InlineData("Movie.2024/setup.exe")]
    [InlineData("keygen.com")]
    [InlineData("install.msi")]
    [InlineData("run.bat")]
    [InlineData("run.cmd")]
    [InlineData("thing.vbs")]
    [InlineData("thing.js")]
    [InlineData("screensaver.scr")]
    [InlineData("shortcut.lnk")]
    [InlineData("script.ps1")]
    [InlineData("app.hta")]
    public void ExecutableExtensions_are_blocked(string path) =>
        Assert.Equal(TorrentFileVerdict.BlockedExtension, Verdict(Blocking(), path));

    /// <summary>
    /// Windows strips trailing dots and spaces when resolving a path, so "evil.exe." lands
    /// on disk as "evil.exe". Path.GetExtension("evil.exe.") returns "" and
    /// Path.GetExtension("evil.exe ") returns ".exe " — a naive check misses both.
    /// </summary>
    [Theory]
    [InlineData("evil.exe.")]
    [InlineData("evil.exe ")]
    [InlineData("evil.exe. . ")]
    [InlineData("evil.exe...")]
    public void TrailingDotsAndSpaces_do_not_evade(string path) =>
        Assert.Equal(TorrentFileVerdict.BlockedExtension, Verdict(Blocking(), path));

    /// <summary>Alternate-data-stream shape: the part before ':' is the real file.</summary>
    [Theory]
    [InlineData("payload.exe::$DATA")]
    [InlineData("payload.exe:stream")]
    public void AlternateDataStreamShape_does_not_evade(string path) =>
        Assert.Equal(TorrentFileVerdict.BlockedExtension, Verdict(Blocking(), path));

    /// <summary>
    /// A right-to-left override makes "holiday\u202Egpj.exe" RENDER as "holidayexe.jpg".
    /// The bytes still end in .exe, so the check already catches it — this pins that, and
    /// that DisplayName strips the control so logs show what is really there.
    /// </summary>
    [Fact]
    public void RightToLeftOverride_is_blocked_and_stripped_for_display()
    {
        const string sneaky = "holiday\u202Egpj.exe";

        Assert.Equal(TorrentFileVerdict.BlockedExtension, Verdict(Blocking(), sneaky));
        Assert.DoesNotContain('\u202E', TorrentContentPolicy.DisplayName(sneaky));
    }

    /// <summary>
    /// The single most important negative case. An EndsWith or Contains implementation
    /// would block this, and blocking a legitimately-named file is how a safety feature
    /// gets switched off wholesale.
    /// </summary>
    [Theory]
    [InlineData("readme.exe.txt")]
    [InlineData("how.to.install.exe.nfo")]
    [InlineData("execute.mkv")]
    [InlineData("exeter.1999.1080p.mkv")]
    public void NameContainingExe_but_not_ending_in_it_is_allowed(string path) =>
        Assert.Equal(TorrentFileVerdict.Allow, Verdict(Blocking(), path));

    [Theory]
    [InlineData("movie.mkv")]
    [InlineData("Sample/sample.mkv")]
    [InlineData("Subs/eng.srt")]
    [InlineData("release.nfo")]
    [InlineData("release.sfv")]
    [InlineData("disc.iso")]
    [InlineData("Movie.2024.part01.rar")]
    [InlineData("Movie.2024.r00")]
    [InlineData("no_extension_at_all")]
    public void LegitimateReleaseContent_is_allowed(string path) =>
        Assert.Equal(TorrentFileVerdict.Allow, Verdict(Blocking(), path));

    /// <summary>
    /// .iso stays allowed deliberately: Windows 11 propagates Mark-of-the-Web into mounted
    /// images since CVE-2022-41091, and DVDR/BDR releases are a normal use of this app —
    /// CleanupWindow already treats .iso as media.
    /// </summary>
    [Fact]
    public void IsoImages_are_not_treated_as_executable() =>
        Assert.Equal(TorrentFileVerdict.Allow, Verdict(Blocking(), "Movie.2024.DVDR/disc.iso"));

    // ── Containment gate (always on) ──────────────────────────────────────────────

    [Theory]
    [InlineData(@"\Windows\System32\pwn.cmd")]   // leading backslash — PathValidator misses this
    [InlineData(@"\\attacker\share\x.mkv")]      // UNC — PathValidator misses this
    [InlineData(@"C:\Windows\x.mkv")]
    [InlineData(@"..\..\x.mkv")]
    [InlineData("../../x.mkv")]
    [InlineData(@"a/..\..\x.mkv")]               // mixed separators — PathValidator misses this
    [InlineData("a/../../../x.mkv")]
    public void PathsEscapingTheSaveDirectory_are_rejected(string path) =>
        Assert.Equal(TorrentFileVerdict.EscapesSaveDirectory, Verdict(Blocking(), path));

    /// <summary>
    /// Containment is an invariant, not a preference. Turning the extension gate off must
    /// not turn off the arbitrary-write fix — that would make the config key a switch for
    /// the actual bug.
    /// </summary>
    [Fact]
    public void Containment_still_applies_when_extension_blocking_is_disabled()
    {
        Assert.Equal(TorrentFileVerdict.EscapesSaveDirectory,
            Verdict(NotBlocking(), @"\Windows\System32\pwn.cmd"));

        Assert.Equal(TorrentFileVerdict.Allow, Verdict(NotBlocking(), "evil.exe"));
    }

    [Theory]
    [InlineData("Movie.2024/Subs/eng.srt")]
    [InlineData("a/b/c/d/deep.mkv")]
    [InlineData("./movie.mkv")]
    public void PathsInsideTheSaveDirectory_are_allowed(string path) =>
        Assert.Equal(TorrentFileVerdict.Allow, Verdict(Blocking(), path));

    /// <summary>A sibling directory sharing a name prefix must not count as contained.</summary>
    [Fact]
    public void PrefixSiblingDirectory_is_not_contained() =>
        Assert.Equal(TorrentFileVerdict.EscapesSaveDirectory,
            Verdict(Blocking(), @"..\movies-evil\x.mkv"));

    // ── Configuration ─────────────────────────────────────────────────────────────

    [Fact]
    public void Disabled_policy_allows_executables() =>
        Assert.Equal(TorrentFileVerdict.Allow, Verdict(NotBlocking(), "setup.exe"));

    [Theory]
    [InlineData("jar")]
    [InlineData(".jar")]
    [InlineData("JAR")]
    public void ExtraBlockedExtensions_accepts_any_spelling(string configured)
    {
        var policy = new TorrentContentPolicy(true, extra: [configured]);
        Assert.Equal(TorrentFileVerdict.BlockedExtension, Verdict(policy, "minecraft.jar"));
    }

    [Fact]
    public void AllowedExtensionOverrides_removes_from_the_default_set()
    {
        var policy = new TorrentContentPolicy(true, allowOverrides: [".msi"]);

        Assert.Equal(TorrentFileVerdict.Allow, Verdict(policy, "installer.msi"));
        // Everything else in the set is untouched.
        Assert.Equal(TorrentFileVerdict.BlockedExtension, Verdict(policy, "installer.exe"));
    }

    /// <summary>
    /// The default set must cover everything Windows will run from Explorer via PATHEXT.
    /// Structural, so trimming the list later is a deliberate act rather than an oversight.
    /// </summary>
    [Theory]
    [InlineData(".com")] [InlineData(".exe")] [InlineData(".bat")] [InlineData(".cmd")]
    [InlineData(".vbs")] [InlineData(".vbe")] [InlineData(".js")]  [InlineData(".jse")]
    [InlineData(".wsf")] [InlineData(".wsh")] [InlineData(".msc")] [InlineData(".cpl")]
    public void DefaultSet_covers_every_PATHEXT_entry(string ext) =>
        Assert.Contains(ext, TorrentContentPolicy.DefaultBlockedExtensions);

    /// <summary>
    /// Guards against anyone "simplifying" the comparison to ToLower() or a
    /// CurrentCulture-sensitive compare. In Turkish, 'I'.ToLower() is 'ı' (dotless), so
    /// ".PIF" and ".MSI" would stop matching and the gate would silently open.
    /// </summary>
    [Fact]
    public void ExtensionMatching_survives_a_Turkish_culture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");

            Assert.Equal(TorrentFileVerdict.BlockedExtension, Verdict(Blocking(), "sneaky.PIF"));
            Assert.Equal(TorrentFileVerdict.BlockedExtension, Verdict(Blocking(), "sneaky.MSI"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    // ── Batch helper used by the service ──────────────────────────────────────────

    [Fact]
    public void EvaluateAll_reports_every_offending_entry()
    {
        var policy = Blocking();
        string[] files = ["movie.mkv", "setup.exe", "Subs/eng.srt", "keygen.com"];

        var decisions = policy.EvaluateAll(files, Root).ToList();

        Assert.Equal(4, decisions.Count);
        Assert.Equal(2, decisions.Count(d => d.Verdict == TorrentFileVerdict.BlockedExtension));
        Assert.Equal(2, decisions.Count(d => d.Verdict == TorrentFileVerdict.Allow));
    }

    [Fact]
    public void EmptyOrNullPaths_are_rejected_rather_than_allowed()
    {
        Assert.NotEqual(TorrentFileVerdict.Allow, Verdict(Blocking(), ""));
        Assert.NotEqual(TorrentFileVerdict.Allow, Verdict(Blocking(), "   "));
    }
}
