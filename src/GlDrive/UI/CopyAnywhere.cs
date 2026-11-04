using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace GlDrive.UI;

/// <summary>
/// App-wide "Copy" for read-only text. WPF TextBlocks can't be selected, so right-clicking
/// any text offers Copy: either as the only item (no menu there) or prepended to the
/// existing context menu of a grid, list or button. DataGrids also get "Copy row".
/// Text inputs keep their own Cut/Copy/Paste menu.
/// </summary>
public static class CopyAnywhere
{
    private const string InjectedTag = "GlDrive.CopyAnywhere";
    private const int MaxHeaderChars = 40;

    private static WeakReference<TextBlock>? _lastText;
    private static long _lastTextTicks;

    public static void Register()
    {
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseRightButtonDownEvent,
            new MouseButtonEventHandler(OnPreviewRightDown), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(Window), UIElement.MouseRightButtonUpEvent,
            new MouseButtonEventHandler(OnRightUp));
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent,
            new RoutedEventHandler(OnMenuOpened));
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.ClosedEvent,
            new RoutedEventHandler(OnMenuClosed));
    }

    private static void OnPreviewRightDown(object sender, MouseButtonEventArgs e)
    {
        var tb = FindTextBlock(e.OriginalSource as DependencyObject);
        _lastText = tb != null && !IsInsideTextInput(tb) ? new WeakReference<TextBlock>(tb) : null;
        _lastTextTicks = Environment.TickCount64;
    }

    // Right-click on text with no context menu anywhere above it: show a Copy-only menu.
    private static void OnRightUp(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled) return;
        var source = e.OriginalSource as DependencyObject;
        var tb = FindTextBlock(source);
        if (tb == null || IsInsideTextInput(tb) || HasContextMenuAbove(source)) return;

        var items = BuildItems(tb);
        if (items.Count == 0) return;
        var menu = new ContextMenu { PlacementTarget = tb, Placement = PlacementMode.MousePoint };
        foreach (var item in items) menu.Items.Add(item);
        menu.IsOpen = true;
        e.Handled = true;
    }

    // An existing menu is opening over text: prepend Copy items for what was right-clicked.
    private static void OnMenuOpened(object sender, RoutedEventArgs e)
    {
        // A menu tagged NoCopyAnywhere already offers its own copy items.
        if (sender is not ContextMenu menu || menu.ItemsSource != null || Equals(menu.Tag, "NoCopyAnywhere")) return;
        if (menu.Items.OfType<FrameworkElement>().Any(i => Equals(i.Tag, InjectedTag))) return;
        if (menu.PlacementTarget is not DependencyObject target || target is TextBoxBase or PasswordBox) return;
        if (Environment.TickCount64 - _lastTextTicks > 2000) return;
        if (_lastText == null || !_lastText.TryGetTarget(out var tb)) return;
        if (!ReferenceEquals(tb, target) && !IsDescendant(tb, target)) return;

        var items = BuildItems(tb);
        if (items.Count == 0) return;
        items.Add(new Separator { Tag = InjectedTag });
        for (var i = 0; i < items.Count; i++)
            menu.Items.Insert(i, items[i]);
    }

    private static void OnMenuClosed(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu || menu.ItemsSource != null) return;
        foreach (var item in menu.Items.OfType<FrameworkElement>().Where(i => Equals(i.Tag, InjectedTag)).ToList())
            menu.Items.Remove(item);
    }

    private static List<FrameworkElement> BuildItems(TextBlock tb)
    {
        var items = new List<FrameworkElement>();
        var text = TextOf(tb).Trim();
        if (text.Length > 0)
            items.Add(CopyItem($"Copy \"{Shorten(text)}\"", text));

        if (FindAncestor<DataGridRow>(tb) is { } row && FindAncestor<DataGrid>(row) is { } grid)
        {
            var rowText = RowText(grid, row);
            if (rowText.Length > 0 && rowText != text)
                items.Add(CopyItem("Copy row", rowText));
        }
        return items;
    }

    private static MenuItem CopyItem(string header, string text)
    {
        // Header as a plain string: underscores in release names would otherwise become access keys.
        var item = new MenuItem { Header = new TextBlock { Text = header }, Tag = InjectedTag };
        item.Click += (_, _) => ClipboardHelper.TrySetText(text);
        return item;
    }

    internal static string TextOf(TextBlock tb) =>
        tb.Inlines.Count > 0 ? new TextRange(tb.ContentStart, tb.ContentEnd).Text : tb.Text ?? "";

    private static string RowText(DataGrid grid, DataGridRow row)
    {
        var cells = new List<string>();
        foreach (var column in grid.Columns.Where(c => c.Visibility == Visibility.Visible).OrderBy(c => c.DisplayIndex))
        {
            var content = column.GetCellContent(row);
            var text = content switch
            {
                TextBlock t => TextOf(t),
                ContentPresenter or ContentControl => FindDescendant<TextBlock>(content) is { } inner ? TextOf(inner) : "",
                _ => "",
            };
            cells.Add(text.Trim());
        }
        return string.Join("\t", cells).Trim();
    }

    private static string Shorten(string text)
    {
        var line = text.Replace("\r", " ").Replace("\n", " ");
        return line.Length <= MaxHeaderChars ? line : line[..(MaxHeaderChars - 1)] + "…";
    }

    private static TextBlock? FindTextBlock(DependencyObject? source)
    {
        // A click on a Run/Hyperlink reports the inline; walk up its logical parents to the TextBlock.
        while (source is TextElement element)
            source = element.Parent;
        return source as TextBlock;
    }

    // Text inputs have their own Cut/Copy/Paste menu; button and menu captions aren't content.
    private static bool IsInsideTextInput(DependencyObject d) =>
        FindAncestor<TextBoxBase>(d) != null || FindAncestor<PasswordBox>(d) != null
        || FindAncestor<ButtonBase>(d) != null || FindAncestor<MenuItem>(d) != null;

    private static bool HasContextMenuAbove(DependencyObject? d)
    {
        for (var current = d; current != null; current = Parent(current))
        {
            if (current is FrameworkElement fe && fe.ContextMenu != null && ContextMenuService.GetIsEnabled(fe)) return true;
            if (current is FrameworkContentElement fce && fce.ContextMenu != null && ContextMenuService.GetIsEnabled(fce)) return true;
        }
        return false;
    }

    private static bool IsDescendant(DependencyObject child, DependencyObject ancestor)
    {
        for (var current = Parent(child); current != null; current = Parent(current))
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        for (var current = d == null ? null : Parent(d); current != null; current = Parent(current))
            if (current is T match) return match;
        return null;
    }

    private static T? FindDescendant<T>(DependencyObject? d) where T : DependencyObject
    {
        if (d == null) return null;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var child = VisualTreeHelper.GetChild(d, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private static DependencyObject? Parent(DependencyObject d) => d switch
    {
        Visual or System.Windows.Media.Media3D.Visual3D => VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d),
        FrameworkContentElement fce => fce.Parent,
        _ => LogicalTreeHelper.GetParent(d),
    };
}
