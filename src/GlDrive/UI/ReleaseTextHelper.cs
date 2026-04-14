using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace GlDrive.UI;

public static partial class ReleaseTextHelper
{
    // Dot-separated releases: Movie.Name.2024.1080p.BluRay.x264-GROUP
    private static readonly Regex DotReleaseRegex = MyDotReleaseRegex();
    // Music releases (hyphen-separated with underscores): Artist_Name-Album-WEB-2024-GROUP
    private static readonly Regex MusicReleaseRegex = MyMusicReleaseRegex();

    public static readonly DependencyProperty MessageProperty =
        DependencyProperty.RegisterAttached("Message", typeof(IrcMessageVm), typeof(ReleaseTextHelper),
            new PropertyMetadata(null, OnMessageChanged));

    public static readonly DependencyProperty ClickCommandProperty =
        DependencyProperty.RegisterAttached("ClickCommand", typeof(ICommand), typeof(ReleaseTextHelper),
            new PropertyMetadata(null));

    public static void SetMessage(DependencyObject d, IrcMessageVm? value) => d.SetValue(MessageProperty, value);
    public static IrcMessageVm? GetMessage(DependencyObject d) => (IrcMessageVm?)d.GetValue(MessageProperty);

    public static void SetClickCommand(DependencyObject d, ICommand? value) => d.SetValue(ClickCommandProperty, value);
    public static ICommand? GetClickCommand(DependencyObject d) => (ICommand?)d.GetValue(ClickCommandProperty);

    private static void OnMessageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        var msg = e.NewValue as IrcMessageVm;
        if (msg == null) { tb.Inlines.Clear(); return; }

        tb.Inlines.Clear();

        var text = msg.FormattedLine;

        // System messages don't have releases
        if (msg.IsSystemMessage)
        {
            tb.Inlines.Add(new Run(text));
            return;
        }

        var matches = MergeMatches(DotReleaseRegex.Matches(text), MusicReleaseRegex.Matches(text));
        if (matches.Count == 0)
        {
            tb.Inlines.Add(new Run(text));
            return;
        }

        var lastIndex = 0;
        foreach (Match match in matches)
        {
            // Add text before match
            if (match.Index > lastIndex)
                tb.Inlines.Add(new Run(text[lastIndex..match.Index]));

            var releaseName = match.Value;
            var link = new Hyperlink(new Run(releaseName))
            {
                TextDecorations = null,
                Cursor = Cursors.Hand
            };
            // Use accent-ish color for links
            link.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
            link.MouseEnter += (s, _) =>
            {
                if (s is Hyperlink h) h.TextDecorations = TextDecorations.Underline;
            };
            link.MouseLeave += (s, _) =>
            {
                if (s is Hyperlink h) h.TextDecorations = null;
            };
            link.Click += (_, _) =>
            {
                var cmd = GetClickCommand(tb);
                if (cmd?.CanExecute(releaseName) == true)
                    cmd.Execute(releaseName);
            };

            tb.Inlines.Add(link);
            lastIndex = match.Index + match.Length;
        }

        // Add remaining text after last match
        if (lastIndex < text.Length)
            tb.Inlines.Add(new Run(text[lastIndex..]));
    }

    // Merge matches from both regexes, deduplicating overlapping spans
    private static List<Match> MergeMatches(MatchCollection a, MatchCollection b)
    {
        var all = new List<Match>();
        foreach (Match m in a) all.Add(m);
        foreach (Match m in b) all.Add(m);
        if (all.Count == 0) return all;

        all.Sort((x, y) => x.Index.CompareTo(y.Index));

        // Remove overlapping matches (keep the longer one)
        var result = new List<Match> { all[0] };
        for (int i = 1; i < all.Count; i++)
        {
            var prev = result[^1];
            var curr = all[i];
            if (curr.Index < prev.Index + prev.Length)
            {
                // Overlapping — keep the longer match
                if (curr.Length > prev.Length)
                    result[^1] = curr;
            }
            else
            {
                result.Add(curr);
            }
        }
        return result;
    }

    // Dot-separated: Movie.Name.2024.1080p-GROUP, Show.S01E02.WEB-DL-GRP
    [GeneratedRegex(@"(?<![.\w])[A-Za-z][\w-]*(?:[._][A-Za-z0-9][\w-]*){1,}[._]?-[A-Za-z0-9]{3,20}(?![.\w-])")]
    private static partial Regex MyDotReleaseRegex();

    // Music: hyphen-separated tokens with underscores — Artist_Name-Album-WEB-2024-GRP
    [GeneratedRegex(@"(?<![.\w])(?=[^\s]*_)[A-Za-z0-9][\w()]*(?:-[A-Za-z0-9][\w()]*){2,}-[A-Za-z0-9]{2,20}(?![.\w-])")]
    private static partial Regex MyMusicReleaseRegex();
}
