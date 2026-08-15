using System.IO;

namespace GlDrive.Player;

/// <summary>What should happen to one file declared inside a torrent.</summary>
public enum TorrentFileVerdict
{
    /// <summary>Download it.</summary>
    Allow,

    /// <summary>Its extension is one Windows would execute from Explorer.</summary>
    BlockedExtension,

    /// <summary>Its path resolves outside the save directory. Never allowed, never configurable.</summary>
    EscapesSaveDirectory,
}

/// <summary>One file's admission decision, with a reason fit for a log line or a dialog.</summary>
public readonly record struct TorrentFileDecision(
    string TorrentPath,
    TorrentFileVerdict Verdict,
    string Detail)
{
    public bool IsBlocked => Verdict != TorrentFileVerdict.Allow;
}

/// <summary>
/// Decides which files inside a torrent GlDrive is willing to write to disk.
///
/// Requested 2026-08-15: "do not allow it to download .exe files … or play". Two separate
/// controls ended up living here, and it matters that they are not confused:
///
/// 1. THE EXTENSION GATE — what was asked for. This is hygiene, not a security boundary.
///    GlDrive never executes downloaded content; every Process.Start in the player targets a
///    fixed binary or a folder. The realistic risk is the user browsing the save folder in
///    Explorer and double-clicking something they took for the film. Default on, overridable,
///    and it must never be described as making a download safe.
///
/// 2. THE CONTAINMENT GATE — not asked for, and the more serious of the two. MonoTorrent
///    3.0.2's PathValidator rejects a leading "/", a "X:" drive prefix, "/../", "\..\" and a
///    leading "..\" or "../" — but NOT a leading "\" or "\\", and it only matches
///    same-separator traversal, so "a/..\..\x" passes. TorrentManager.SetMetadata then builds
///    each file path with Path.Combine, which DISCARDS the left operand when the right is
///    rooted, and the library's only containment assertion is about the containing DIRECTORY,
///    never the files. The v2 file-tree loader does not call PathValidator at all. Net effect:
///    a malicious torrent can name a file "\Windows\System32\x.cmd" and have it written
///    outside the chosen folder. That is an arbitrary file write, it exists independently of
///    anyone caring about .exe files, and so this half has no config key.
///
/// Why the blocked set goes beyond literal ".exe": blocking one extension is not a smaller
/// control, it is the same control with a hole the attacker picks by typing three different
/// letters. ".scr" is the classic media-adjacent disguise, and ".cmd"/".vbs"/".js" are all in
/// PATHEXT. Since the premise is "the user misidentifies a file in a folder", restricting to a
/// single extension defeats the premise. The one real false positive — a software torrent
/// carrying setup.exe — is handled by the per-download override, not by shortening the list.
///
/// Deliberately NOT blocked, each for a reason:
///   .iso/.img  — Windows 11 propagates Mark-of-the-Web into mounted images (CVE-2022-41091),
///                and DVDR/BDR releases are a normal use of this app.
///   .dll/.sys  — the attacker cannot choose the destination, nothing writes to the program
///                directory, and games legitimately ship DLLs. Real false positives, no gain.
///   .jar       — needs a JRE and appears in legitimate mod torrents. Use extraBlockedExtensions.
///   .reg       — a double-click raises UAC *and* regedit's own warning. Two prompts, so it
///                fails the "at most one confirmation" bar this list is built on.
/// </summary>
public sealed class TorrentContentPolicy
{
    /// <summary>
    /// Extensions Windows will run from Explorer with at most one confirmation, and which do
    /// not plausibly appear in a legitimate release.
    /// </summary>
    /// <summary>
    /// Shared with the archive extractor via <see cref="Downloads.ExecutableExtensions"/> — the
    /// two must not drift, because a torrent-delivered .rar unpacked by a watch folder would
    /// otherwise bypass this gate entirely.
    /// </summary>
    public static IReadOnlySet<string> DefaultBlockedExtensions => Downloads.ExecutableExtensions.Default;

    // Bidi and zero-width controls. Stripped for DISPLAY only — the raw bytes are what decide
    // the verdict, so a name is never judged by how it happens to render.
    private static readonly char[] FormatControls =
    [
        '​', '‌', '‍', '‎', '‏',
        '‪', '‫', '‬', '‭', '‮',
        '⁦', '⁧', '⁨', '⁩', '﻿',
    ];

    private readonly bool _blockExecutables;
    private readonly HashSet<string> _blocked;

    public TorrentContentPolicy(Config.TorrentConfig cfg)
        : this(cfg.BlockExecutables, cfg.ExtraBlockedExtensions, cfg.AllowedExtensionOverrides)
    {
    }

    public TorrentContentPolicy(
        bool blockExecutables,
        IEnumerable<string>? extra = null,
        IEnumerable<string>? allowOverrides = null)
    {
        _blockExecutables = blockExecutables;
        _blocked = new HashSet<string>(DefaultBlockedExtensions, StringComparer.OrdinalIgnoreCase);

        foreach (var e in extra ?? [])
            if (Normalize(e) is { } n) _blocked.Add(n);

        foreach (var e in allowOverrides ?? [])
            if (Normalize(e) is { } n) _blocked.Remove(n);

        static string? Normalize(string raw)
        {
            var t = raw?.Trim();
            if (string.IsNullOrEmpty(t)) return null;
            return t.StartsWith('.') ? t.ToLowerInvariant() : "." + t.ToLowerInvariant();
        }
    }

