using System.Windows;
using System.Windows.Controls;

namespace GlDrive.AiAgent;

public static class AiAgentAttached
{
    public static readonly DependencyProperty FreezablePathProperty = DependencyProperty.RegisterAttached(
        "FreezablePath", typeof(string), typeof(AiAgentAttached),
        new PropertyMetadata(null, OnFreezablePathChanged));

    public static string? GetFreezablePath(DependencyObject d) => (string?)d.GetValue(FreezablePathProperty);
    public static void SetFreezablePath(DependencyObject d, string? v) => d.SetValue(FreezablePathProperty, v);

    private static void OnFreezablePathChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;

        var newPath = e.NewValue as string;
        if (string.IsNullOrWhiteSpace(newPath))
        {
            fe.ContextMenu = null;
            return;
        }

        var cm = new ContextMenu();
        var toggle = new MenuItem { Header = "Freeze for AI" };
        toggle.Click += (_, __) =>
        {
            var store = App.FreezeStore;
            var path = GetFreezablePath(fe);
            if (store is null || string.IsNullOrWhiteSpace(path)) return;
            if (store.IsFrozen(path))
                store.Unfreeze(path);
            else
                store.Freeze(path);
            UpdateMenuLabel(toggle, store, path!);
            UpdateVisualIndicator(fe, store, path!);
        };
        cm.Items.Add(toggle);
        fe.ContextMenu = cm;

        fe.Loaded += (_, __) =>
        {
            var store = App.FreezeStore;
            var path = GetFreezablePath(fe);
            if (store is null || string.IsNullOrWhiteSpace(path)) return;
            UpdateMenuLabel(toggle, store, path!);
            UpdateVisualIndicator(fe, store, path!);
            store.Changed += OnStoreChanged;

            void OnStoreChanged()
            {
                fe.Dispatcher.Invoke(() =>
                {
                    var currentPath = GetFreezablePath(fe);
                    if (string.IsNullOrWhiteSpace(currentPath)) return;
                    UpdateMenuLabel(toggle, store, currentPath!);
                    UpdateVisualIndicator(fe, store, currentPath!);
                });
            }
        };
    }

    private static void UpdateMenuLabel(MenuItem mi, FreezeStore store, string path)
        => mi.Header = store.IsFrozen(path) ? "Unfreeze (AI may change)" : "Freeze for AI";

    private static void UpdateVisualIndicator(FrameworkElement fe, FreezeStore store, string path)
    {
        if (store.IsFrozen(path))
        {
            fe.ToolTip = $"Frozen — AI agent will not modify this ({path})";
            if (fe is Control c)
                c.BorderBrush = Application.Current.TryFindResource("AccentBrush") as System.Windows.Media.Brush
                                ?? c.BorderBrush;
        }
        else
        {
            fe.ToolTip = null;
            // Don't forcefully reset BorderBrush — original style reasserts via DynamicResource on next theme swap
        }
    }
}
