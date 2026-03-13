using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace GlDrive.UI;

public static partial class ReleaseTextHelper
{
    // Scene release pattern: at least 2 dot-separated words ending with -GROUP
    // Examples: Movie.Name.2024.1080p.BluRay.x264-GROUP, Show.S01E02.720p-GROUP
    private static readonly Regex ReleaseRegex = MyReleaseRegex();

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

        var matches = ReleaseRegex.Matches(text);
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

    // Scene release: letter-starting, dot/underscore-separated tokens, ending with -GROUP (3-20 chars)
    // Allows hyphens within name (WEB-DL, x264-hi10p) — group is always after the last hyphen
    [GeneratedRegex(@"(?<![.\w])[A-Za-z][\w-]*(?:[._][A-Za-z0-9][\w-]*){1,}[._]?-[A-Za-z0-9]{3,20}(?![.\w-])")]
    private static partial Regex MyReleaseRegex();
}