    /// <summary>
    /// Judge one torrent-declared path. <paramref name="saveRootFullPath"/> must already be
    /// an absolute path. Containment is checked first and is never gated on configuration.
    /// </summary>
    public TorrentFileDecision Evaluate(string torrentPath, string saveRootFullPath)
    {
        if (string.IsNullOrWhiteSpace(torrentPath))
            return new(torrentPath ?? "", TorrentFileVerdict.EscapesSaveDirectory,
                "empty path");

        if (!IsContained(torrentPath, saveRootFullPath, out var why))
            return new(torrentPath, TorrentFileVerdict.EscapesSaveDirectory, why);

        if (_blockExecutables)
        {
            foreach (var ext in ExtensionCandidates(torrentPath))
                if (_blocked.Contains(ext))
                    return new(torrentPath, TorrentFileVerdict.BlockedExtension,
                        $"executable extension {ext}");
        }

        return new(torrentPath, TorrentFileVerdict.Allow, "");
    }

    /// <summary>Judge a whole torrent's file list in declaration order.</summary>
    public IEnumerable<TorrentFileDecision> EvaluateAll(
        IEnumerable<string> torrentPaths, string saveRootFullPath) =>
        torrentPaths.Select(p => Evaluate(p, saveRootFullPath));

    /// <summary>
    /// Every spelling of the leaf's extension that Windows might end up honouring. All are
    /// tested and any match blocks, so the check fails closed rather than betting on which
    /// normalisation the filesystem will apply.
    /// </summary>
    internal static IEnumerable<string> ExtensionCandidates(string torrentPath)
    {
        // LastIndexOfAny, not Path.GetFileName: GetFileName also splits on ':' on Windows,
        // which would silently discard the ADS case this is trying to catch.
        var cut = torrentPath.LastIndexOfAny(['/', '\\']);
        var leaf = cut >= 0 ? torrentPath[(cut + 1)..] : torrentPath;

        var forms = new List<string>(3) { leaf };

        var stripped = new string(leaf.Where(c => !FormatControls.Contains(c)).ToArray());
        if (!string.Equals(stripped, leaf, StringComparison.Ordinal)) forms.Add(stripped);

        var colon = leaf.IndexOf(':');
        if (colon > 0) forms.Add(leaf[..colon]);

        foreach (var form in forms)
        {
            // Win32 drops trailing dots and spaces when resolving a name, so "evil.exe." is
            // created on disk as "evil.exe". Trim BEFORE looking for the extension.
            var t = form.TrimEnd('.', ' ');
            if (t.Length == 0) continue;

            var dot = t.LastIndexOf('.');
            if (dot < 0) continue;

            yield return t[dot..].ToLowerInvariant();
        }
    }

    /// <summary>Leaf name with bidi/zero-width controls removed. For logs and UI only.</summary>
    public static string DisplayName(string torrentPath)
    {
        if (string.IsNullOrEmpty(torrentPath)) return "";

        var cut = torrentPath.LastIndexOfAny(['/', '\\']);
        var leaf = cut >= 0 ? torrentPath[(cut + 1)..] : torrentPath;

        return new string(leaf.Where(c => !FormatControls.Contains(c)).ToArray());
    }

    /// <summary>
    /// True when the declared path resolves inside the save root. Evaluated against the RAW
    /// torrent path, which is conservative-correct: MonoTorrent's escaping only ever lengthens
    /// a segment (':' becomes "_3a_"), so it can never introduce a separator or a root that
    /// this did not already see.
    /// </summary>
    private static bool IsContained(string torrentPath, string saveRootFullPath, out string why)
    {
        why = "";

        try
        {
            if (torrentPath.Contains('\0')) { why = "embedded null"; return false; }

            var p = torrentPath.Replace('/', Path.DirectorySeparatorChar);

            // A leading "\" or "\\" is exactly what MonoTorrent's PathValidator fails to
            // reject, and Path.Combine will honour it by discarding the save directory.
            if (p.StartsWith(Path.DirectorySeparatorChar) || p.StartsWith(@"\\", StringComparison.Ordinal))
            {
                why = "rooted or UNC path";
                return false;
            }

            if (Path.IsPathRooted(p)) { why = "absolute path"; return false; }

            foreach (var segment in p.Split(Path.DirectorySeparatorChar))
                if (segment == "..") { why = "parent-directory traversal"; return false; }

            var root = Path.GetFullPath(saveRootFullPath);
            if (!root.EndsWith(Path.DirectorySeparatorChar)) root += Path.DirectorySeparatorChar;

            var full = Path.GetFullPath(Path.Combine(root, p));

            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                why = "resolves outside the save folder";
                return false;
            }

            return true;
        }
        catch (Exception)
        {
            // A path we cannot even resolve is not one we should write. Fail closed.
            why = "unresolvable path";
            return false;
        }
    }
}
