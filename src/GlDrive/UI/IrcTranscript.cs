using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace GlDrive.UI;

/// <summary>
/// Renders the IRC message collection into a read-only RichTextBox, one paragraph per
/// line, so text can be drag-selected across lines and copied like a normal IRC client.
/// Follows the newest line while the view is at the bottom; scrolling up (or holding a
/// selection) stops the follow until the view is back at the bottom.
/// </summary>
public static class IrcTranscript
{
    private const double BottomSlackPx = 24;

    public static readonly DependencyProperty MessagesProperty =
        DependencyProperty.RegisterAttached("Messages", typeof(ObservableCollection<IrcMessageVm>), typeof(IrcTranscript),
            new PropertyMetadata(null, OnMessagesChanged));

    public static readonly DependencyProperty LinkCommandProperty =
        DependencyProperty.RegisterAttached("LinkCommand", typeof(ICommand), typeof(IrcTranscript), new PropertyMetadata(null));

    private static readonly DependencyProperty StateProperty =
        DependencyProperty.RegisterAttached("State", typeof(State), typeof(IrcTranscript), new PropertyMetadata(null));

    public static void SetMessages(DependencyObject d, ObservableCollection<IrcMessageVm>? value) => d.SetValue(MessagesProperty, value);
    public static ObservableCollection<IrcMessageVm>? GetMessages(DependencyObject d) => (ObservableCollection<IrcMessageVm>?)d.GetValue(MessagesProperty);
    public static void SetLinkCommand(DependencyObject d, ICommand? value) => d.SetValue(LinkCommandProperty, value);
    public static ICommand? GetLinkCommand(DependencyObject d) => (ICommand?)d.GetValue(LinkCommandProperty);

    private sealed class State
    {
        public ObservableCollection<IrcMessageVm>? Source;
        public NotifyCollectionChangedEventHandler? Handler;
        public bool FollowBottom = true;
    }

    /// <summary>Scrolls to the newest line unless the reader has scrolled up or is selecting.</summary>
    public static void ScrollToEndIfFollowing(RichTextBox box)
    {
        var state = (State?)box.GetValue(StateProperty);
        if (state is { FollowBottom: true } && box.Selection.IsEmpty)
            box.ScrollToEnd();
    }

    /// <summary>The message line under a point in the box, or null.</summary>
    public static (string Line, string? Release)? HitTest(RichTextBox box, Point point)
    {
        var position = box.GetPositionFromPoint(point, snapToText: true);
        if (position?.Paragraph is not { } paragraph) return null;
        string? release = null;
        for (var element = position.Parent as TextElement; element != null && element != paragraph; element = element.Parent as TextElement)
        {
            if (element is Hyperlink { Tag: string name }) { release = name; break; }
        }
        return (new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text, release);
    }

    private static void OnMessagesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not RichTextBox box) return;
        var state = (State?)box.GetValue(StateProperty);
        if (state == null)
        {
            state = new State();
            box.SetValue(StateProperty, state);
            box.Document = new FlowDocument
            {
                PagePadding = new Thickness(4, 2, 4, 2),
                FontFamily = box.FontFamily,
                FontSize = box.FontSize,
            };
            box.Document.SetResourceReference(FlowDocument.ForegroundProperty, "ForegroundBrush");
            box.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, args) => OnScrollChanged(box, state, args)));
        }

        if (state.Source != null && state.Handler != null)
            state.Source.CollectionChanged -= state.Handler;
        state.Source = e.NewValue as ObservableCollection<IrcMessageVm>;
        if (state.Source != null)
        {
            state.Handler = (_, args) => OnCollectionChanged(box, state, args);
            state.Source.CollectionChanged += state.Handler;
        }
        Rebuild(box, state);
    }

    private static void OnScrollChanged(RichTextBox box, State state, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0)
            state.FollowBottom = e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - BottomSlackPx;
        else if (state.FollowBottom && box.Selection.IsEmpty)
            box.ScrollToEnd();
    }

    private static void OnCollectionChanged(RichTextBox box, State state, NotifyCollectionChangedEventArgs e)
    {
        var blocks = box.Document.Blocks;
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems != null && e.NewStartingIndex >= 0:
                var index = e.NewStartingIndex;
                foreach (IrcMessageVm msg in e.NewItems)
                    Insert(blocks, index++, BuildParagraph(box, msg));
                break;
            case NotifyCollectionChangedAction.Remove when e.OldItems != null && e.OldStartingIndex >= 0:
                for (var i = 0; i < e.OldItems.Count; i++)
                    if (BlockAt(blocks, e.OldStartingIndex) is { } block) blocks.Remove(block);
                break;
            default:
                Rebuild(box, state);
                return;
        }
        ScrollToEndIfFollowing(box);
    }

    private static void Rebuild(RichTextBox box, State state)
    {
        var blocks = box.Document.Blocks;
        blocks.Clear();
        if (state.Source != null)
            foreach (var msg in state.Source)
                blocks.Add(BuildParagraph(box, msg));
        state.FollowBottom = true;
        box.ScrollToEnd();
    }

    private static Paragraph BuildParagraph(RichTextBox box, IrcMessageVm msg)
    {
        // Hanging indent: wrapped continuation lines sit under the message, not the timestamp.
        var paragraph = new Paragraph { Margin = new Thickness(18, 1, 0, 2), TextIndent = -18, Tag = msg };

        var timestamp = new Run(msg.Timestamp + " ") { FontSize = 12 };
        timestamp.SetResourceReference(TextElement.ForegroundProperty, "ForegroundDimBrush");
        paragraph.Inlines.Add(timestamp);
        if (msg.WasEncrypted)
            paragraph.Inlines.Add(new Run("\U0001F512 ") { FontSize = 11 });

        var body = new Span();
        if (msg.IsSystemMessage)
        {
            body.SetResourceReference(TextElement.ForegroundProperty, "ForegroundDimBrush");
            body.FontStyle = FontStyles.Italic;
        }
        else if (msg.IsHighlight)
        {
            body.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
            body.FontWeight = FontWeights.Bold;
        }
        else
        {
            body.Foreground = msg.NickColor ?? Brushes.White;
        }
        ReleaseTextHelper.AppendInlines(body.Inlines, msg, () => GetLinkCommand(box));
        paragraph.Inlines.Add(body);
        return paragraph;
    }

    private static Block? BlockAt(BlockCollection blocks, int index)
    {
        var block = blocks.FirstBlock;
        for (var i = 0; i < index && block != null; i++)
            block = block.NextBlock;
        return block;
    }

    private static void Insert(BlockCollection blocks, int index, Block block)
    {
        if (BlockAt(blocks, index) is { } before) blocks.InsertBefore(before, block);
        else blocks.Add(block);
    }
}
