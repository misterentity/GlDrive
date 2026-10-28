using System.Text.RegularExpressions;

namespace GlDrive.Downloads;

public enum ReleaseSource { Unknown, Cam, Telesync, Telecine, Dvd, Broadcast, WebRip, Web, BluRay }
public enum ReleaseCodec { Unknown, H264, H265, X264, X265, Av1, Xvid }
public enum ReleaseHdr { Sdr, Hdr, Hdr10Plus, Hlg, DolbyVision }

/// <summary>Name-derived hints only: the tokens do not prove the contents of a release.</summary>
public sealed record ReleaseTraits(
    QualityProfile Resolution, ReleaseSource Source, ReleaseCodec Codec, ReleaseHdr Hdr,
    bool Remux, bool FullDisc, string? Edition, bool Proper, int RepackRevision, string? Group)
{
    public bool Untouched => Remux || Codec is ReleaseCodec.H264 or ReleaseCodec.H265;
    public bool LowQualitySource => Source is ReleaseSource.Cam or ReleaseSource.Telesync or ReleaseSource.Telecine;

    public static ReleaseTraits Parse(string releaseName)
    {
        ArgumentNullException.ThrowIfNull(releaseName);
        // Remove the group before inspecting quality tokens: a group named CAM is not a CAM source.
        var groupMatch = Regex.Match(releaseName, @"-([a-z0-9]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(50));
        var name = groupMatch.Success ? releaseName[..groupMatch.Index] : releaseName;
        // A reliable year/episode boundary separates title words from format tokens.
        // Use the last year so titles beginning with a year do not hide their actual release year.
        var boundaries = Regex.Matches(name, @"(?:^|[. _-])(?:(?:19|20)[0-9]{2}|S[0-9]{1,2}(?:E[0-9]{1,3})*)(?:$|[. _-])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
            TimeSpan.FromMilliseconds(50));
        if (boundaries.Count > 0)
        {
            var boundary = boundaries[^1];
            name = name[(boundary.Index + boundary.Length)..];
        }
        bool Has(string expression) => Regex.IsMatch(name, @"(?:^|[. _-])(?:" + expression + @")(?:$|[. _-])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
            TimeSpan.FromMilliseconds(50));

        var resolution = Has("2160p|4k|uhd") ? QualityProfile.Q2160p :
            Has("1080p|1080i") ? QualityProfile.Q1080p : Has("720p") ? QualityProfile.Q720p :
            Has("480p|576p|sdtv") ? QualityProfile.SD : QualityProfile.Any;
        // Bad acquisition sources take precedence over attractive but contradictory tokens.
        var source = Has("CAM|HDCAM") ? ReleaseSource.Cam : Has("TS|HDTS|TELESYNC") ? ReleaseSource.Telesync :
            Has("TC|HDTC|TELECINE") ? ReleaseSource.Telecine : Has("BLU[. _-]?RAY|BDREMUX") ? ReleaseSource.BluRay :
            Has("WEB[. _-]?RIP") ? ReleaseSource.WebRip : Has("WEB(?:[. _-]?DL)?") ? ReleaseSource.Web :
            Has("HDTV|PDTV") ? ReleaseSource.Broadcast : Has("BDRIP|BRRIP|DVDRIP|DVD|DVDSCR|SCREENER") ? ReleaseSource.Dvd :
            ReleaseSource.Unknown;
        var codec = Has("X264") ? ReleaseCodec.X264 : Has("X265") ? ReleaseCodec.X265 :
            Has("H[. _-]?264|AVC") ? ReleaseCodec.H264 : Has("H[. _-]?265|HEVC") ? ReleaseCodec.H265 :
            Has("AV1") ? ReleaseCodec.Av1 : Has("XVID|DIVX") ? ReleaseCodec.Xvid : ReleaseCodec.Unknown;
        var hdr = Has("DV|DOVI|DOLBY[. _-]?VISION") ? ReleaseHdr.DolbyVision :
            Has(@"HDR10\+|HDR10PLUS") ? ReleaseHdr.Hdr10Plus : Has("HLG") ? ReleaseHdr.Hlg :
            Has("HDR|HDR10") ? ReleaseHdr.Hdr : ReleaseHdr.Sdr;
        var edition = new[] { "UNRATED", "EXTENDED", "DC", "REMASTERED", "IMAX", "THEATRICAL" }
            .FirstOrDefault(Has);
        var repack = Regex.Match(name, @"(?:^|[. _-])(?:REPACK|RERIP)([1-9][0-9]?)?(?:$|[. _-])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
            TimeSpan.FromMilliseconds(50));
        var revision = repack.Success ? (repack.Groups[1].Success ? int.Parse(repack.Groups[1].Value) : 1) : 0;
        return new ReleaseTraits(resolution, source, codec, hdr, Has("REMUX|BDREMUX"),
            Has("BDMV|BDISO|DVDISO|FULL[. _-]?BLU[. _-]?RAY") ||
            (Has("COMPLETE") && Has("BLU[. _-]?RAY|DVD")), edition, Has("PROPER"), revision,
            groupMatch.Success ? groupMatch.Groups[1].Value : null);
    }
}
