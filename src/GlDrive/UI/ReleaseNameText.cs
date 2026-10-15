using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace GlDrive.UI;

public enum ReleaseTokenKind { Title, Separator, Year, Resolution, Episode, Version, Source, Codec, Tag, Group, Other }

public readonly record struct ReleaseToken(string Text, ReleaseTokenKind Kind);

/// <summary>
/// Splits a scene release name into display tokens for colouring. Presentation only: the
/// tokens always concatenate back to the exact input, and nothing here feeds search, copy,
/// download identity or matching (those keep using the original string).
/// </summary>
public static class ReleaseNameTokenizer
{
    private static readonly HashSet<string> Sources = new(StringComparer.OrdinalIgnoreCase)
    {
        "BluRay", "BDRip", "BRRip", "BD", "WEB", "WEBRip", "WEBDL", "HDTV", "PDTV", "SDTV", "DVDRip", "DVD", "DVDR",
        "DVD9", "DVD5", "HDRip", "CAM", "TS", "TC", "SCREENER", "DVDScr", "REMUX", "AMZN", "NF", "DSNP", "HMAX",
        "ATVP", "HULU", "PCOK", "CD", "CDM", "CDS", "VINYL", "SAT", "DAB", "LINE", "UHD"
    };

    private static readonly HashSet<string> Codecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "x264", "x265", "H264", "H265", "HEVC", "AVC", "XviD", "DivX", "AV1", "VP9", "AAC", "AAC2", "AC3", "DD5", "DD2",
        "DDP5", "DDP2", "DDP", "EAC3", "DTS", "DTSHD", "DTS-HD", "TrueHD", "Atmos", "FLAC", "MP3", "OPUS", "OGG",
        "10bit", "8bit", "HDR", "HDR10", "HDR10Plus", "DV", "DoVi", "SDR", "HLG", "2CH", "6CH", "320", "V0"
    };

    private static readonly HashSet<string> Tags = new(StringComparer.OrdinalIgnoreCase)
    {
        "PROPER", "REPACK", "RERIP", "REAL", "MULTi", "DUAL", "iNTERNAL", "LIMITED", "UNRATED", "UNCUT", "EXTENDED",
        "DC", "DIRFIX", "NFOFIX", "SAMPLEFIX", "PROOFFIX", "SUBFIX", "SUBBED", "DUBBED", "READNFO", "COMPLETE",
        "FINAL", "RETAIL", "CRACKED", "REMASTERED", "RESTORED", "iMAX", "OAR", "WS", "FS"
    };

    // Tokens that are also ordinary title words; they only count as metadata after the title.
    private static readonly HashSet<string> TitleWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "TS", "TC", "CD", "DC", "WS", "FS", "BD", "NF", "DV", "SAT", "DAB", "LINE", "REAL", "FINAL", "COMPLETE",
        "DUAL", "LIMITED", "EXTENDED", "UNCUT", "RETAIL", "RESTORED", "CAM", "320", "V0"
    };

    public static IReadOnlyList<ReleaseToken> Tokenize(string? name)
    {
        if (string.IsNullOrEmpty(name)) return [];

        // Raw pieces: each separator character on its own, everything else as maximal word runs.
        var pieces = new List<(int Start, int Length, bool IsSeparator)>();
        int i = 0;
        while (i < name.Length)
        {
            if (IsSeparator(name[i])) { pieces.Add((i, 1, true)); i++; continue; }
            int start = i;
            while (i < name.Length && !IsSeparator(name[i])) i++;
            pieces.Add((start, i - start, false));
        }

        string Text(int p) => name.Substring(pieces[p].Start, pieces[p].Length);
        int groupPiece = FindGroupPiece(name, pieces, Text);

        var tokens = new List<ReleaseToken>(pieces.Count);
        bool inTitle = true;
        int wordIndex = 0;
        for (int p = 0; p < pieces.Count; p++)
        {
            var text = Text(p);
            if (pieces[p].IsSeparator) { tokens.Add(new(text, ReleaseTokenKind.Separator)); continue; }
            if (p == groupPiece) { tokens.Add(new(text, ReleaseTokenKind.Group)); continue; }

            var kind = Classify(text, wordIndex++, PreviousWord(p));
            // The first word, and short words that also occur in titles ("The.Final.Countdown"),
            // stay part of the title until a definite metadata token has been seen.
            if (inTitle && (wordIndex == 1 || TitleWords.Contains(text))) kind = ReleaseTokenKind.Other;
            if (kind == ReleaseTokenKind.Other) kind = inTitle ? ReleaseTokenKind.Title : ReleaseTokenKind.Other;
            else inTitle = false;
            tokens.Add(new(text, kind));
        }
        return tokens;

        string? PreviousWord(int p)
        {
            // "WEB-DL": DL is only a source when it is glued to WEB by a single hyphen.
            if (p >= 2 && pieces[p - 1].IsSeparator && name[pieces[p - 1].Start] == '-' && !pieces[p - 2].IsSeparator)
                return Text(p - 2);
            return null;
        }
    }

    private static int FindGroupPiece(string name, List<(int Start, int Length, bool IsSeparator)> pieces, Func<int, string> text)
    {
        // A trailing "-GROUP": the last piece is a word, the one before it a hyphen, and some word precedes that.
        int last = pieces.Count - 1;
        if (last < 2 || pieces[last].IsSeparator || !pieces[last - 1].IsSeparator || name[pieces[last - 1].Start] != '-')
            return -1;
        if (pieces[last - 2].IsSeparator) return -1;
        var group = text(last);
        if (group.Length is < 2 or > 32 || !group.All(char.IsAsciiLetterOrDigit)) return -1;
        if (group.Equals("DL", StringComparison.OrdinalIgnoreCase) && text(last - 2).Equals("WEB", StringComparison.OrdinalIgnoreCase))
            return -1;
        return last;
    }

    private static ReleaseTokenKind Classify(string word, int wordIndex, string? hyphenatedAfter)
    {
        if (hyphenatedAfter != null && word.Equals("DL", StringComparison.OrdinalIgnoreCase)
            && hyphenatedAfter.Equals("WEB", StringComparison.OrdinalIgnoreCase))
            return ReleaseTokenKind.Source;
        // A leading number is part of the title ("1917", "2012"), not its year.
        if (wordIndex > 0 && IsYear(word)) return ReleaseTokenKind.Year;
        if (IsResolution(word)) return ReleaseTokenKind.Resolution;
        if (IsEpisode(word)) return ReleaseTokenKind.Episode;
        if (IsVersion(word)) return ReleaseTokenKind.Version;
        if (Tags.Contains(word)) return ReleaseTokenKind.Tag;
        if (Sources.Contains(word)) return ReleaseTokenKind.Source;
        if (Codecs.Contains(word)) return ReleaseTokenKind.Codec;
        return ReleaseTokenKind.Other;
    }

    private static bool IsSeparator(char c) => c is '.' or '_' or '-' or ' ';

    private static bool IsYear(string w) =>
        w.Length == 4 && w.All(char.IsAsciiDigit) && (w.StartsWith("19", StringComparison.Ordinal) || w.StartsWith("20", StringComparison.Ordinal));

    private static bool IsResolution(string w)
    {
        if (w.Equals("4K", StringComparison.OrdinalIgnoreCase) || w.Equals("8K", StringComparison.OrdinalIgnoreCase)) return true;
        if (w.Length is < 4 or > 5) return false;
        char suffix = char.ToLowerInvariant(w[^1]);
        return (suffix == 'p' || suffix == 'i') && w[..^1].All(char.IsAsciiDigit);
    }

    private static bool IsEpisode(string w)
    {
        // S01, S01E02, S01E02E03, E05, 1x02
        int i = 0;
        if (w.Length >= 2 && (w[0] is 'S' or 's') && char.IsAsciiDigit(w[1]))
        {
            i = 1;
            int digits = Digits(w, ref i);
            if (digits is 0 or > 3) return false;
            while (i < w.Length)
            {
                if (w[i] is not ('E' or 'e')) return false;
                i++;
                digits = Digits(w, ref i);
                if (digits is 0 or > 4) return false;
            }
            return true;
        }
        if (w.Length >= 3 && (w[0] is 'E' or 'e'))
        {
            i = 1;
            return Digits(w, ref i) is >= 2 and <= 4 && i == w.Length;
        }
        int x = w.IndexOf('x');
        return x is 1 or 2 && x < w.Length - 2 && w[..x].All(char.IsAsciiDigit) && w[(x + 1)..].All(char.IsAsciiDigit) && w.Length - x - 1 <= 3;
    }

    private static bool IsVersion(string w) =>
        w.Length >= 2 && (w[0] is 'v' or 'V') && w[1..].All(char.IsAsciiDigit);

    private static int Digits(string w, ref int i)
    {
        int start = i;
        while (i < w.Length && char.IsAsciiDigit(w[i])) i++;
        return i - start;
    }

    internal static string? BrushKey(ReleaseTokenKind kind) => kind switch
    {
        ReleaseTokenKind.Title => null,
        ReleaseTokenKind.Separator => "ReleaseTokenSeparatorBrush",
        ReleaseTokenKind.Year or ReleaseTokenKind.Resolution or ReleaseTokenKind.Episode or ReleaseTokenKind.Version => "ReleaseTokenMetaBrush",
        ReleaseTokenKind.Codec => "ReleaseTokenCodecBrush",
        ReleaseTokenKind.Tag => "ReleaseTokenTagBrush",
        ReleaseTokenKind.Group => "ReleaseTokenGroupBrush",
        _ => "ReleaseTokenSourceBrush"
    };
}

/// <summary>
/// <c>ui:ReleaseNameText.Source="{Binding ReleaseName}"</c> fills a TextBlock with theme-coloured
/// runs. The TextBlock's text stays identical to the bound name, so trimming, tooltips,
/// automation names and copy keep working on the real string.
/// </summary>
public static class ReleaseNameText
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "Source", typeof(string), typeof(ReleaseNameText), new PropertyMetadata(null, OnSourceChanged));

    public static void SetSource(DependencyObject d, string? value) => d.SetValue(SourceProperty, value);
    public static string? GetSource(DependencyObject d) => (string?)d.GetValue(SourceProperty);

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        tb.Inlines.Clear();
        foreach (var token in ReleaseNameTokenizer.Tokenize(e.NewValue as string))
        {
            var run = new Run(token.Text);
            if (ReleaseNameTokenizer.BrushKey(token.Kind) is { } key)
                run.SetResourceReference(TextElement.ForegroundProperty, key);
            tb.Inlines.Add(run);
        }
    }
}
