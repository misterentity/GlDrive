using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.UI;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Live-control checks for the terminal UI pass, run inside the render test's single WPF
/// Application for every theme (Cyberpunk, Dark, Light, and Cyberpunk again after switching),
/// plus once-only checks of already-open controls across live theme swaps.
/// </summary>
public partial class StreamMatrixRenderTests
{
    private static readonly string[] TerminalThemeKeys =
    [
        "ReleaseTokenMetaBrush", "ReleaseTokenSourceBrush", "ReleaseTokenCodecBrush",
        "ReleaseTokenTagBrush", "ReleaseTokenGroupBrush", "ReleaseTokenSeparatorBrush",
    ];

    private static void AssertTerminalUi(string theme, ResourceDictionary themeDict)
    {
        var missing = TerminalThemeKeys.Where(k => !themeDict.Contains(k)).ToList();
        Assert.True(missing.Count == 0, $"{theme} theme is missing: {string.Join(", ", missing)}");

        AssertThemeSwitchLeavesWindowContentAlone(theme);
        AssertFilterChipsDriveTheSearchViewThroughAutomationAndClick();
        AssertDownloadRowsShowSelectionStateAndLiveProgress(theme);
        AssertFocusRingIsOutsideTheControlAndContrasting(theme);
        AssertRealKeyboardFocusIsVisible(theme);
        AssertReleaseTokensKeepExactTextAndThemeColours();
        if (theme == "Cyberpunk") AssertDisabledButtonsDropTheirFill();
    }

    /// <summary>Checks that need one setup and then drive their own theme changes or have no theme dependence.</summary>
    private static void AssertTerminalUiOnce()
    {
        AssertSearchPublicationKeepsFiltersCurrentAndSelection();
        AssertClearMenuOpensAndRunsEachClearCommand();
        AssertOpenControlsFollowLiveThemeSwaps();
    }

    private static ResourceDictionary DashboardControls() =>
        new() { Source = new Uri("pack://application:,,,/GlDrive;component/UI/DashboardControls.xaml") };

    // Cyberpunk used to wrap every window's content in an overlay grid, including the window that
    // hosts LibVLC and WebView2. Applying a theme must leave content where it is.
    private static void AssertThemeSwitchLeavesWindowContentAlone(string theme)
    {
        var content = new Grid();
        var window = OffscreenWindow(content, 300, 120);
        try
        {
            window.Show();
            Pump();
            ThemeManager.ApplyTheme(theme);
            Pump();
            Assert.Same(content, window.Content);
        }
        finally { window.Close(); }
    }

    // ── Search: filter chips and result publication ────────────────────────────────────────

    private static SearchResultVm Result(string name, long size) => new() { ReleaseName = name, Size = size, ServerName = "site" };

    /// <summary>
    /// The Search tab's chip surface (same property and command names DashboardViewModel binds),
    /// filtering through the product's SearchResultsView/SearchFilterToggle. Counts executions and
    /// view refreshes so a double-applied toggle shows up.
    /// </summary>
    private sealed class SearchChipViewModel : INotifyPropertyChanged
    {
        public ObservableCollection<SearchResultVm> SearchResults { get; } = new();
        public ICollectionView View { get; }
        public SearchFilterToggle.State Filter { get; private set; } = new("", "", false);
        public int Executions { get; private set; }
        public int Refreshes { get; private set; }
        public ICommand ToggleSearchFilterCommand { get; }
        public string SearchFilterQuality => Filter.Quality;
        public string SearchFilterSource => Filter.Source;
        public bool SearchFilterBigSize => Filter.BigSize;
        public bool SearchFilterAnyActive => Filter.AnyActive;

        public SearchChipViewModel(params SearchResultVm[] results)
        {
            foreach (var r in results) SearchResults.Add(r);
            View = SearchResultsView.Attach(SearchResults, () => Filter);
            ToggleSearchFilterCommand = new RelayCommand<string>(arg =>
            {
                Executions++;
                var next = SearchFilterToggle.Apply(Filter, arg);
                if (next == Filter) return;
                Filter = next;
                Refreshes++;
                View.Refresh();
                foreach (var name in new[] { nameof(SearchFilterQuality), nameof(SearchFilterSource), nameof(SearchFilterBigSize), nameof(SearchFilterAnyActive) })
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            });
        }

        public string[] Visible => View.Cast<SearchResultVm>().Select(r => r.ReleaseName).ToArray();
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>A chip as DashboardWindow declares it: the product FilterChipStyle (which binds ToggleCommand) and a one-way IsChecked.</summary>
    private static FilterChip ProductChip(ResourceDictionary controls, string content, string parameter, string path,
        IValueConverter? converter = null, object? converterParameter = null)
    {
        var chip = new FilterChip { Content = content, ToggleCommandParameter = parameter, Style = (Style)controls["FilterChipStyle"] };
        chip.SetBinding(ToggleButton.IsCheckedProperty, new Binding(path)
        { Mode = BindingMode.OneWay, Converter = converter, ConverterParameter = converterParameter });
        AutomationProperties.SetName(chip, $"Filter {content}");
        return chip;
    }

    private static IToggleProvider ToggleOf(ToggleButton chip) =>
        Assert.IsAssignableFrom<IToggleProvider>(new ToggleButtonAutomationPeer(chip).GetPattern(PatternInterface.Toggle));

    private static void AssertFilterChipsDriveTheSearchViewThroughAutomationAndClick()
    {
        const long GiB = 1L << 30;
        var a = "A.2026.1080p.WEB.H264-GRP";
        var b = "B.2026.2160p.BluRay.x265-GRP";
        var c = "C.2026.1080p.BluRay.x264-GRP";
        var d = "D.2026.720p.WEB.H264-GRP";
        var vm = new SearchChipViewModel(Result(a, 2 * GiB), Result(b, 8 * GiB), Result(c, GiB / 2), Result(d, GiB / 3));
        var controls = DashboardControls();
        var all = ProductChip(controls, "ALL", "clear", nameof(vm.SearchFilterAnyActive), new InverseBooleanConverter());
        var hd = ProductChip(controls, "1080p", "quality:1080p", nameof(vm.SearchFilterQuality), new ValueEqualsConverter(), "1080p");
        var uhd = ProductChip(controls, "2160p", "quality:2160p", nameof(vm.SearchFilterQuality), new ValueEqualsConverter(), "2160p");
        var web = ProductChip(controls, "WEB", "source:WEB", nameof(vm.SearchFilterSource), new ValueEqualsConverter(), "WEB");
        var bluray = ProductChip(controls, "BLURAY", "source:BLURAY", nameof(vm.SearchFilterSource), new ValueEqualsConverter(), "BLURAY");
        var big = ProductChip(controls, "> 1GB", "size:1GB", nameof(vm.SearchFilterBigSize));
        var panel = new StackPanel { DataContext = vm, Orientation = Orientation.Horizontal };
        foreach (var chip in new[] { all, hd, uhd, web, bluray, big }) panel.Children.Add(chip);
        var window = OffscreenWindow(panel, 700, 80);
        try
        {
            window.Show();
            Pump();
            Assert.Same(vm.ToggleSearchFilterCommand, hd.ToggleCommand);   // wired by the product style
            Assert.Equal(true, all.IsChecked);
            Assert.Equal(new[] { a, b, c, d }, vm.Visible);
            Assert.Equal("Filter 1080p", new ToggleButtonAutomationPeer(hd).GetName());

            // ALL while nothing is filtered: the command runs, nothing changes, and the chip does not flip itself off.
            ToggleOf(all).Toggle();
            Pump();
            Assert.Equal((1, 0), (vm.Executions, vm.Refreshes));
            Assert.Equal(true, all.IsChecked);

            ToggleOf(hd).Toggle();
            Pump();
            Assert.Equal((2, 1), (vm.Executions, vm.Refreshes));
            Assert.Equal(new[] { a, c }, vm.Visible);
            Assert.Equal(ToggleState.On, ToggleOf(hd).ToggleState);
            Assert.Equal(false, all.IsChecked);

            ToggleOf(uhd).Toggle();                  // exclusive: 2160p replaces 1080p
            Pump();
            Assert.Equal((3, 2), (vm.Executions, vm.Refreshes));
            Assert.Equal(new[] { b }, vm.Visible);
            Assert.Equal(false, hd.IsChecked);
            Assert.Equal(true, uhd.IsChecked);

            ToggleOf(uhd).Toggle();                  // choosing the active chip again clears it
            Pump();
            Assert.Equal(new[] { a, b, c, d }, vm.Visible);
            Assert.Equal(false, uhd.IsChecked);
            Assert.Equal(true, all.IsChecked);

            // A click (mouse or Enter) runs the command exactly once and never toggles locally.
            typeof(ButtonBase).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(web, null);
            Pump();
            Assert.Equal((5, 4), (vm.Executions, vm.Refreshes));
            Assert.Equal(new[] { a, d }, vm.Visible);
            Assert.Equal(true, web.IsChecked);

            ToggleOf(bluray).Toggle();               // source is exclusive too
            Pump();
            Assert.Equal(new[] { b, c }, vm.Visible);
            Assert.Equal(false, web.IsChecked);

            ToggleOf(big).Toggle();                  // size combines with the source filter
            Pump();
            Assert.Equal(new[] { b }, vm.Visible);
            Assert.Equal(true, big.IsChecked);

            var refreshes = vm.Refreshes;
            ToggleOf(all).Toggle();                  // ALL clears every filter in one refresh
            Pump();
            Assert.Equal(refreshes + 1, vm.Refreshes);
            Assert.Equal(new SearchFilterToggle.State("", "", false), vm.Filter);
            Assert.Equal(new[] { a, b, c, d }, vm.Visible);
            Assert.Equal(true, all.IsChecked);
            Assert.All(new[] { hd, uhd, web, bluray, big }, chip => Assert.Equal(false, chip.IsChecked));
            Assert.True(BindingOperations.IsDataBound(hd, ToggleButton.IsCheckedProperty), "IsChecked binding was replaced by a local value.");

            // The checked state is drawn (LED fill), not only exposed.
            Color Res(string key) => ((SolidColorBrush)Application.Current.FindResource(key)).Color;
            var led = Assert.IsType<Rectangle>(all.Template.FindName("led", all));
            Assert.Equal(Res("AccentBrush"), ((SolidColorBrush)led.Fill).Color);
            Assert.Equal(0, ((SolidColorBrush)((Rectangle)hd.Template.FindName("led", hd)).Fill).Color.A);
        }
        finally { window.Close(); }
    }

    // Production 3.11.11 "Dashboard search failed": each site's batch was added inside
    // DeferRefresh on the filtered default view the Search DataGrid is bound to.
    private static void AssertSearchPublicationKeepsFiltersCurrentAndSelection()
    {
        foreach (var seeded in new[] { false, true })
        {
            // Control: the removed pattern throws in this exact setup, so the check below is sensitive to it.
            {
                var (results, view, _, window) = SearchGrid(seeded);
                try
                {
                    var error = Assert.Throws<InvalidOperationException>(() =>
                    {
                        using (view.DeferRefresh()) results.Add(Result("Deferred.2026.1080p.WEB-GRP", 1));
                    });
                    Assert.Contains("Refresh is being deferred", error.Message);
                }
                finally { window.Close(); }
            }
            {
                var (results, view, grid, window) = SearchGrid(seeded);
                try
                {
                    var seed = results.FirstOrDefault();
                    // Two sites finishing separately, each published as it completes.
                    SearchResultsView.Publish(results, [Result("Skip.2026.2160p.WEB-GRP", 1), Result("Match.A.2026.1080p.WEB-GRP", 1)]);
                    Pump();
                    SearchResultsView.Publish(results, [Result("Match.B.2026.1080p.BluRay-GRP", 1)]);
                    Pump();
                    grid.UpdateLayout();

                    var matches = new[] { "Match.A.2026.1080p.WEB-GRP", "Match.B.2026.1080p.BluRay-GRP" };
                    var expected = seeded ? new[] { "Seed.2026.1080p.WEB-GRP" }.Concat(matches).ToArray() : matches;
                    Assert.Equal(seeded ? 4 : 3, results.Count);
                    Assert.Equal(expected, view.Cast<SearchResultVm>().Select(r => r.ReleaseName).ToArray());
                    Assert.Equal(expected, grid.Items.Cast<SearchResultVm>().Select(r => r.ReleaseName).ToArray());
                    if (seeded)
                    {
                        Assert.Same(seed, view.CurrentItem);   // the user's row stays current and selected
                        Assert.Same(seed, grid.SelectedItem);
                    }
                    else
                    {
                        view.MoveCurrentToFirst();
                        grid.SelectedItem = view.CurrentItem;
                        Pump();
                        Assert.Equal("Match.A.2026.1080p.WEB-GRP", ((SearchResultVm)grid.SelectedItem).ReleaseName);
                    }
                }
                finally { window.Close(); }
            }
        }
    }

    private static (ObservableCollection<SearchResultVm>, ICollectionView, DataGrid, Window) SearchGrid(bool seeded)
    {
        var results = new ObservableCollection<SearchResultVm>();
        if (seeded) results.Add(Result("Seed.2026.1080p.WEB-GRP", 1));
        var filter = new SearchFilterToggle.State("1080p", "", false);   // an active chip
        var view = SearchResultsView.Attach(results, () => filter);
        var grid = new DataGrid
        {
            ItemsSource = results, AutoGenerateColumns = false, IsReadOnly = true,
            SelectionMode = DataGridSelectionMode.Extended, Width = 640, Height = 200,
        };
        grid.Columns.Add(new DataGridTextColumn { Header = "Release", Binding = new Binding(nameof(SearchResultVm.ReleaseName)) });
        var window = OffscreenWindow(grid, 680, 240);
        window.Show();
        Pump();
        if (seeded)
        {
            view.MoveCurrentTo(results[0]);
            grid.SelectedItem = results[0];
            Pump();
        }
        return (results, view, grid, window);
    }

    // ── Downloads ──────────────────────────────────────────────────────────────────────────

    private static ListBox DownloadList(ResourceDictionary controls, IEnumerable<DownloadItemVm> rows, double width = 900) => new()
    {
        ItemsSource = rows, SelectionMode = SelectionMode.Extended, Width = width,
        ItemContainerStyle = (Style)controls["DownloadQueueItemStyle"],
        ItemTemplate = new DataTemplate { VisualTree = new FrameworkElementFactory(typeof(DownloadRowView)) },
    };

    private static DownloadRowView RowView(ListBox list, DownloadItemVm row) =>
        Assert.Single(FindAll<DownloadRowView>((ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(row)));

    // A row created by a template factory does not resolve its own names through FindName, so look
    // up the rendered named element in its visual tree.
    private static T RowPart<T>(ListBox list, DownloadItemVm row, string name) where T : FrameworkElement =>
        Assert.Single(FindAll<T>(RowView(list, row)), e => e.Name == name);

    private static void AssertDownloadRowsShowSelectionStateAndLiveProgress(string theme)
    {
        var rows = new[]
        {
            new DownloadItemVm { Id = "a", ReleaseName = "Sinners.2026.2160p.UHD.BluRay.x265-LIGHTBRiNGER", Status = "Downloading", ServerName = "site-a", Category = "x265" },
            new DownloadItemVm { Id = "b", ReleaseName = "Dune.Part.Three.2026.1080p.BluRay.H264-RiSEHD", Status = "Failed", ServerName = "site-a", Category = "x264-hd", ProgressPercent = 18 },
            new DownloadItemVm { Id = "c", ReleaseName = "Severance.S03E07.1080p.WEB.H264-NTb", Status = "Completed", ServerName = "site-b", Category = "tv-hd", ProgressPercent = 100 },
        };
        var list = DownloadList(DashboardControls(), rows);
        var window = OffscreenWindow(list, 940, 420);
        try
        {
            window.Show();
            Pump();
            list.SelectedItems.Add(rows[0]);
            list.SelectedItems.Add(rows[2]);
            Pump();
            Assert.Equal(new[] { rows[0], rows[2] }, list.SelectedItems.Cast<DownloadItemVm>().ToArray());

            Color Res(string key) => ((SolidColorBrush)Application.Current.FindResource(key)).Color;
            void AssertSelection(DownloadItemVm row, bool selected)
            {
                var root = RowPart<Border>(list, row, "RowRoot");
                Assert.Equal(Res(selected ? "AccentDimBrush" : "SurfaceBrush"), ((SolidColorBrush)root.Background).Color);
                if (selected) Assert.Equal(Res("AccentBrush"), ((SolidColorBrush)root.BorderBrush).Color);
            }

            // Selection is visible on every selected row (multi-select), not hidden under the row surface.
            AssertSelection(rows[0], true);
            AssertSelection(rows[1], false);
            AssertSelection(rows[2], true);
            Assert.NotEqual(Res("AccentDimBrush"), Res("SurfaceBrush"));

            // State: rail and progress share the status colour; the chip carries a glyph.
            var failed = RowView(list, rows[1]);
            var rail = RowPart<Rectangle>(list, rows[1], "StatusRail");
            var bar = RowPart<ProgressBar>(list, rows[1], "RowProgress");
            Assert.Equal(Res("BadBrush"), ((SolidColorBrush)rail.Fill).Color);
            Assert.Same(rail.Fill, bar.Foreground);
            Assert.Equal(18, bar.Value, 3);
            Assert.Contains(FindAll<TextBlock>(failed), t => t.Text == TransferStatePresentation.Glyph("Failed"));
            Assert.Contains(FindAll<TextBlock>(failed), t => t.Text == "Failed");     // the word, not only colour/glyph
            Assert.Equal(Res("GoodBrush"), ((SolidColorBrush)RowPart<Rectangle>(list, rows[2], "StatusRail").Fill).Color);
            var liveRail = RowPart<Rectangle>(list, rows[0], "StatusRail");
            Assert.Equal(theme == "Cyberpunk", liveRail.Effect != null);

            // Selecting the failed row keeps its status rail; deselecting restores the plain surface.
            list.SelectedItems.Add(rows[1]);
            list.SelectedItems.Remove(rows[0]);
            Pump();
            AssertSelection(rows[1], true);
            Assert.Equal(Res("BadBrush"), ((SolidColorBrush)rail.Fill).Color);
            AssertSelection(rows[0], false);

            // Accessible name of each row is its release.
            var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(rows[1]);
            Assert.Equal(rows[1].ReleaseName, AutomationProperties.GetName(container));

            // The row's text is the exact release name even though it is drawn as tokens.
            Assert.Equal(rows[1].ReleaseName, RowPart<TextBlock>(list, rows[1], "ReleaseNameLabel").Text);

            // Live progress reaches the same bar without the row being rebuilt.
            var liveBar = RowPart<ProgressBar>(list, rows[0], "RowProgress");
            var downloading = new DownloadItem { Status = DownloadStatus.Downloading };
            rows[0].ApplyProgress(downloading, new DownloadProgress(10, 100, 1));
            Pump();
            Assert.Equal(10, liveBar.Value, 3);
            rows[0].ApplyProgress(downloading, new DownloadProgress(60, 100, 1));
            Pump();
            Assert.Equal(60, liveBar.Value, 3);
            rows[0].ApplyProgress(downloading, new DownloadProgress(250, 100, 1));   // bytes beyond an evolving total
            Pump();
            Assert.Equal(100, liveBar.Value, 3);
            rows[0].ApplyProgress(downloading, new DownloadProgress(60, 100, 1));
            downloading.Status = DownloadStatus.Failed;                              // a late callback after leaving Downloading
            rows[0].ApplyProgress(downloading, new DownloadProgress(95, 100, 1));
            Pump();
            Assert.Equal(60, liveBar.Value, 3);
            Assert.Same(liveBar, RowPart<ProgressBar>(list, rows[0], "RowProgress"));
        }
        finally { window.Close(); }
    }

    private sealed class ClearCommandsSpy
    {
        public Dictionary<string, int> Runs { get; } = new();
        public ICommand ClearCompletedCommand { get; }
        public ICommand ClearFailedCommand { get; }
        public ICommand ClearCancelledCommand { get; }
        public ICommand ClearAllFinishedCommand { get; }

        public ClearCommandsSpy()
        {
            ClearCompletedCommand = Spy(nameof(ClearCompletedCommand));
            ClearFailedCommand = Spy(nameof(ClearFailedCommand));
            ClearCancelledCommand = Spy(nameof(ClearCancelledCommand));
            ClearAllFinishedCommand = Spy(nameof(ClearAllFinishedCommand));
        }

        private ICommand Spy(string name) => new RelayCommand(() => { Runs[name] = Runs.GetValueOrDefault(name) + 1; });
    }

    // The Clear ▾ menu is a ContextMenu (outside the visual tree): opening it must bind each entry to
    // the toolbar's DataContext. Spy commands only; no download store is touched.
    private static void AssertClearMenuOpensAndRunsEachClearCommand()
    {
        var spy = new ClearCommandsSpy();
        var control = new ClearDownloadsMenuButton { DataContext = spy };
        var window = OffscreenWindow(control, 300, 120);
        var menu = control.Menu;
        try
        {
            window.Show();
            Pump();
            var button = Assert.IsType<Button>(control.FindName("MenuButton"));
            Assert.Equal("Clear finished downloads", new ButtonAutomationPeer(button).GetName());
            Assert.False(menu.IsOpen);
            var entries = new (string Header, ICommand Command, string Name)[]
            {
                ("Clear Completed", spy.ClearCompletedCommand, nameof(spy.ClearCompletedCommand)),
                ("Clear Failed", spy.ClearFailedCommand, nameof(spy.ClearFailedCommand)),
                ("Clear Cancelled", spy.ClearCancelledCommand, nameof(spy.ClearCancelledCommand)),
                ("Clear All Finished", spy.ClearAllFinishedCommand, nameof(spy.ClearAllFinishedCommand)),
            };
            Assert.Equal(entries.Select(e => e.Header), menu.Items.OfType<MenuItem>().Select(i => (string)i.Header));

            var total = 0;
            foreach (var (header, command, name) in entries)
            {
                var invoke = Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke));
                invoke.Invoke();                     // the same click path as the mouse / Enter / Space
                Pump();
                Assert.True(menu.IsOpen, "Clear ▾ did not open its menu");
                Assert.Same(button, menu.PlacementTarget);
                Assert.Same(spy, menu.DataContext);

                var item = menu.Items.OfType<MenuItem>().Single(i => (string)i.Header == header);
                Assert.Same(command, item.Command);
                Assert.True(item.IsEnabled, $"{header} is disabled");
                Assert.IsAssignableFrom<IInvokeProvider>(new MenuItemAutomationPeer(item).GetPattern(PatternInterface.Invoke)).Invoke();
                Pump();
                Assert.Equal(1, spy.Runs.GetValueOrDefault(name));
                Assert.Equal(++total, spy.Runs.Values.Sum());   // only that entry's command ran
                menu.IsOpen = false;
                Pump();
            }
        }
        finally
        {
            menu.IsOpen = false;
            window.Close();
        }
    }

    // ── Focus ──────────────────────────────────────────────────────────────────────────────

    private static void AssertFocusRingIsOutsideTheControlAndContrasting(string theme)
    {
        var focusStyle = (Style)Application.Current.FindResource("KeyboardFocusStyle");
        var target = new Control { Style = focusStyle, Width = 120, Height = 32 };
        var primary = new Button { Content = "Search", Style = (Style)Application.Current.FindResource("PrimaryButton") };
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(target);
        panel.Children.Add(primary);
        var window = OffscreenWindow(panel, 300, 160);
        try
        {
            window.Show();
            Pump();
            // Primary buttons use the same ring (it used to be a 1px dashed inset on the mint fill).
            Assert.Same(focusStyle, primary.FocusVisualStyle);

            var frame = Assert.IsType<Grid>(VisualTreeHelper.GetChild(target, 0));
            Assert.True(frame.Margin.Left <= -3 && frame.Margin.Top <= -3, $"{theme}: focus ring is drawn inside the control");
            var ring = Assert.IsType<Rectangle>(target.Template.FindName("FocusRing", target));
            var gap = Assert.IsType<Rectangle>(target.Template.FindName("FocusGap", target));
            Assert.True(ring.StrokeThickness >= 2);
            Assert.True(ring.StrokeDashArray == null || ring.StrokeDashArray.Count == 0, "focus ring should be solid");
            var ringColor = ((SolidColorBrush)ring.Stroke).Color;
            var background = ((SolidColorBrush)Application.Current.FindResource("BackgroundBrush")).Color;
            Assert.Equal(background, ((SolidColorBrush)gap.Stroke).Color);
            var contrast = Contrast(ringColor, background);
            Assert.True(contrast >= 3.0, $"{theme}: focus ring contrast {contrast:0.0}:1 against the background");
        }
        finally { window.Close(); }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(nint hwnd, uint message, nuint wparam, nint lparam);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    /// <summary>
    /// A full key press (down, then up) posted to the HWND of the fixture window that shows
    /// <paramref name="target"/>, so it goes through WPF's native keyboard provider like a real
    /// key. Nothing outside that window is addressed; no global input is injected.
    /// </summary>
    private static void SendKey(UIElement target, Key key)
    {
        var window = Window.GetWindow(target) ?? throw new InvalidOperationException("target is not in a fixture window");
        if (!window.IsVisible || !ReferenceEquals(PresentationSource.FromVisual(target)?.RootVisual, window))
            throw new InvalidOperationException("target is not in a shown fixture window");
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0) throw new InvalidOperationException("fixture window has no HWND");

        const uint WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101;
        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        var scan = (int)MapVirtualKey(vk, 0);   // MAPVK_VK_TO_VSC
        void Post(uint message, nint lparam)
        {
            if (!PostMessage(hwnd, message, vk, lparam))
                throw new InvalidOperationException($"PostMessage 0x{message:X4} failed: {Marshal.GetLastWin32Error()}");
            Pump();
        }
        Post(WM_KEYDOWN, (scan << 16) | 1);
        Post(WM_KEYUP, unchecked((int)0xC0000000) | (scan << 16) | 1);   // previous-state and transition bits
    }

    /// <summary>The focus visual WPF actually attached to a keyboard-focused element, if any.</summary>
    private static Control? FocusVisualOf(UIElement element)
    {
        var adorner = AdornerLayer.GetAdornerLayer(element)?.GetAdorners(element)?
            .FirstOrDefault(a => a.GetType().Name == "FocusVisualAdorner");
        return adorner == null ? null : FindAll<Control>(adorner).FirstOrDefault();
    }

    // Real keyboard focus on real controls: keyboard input (Tab, Space) is posted as native key
    // messages to the fixture window and goes through WPF's keyboard provider, and the ring checked is the focus-visual adorner WPF itself attached, not a
    // hand-styled fixture. Needs a test host that lets an offscreen window take keyboard focus;
    // if that fails, the message says so and a native focus smoke is required instead.
    private static void AssertRealKeyboardFocusIsVisible(string theme)
    {
        var controls = DashboardControls();
        var vm = new SearchChipViewModel(Result("A.2026.1080p.WEB-GRP", 1));
        var primary = new Button { Content = "Search", Margin = new Thickness(8) };
        primary.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
        var secondary = new Button { Content = "Cancel", Margin = new Thickness(8) };
        secondary.SetResourceReference(FrameworkElement.StyleProperty, "SecondaryButton");
        var chip = ProductChip(controls, "1080p", "quality:1080p", nameof(vm.SearchFilterQuality), new ValueEqualsConverter(), "1080p");
        chip.Margin = new Thickness(8);
        var input = new TextBox { Text = "query", Width = 180, Margin = new Thickness(8) };
        var tabs = new TabControl { Height = 60, Margin = new Thickness(8) };
        tabs.Items.Add(new TabItem { Header = "Search" });
        tabs.Items.Add(new TabItem { Header = "Player" });
        var expander = new Expander { Header = "Connection streams", Content = new TextBlock { Text = "matrix" }, Margin = new Thickness(8) };
        var row = new DownloadItemVm { Id = "f", ReleaseName = "Focus.Row.2026.1080p.WEB-GRP", Status = "Queued", ServerName = "site" };
        var list = DownloadList(controls, new[] { row }, 600);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(primary);
        buttons.Children.Add(secondary);
        buttons.Children.Add(chip);
        var panel = new StackPanel { DataContext = vm, Margin = new Thickness(12) };
        foreach (var element in new UIElement[] { buttons, input, tabs, expander, list }) panel.Children.Add(element);
        var window = OffscreenWindow(panel, 700, 460);
        try
        {
            window.ShowActivated = true;
            window.Show();
            window.Activate();
            Pump();
            Keyboard.Focus(primary);
            Pump();
            Assert.True(primary.IsKeyboardFocused,
                $"{theme}: this test host could not give an offscreen window keyboard focus; run a native focus smoke instead.");

            var focusRing = ((SolidColorBrush)Application.Current.FindResource("FocusRingBrush")).Color;
            void AssertRing(FrameworkElement target, string what)
            {
                var visual = FocusVisualOf(target);
                Assert.True(visual != null, $"{theme}: no keyboard focus visual on {what}");
                Pump();
                var ring = Assert.IsType<Rectangle>(visual!.Template.FindName("FocusRing", visual));
                Assert.Equal(focusRing, ((SolidColorBrush)ring.Stroke).Color);
                Assert.True(ring.ActualWidth >= target.ActualWidth + 6 && ring.ActualHeight >= target.ActualHeight + 6,
                    $"{theme}: focus ring on {what} is not drawn outside it");
            }

            // Tab: real keyboard navigation moves focus and WPF draws the ring.
            SendKey(primary, Key.Tab);
            Assert.True(secondary.IsKeyboardFocused, $"{theme}: Tab did not move focus from the primary to the next button");
            AssertRing(secondary, "secondary button");

            // Keyboard is now the most recent input device, so focus moves show the ring as they do for Tab.
            Keyboard.Focus(primary);
            Pump();
            AssertRing(primary, "primary button");

            Keyboard.Focus(chip);
            Pump();
            AssertRing(chip, "filter chip");
            // Space on the focused chip applies the filter once through the same command path.
            SendKey(chip, Key.Space);
            Assert.Equal((1, "1080p"), (vm.Executions, vm.Filter.Quality));
            Assert.Equal(true, chip.IsChecked);
            Assert.DoesNotContain(vm.Visible, n => !n.Contains("1080p"));

            Keyboard.Focus(input);
            Pump();
            Assert.True(input.IsKeyboardFocusWithin);
            var inputBorder = Assert.IsType<Border>(input.Template.FindName("Bd", input));
            Assert.Equal(focusRing, ((SolidColorBrush)inputBorder.BorderBrush).Color);

            var tab = (TabItem)tabs.ItemContainerGenerator.ContainerFromIndex(0);
            Keyboard.Focus(tab);
            Pump();
            AssertRing(tab, "tab");

            var header = Assert.Single(FindAll<ToggleButton>(expander));
            Keyboard.Focus(header);
            Pump();
            Assert.True(header.IsKeyboardFocused);
            // Dark and Light keep WPF's own Expander template (its own focus visual); Cyberpunk uses the shared ring.
            if (theme == "Cyberpunk") AssertRing(header, "expander header");
            else Assert.NotNull(FocusVisualOf(header));

            list.SelectedItem = row;
            Pump();
            var item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(row);
            Keyboard.Focus(item);
            Pump();
            AssertRing(item, "download row");
            // Focus does not replace the selection treatment.
            var root = RowPart<Border>(list, row, "RowRoot");
            Assert.Equal(((SolidColorBrush)Application.Current.FindResource("AccentDimBrush")).Color, ((SolidColorBrush)root.Background).Color);
        }
        finally { window.Close(); }
    }

    // ── Release names, disabled buttons ────────────────────────────────────────────────────

    private static void AssertReleaseTokensKeepExactTextAndThemeColours()
    {
        const string name = "Sinners.2026.PROPER.2160p.UHD.BluRay.x265-LIGHTBRiNGER";
        var text = new TextBlock();
        ReleaseNameText.SetSource(text, name);
        var window = OffscreenWindow(text, 600, 60);
        try
        {
            window.Show();
            Pump();
            // Source was set before the TextBlock was shown, when WPF can leave its Text cache empty;
            // what is drawn and what a screen reader hears are the content and the automation name.
            string Drawn() => new TextRange(text.ContentStart, text.ContentEnd).Text;
            string Heard() => new TextBlockAutomationPeer(text).GetName();
            Assert.Equal(name, Drawn());
            Assert.Equal(name, Heard());
            var runs = text.Inlines.OfType<Run>().ToList();
            Color Fore(string token) => ((SolidColorBrush)runs.Single(r => r.Text == token).Foreground).Color;
            Color Res(string key) => ((SolidColorBrush)Application.Current.FindResource(key)).Color;
            Assert.Equal(Res("ReleaseTokenMetaBrush"), Fore("2026"));
            Assert.Equal(Res("ReleaseTokenTagBrush"), Fore("PROPER"));
            Assert.Equal(Res("ReleaseTokenGroupBrush"), Fore("LIGHTBRiNGER"));
            // The title is not recoloured: it inherits the row's full-contrast foreground.
            Assert.Equal(DependencyProperty.UnsetValue, runs.Single(r => r.Text == "Sinners").ReadLocalValue(TextElement.ForegroundProperty));

            ReleaseNameText.SetSource(text, null);
            Assert.Equal("", Drawn());
            Assert.Equal("", Heard());
        }
        finally { window.Close(); }
    }

    private static void AssertDisabledButtonsDropTheirFill()
    {
        var button = new Button { Content = "Download Selected", IsEnabled = false, Style = (Style)Application.Current.FindResource("PrimaryButton") };
        var window = OffscreenWindow(button, 300, 80);
        try
        {
            window.Show();
            Pump();
            var bd = Assert.IsType<Border>(button.Template.FindName("Bd", button));
            Assert.Equal(0, ((SolidColorBrush)bd.Background).Color.A);
            Assert.Equal(((SolidColorBrush)Application.Current.FindResource("DisabledForegroundBrush")).Color,
                ((SolidColorBrush)button.Foreground).Color);
        }
        finally { window.Close(); }
    }

    // ── Already-open controls across live theme swaps ──────────────────────────────────────

    private static ControlTemplate? StyleTemplate(Style? style)
    {
        for (; style != null; style = style.BasedOn)
            if (style.Setters.OfType<Setter>().FirstOrDefault(s => s.Property == Control.TemplateProperty) is { } setter)
                return (ControlTemplate)setter.Value;
        return null;
    }

    private static List<Brush> PenBrushes(Visual visual)
    {
        var brushes = new List<Brush>();
        void Visit(Drawing? drawing)
        {
            if (drawing is DrawingGroup group) foreach (var child in group.Children) Visit(child);
            else if (drawing is GeometryDrawing { Pen.Brush: { } brush }) brushes.Add(brush);
        }
        Visit(VisualTreeHelper.GetDrawing(visual));
        return brushes;
    }

    private static bool Paints(List<Brush> brushes, object? resource) =>
        resource is Brush r && brushes.Any(b => ReferenceEquals(b, r) || (b is SolidColorBrush s && r is SolidColorBrush rs && s.Color == rs.Color));

    /// <summary>Pixels of the element's own retained drawing (independent of where it sits in the window).</summary>
    private static string DrawingHash(FrameworkElement element)
    {
        int w = (int)Math.Ceiling(element.ActualWidth), h = (int)Math.Ceiling(element.ActualHeight);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            if (VisualTreeHelper.GetDrawing(element) is { } drawing) dc.DrawDrawing(drawing);
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[w * h * 4];
        bitmap.CopyPixels(pixels, w * 4, 0);
        return Convert.ToHexString(SHA256.HashData(pixels));
    }

    // One set of open controls, never rebuilt, through Cyberpunk→Dark→Light→Cyberpunk and
    // Dark→Cyberpunk→Light→Dark: every theme-dependent brush and template must be the current one.
    private static void AssertOpenControlsFollowLiveThemeSwaps()
    {
        var app = Application.Current!;
        var controls = DashboardControls();
        var rows = new[]
        {
            new DownloadItemVm { Id = "s", ReleaseName = "Selected.2026.1080p.WEB-GRP", Status = "Downloading", ServerName = "site", ProgressPercent = 40 },
            new DownloadItemVm { Id = "f", ReleaseName = "Failed.2026.2160p.BluRay-GRP", Status = "Failed", ServerName = "site", ProgressPercent = 18 },
            new DownloadItemVm { Id = "q", ReleaseName = "Queued.2026.720p.WEB-GRP", Status = "Queued", ServerName = "site" },
        };
        var list = DownloadList(controls, rows, 700);

        var idle = new TelemetrySparkline { Width = 240, Height = 48 };                       // no data: AWAITING SIGNAL
        var points = new PointCollection { new(0, 35), new(1, 20), new(2, 10), new(3, 25) };
        var still = new TelemetrySparkline { Width = 240, Height = 48, YRange = 40, Points = points };   // data that never updates
        var owned = new SolidColorBrush(Color.FromRgb(0xFF, 0x80, 0x00));
        var ownedChart = new TelemetrySparkline { Width = 240, Height = 48, YRange = 40, Points = points.Clone(), LineBrush = owned };

        var mcpVm = new McpSettingsViewModel(new AppConfig { ControlApi = new() { Enabled = false, Port = 8756 } }, readLocalState: false);
        var mcp = new McpSettingsPanel { DataContext = mcpVm, Width = 560 };

        var chipVm = new SearchChipViewModel();
        var chip = ProductChip(controls, "1080p", "quality:1080p", nameof(chipVm.SearchFilterQuality), new ValueEqualsConverter(), "1080p");
        chipVm.ToggleSearchFilterCommand.Execute("quality:1080p");
        var chipHost = new StackPanel { DataContext = chipVm };
        chipHost.Children.Add(chip);

        var panel = new StackPanel();
        foreach (var element in new UIElement[] { list, idle, still, ownedChart, chipHost, mcp }) panel.Children.Add(element);
        var window = OffscreenWindow(new ScrollViewer { Content = panel }, 760, 1200);
        try
        {
            window.Show();
            Pump();
            list.SelectedItems.Add(rows[0]);
            list.SelectedItems.Add(rows[1]);
            Pump();

            // Caller-owned brushes stay mutable (rendering must not freeze them) and repaint when edited.
            Assert.False(owned.IsFrozen, "rendering froze a caller-owned brush");
            owned.Color = Colors.DodgerBlue;
            Pump();
            Assert.Contains(PenBrushes(ownedChart), b => b is SolidColorBrush s && s.Color == Colors.DodgerBlue);

            var bar = RowPart<ProgressBar>(list, rows[1], "RowProgress");
            var rail = RowPart<Rectangle>(list, rows[1], "StatusRail");
            var applyButton = Assert.IsType<Button>(mcp.FindName("ApplyButton"));
            var copyButton = Assert.IsType<Button>(mcp.FindName("CopyButton"));
            var checkButton = Assert.IsType<Button>(mcp.FindName("CheckButton"));
            var styleTemplates = new HashSet<ControlTemplate>();
            string? lastIdleHash = null;
            Color? lastGrid = null;

            string[] sequence = ["Cyberpunk", "Dark", "Light", "Cyberpunk", "Dark", "Cyberpunk", "Light", "Dark"];
            for (int i = 0; i < sequence.Length; i++)
            {
                var theme = sequence[i];
                ThemeManager.ApplyTheme(theme);
                var sparkResource = (Freezable)app.FindResource("StreamSparkBrush");
                var gridResource = (Freezable)app.FindResource("StreamGridLineBrush");
                var frozenBefore = (sparkResource.IsFrozen, gridResource.IsFrozen);
                if (i == 4) mcpVm.Enabled = true;      // an unsaved edit mid-sequence moves the primary to Apply
                Pump();
                Color Res(string key) => ((SolidColorBrush)app.FindResource(key)).Color;
                string at = $"after switching to {theme} (step {i})";

                // Same instances throughout.
                Assert.Same(bar, RowPart<ProgressBar>(list, rows[1], "RowProgress"));
                Assert.Same(points, still.Points);

                // Download row: the progress bar wears the current theme's style and template.
                var progressStyle = (Style)app.FindResource(typeof(ProgressBar));
                Assert.Same(progressStyle, bar.Style);
                var progressTemplate = StyleTemplate(progressStyle);
                if (progressTemplate != null) { Assert.Same(progressTemplate, bar.Template); styleTemplates.Add(progressTemplate); }
                else Assert.DoesNotContain(bar.Template, styleTemplates);
                Assert.Equal(Res("BadBrush"), ((SolidColorBrush)rail.Fill).Color);
                Assert.Same(rail.Fill, bar.Foreground);
                Assert.Equal(18, bar.Value, 3);
                foreach (var (row, selected) in new[] { (rows[0], true), (rows[1], true), (rows[2], false) })
                {
                    var root = RowPart<Border>(list, row, "RowRoot");
                    Assert.True(Res(selected ? "AccentDimBrush" : "SurfaceBrush") == ((SolidColorBrush)root.Background).Color,
                        $"{row.Id} row background is stale {at}");
                }

                // Checked chip keeps its drawn state in the new palette.
                Assert.Equal(true, chip.IsChecked);
                Assert.Equal(Res("AccentBrush"), ((SolidColorBrush)((Rectangle)chip.Template.FindName("led", chip)).Fill).Color);

                // MCP: the shared button styles (and their templates) of the current theme, primary where the VM says.
                foreach (var (button, primary) in new[] { (applyButton, mcpVm.HasPendingChanges), (copyButton, mcpVm.CopyIsPrimary), (checkButton, false) })
                {
                    var expected = (Style)app.FindResource(primary ? ButtonEmphasis.PrimaryKey : ButtonEmphasis.SecondaryKey);
                    Assert.True(ReferenceEquals(expected, button.Style), $"MCP '{button.Content}' style is stale {at}");
                    var template = StyleTemplate(expected);
                    if (template != null) Assert.Same(template, button.Template);
                }
                Assert.False(mcpVm.HasPendingChanges && mcpVm.CopyIsPrimary, "two primary actions");

                // Sparklines repaint from the new palette without any data or size change.
                var idlePens = PenBrushes(idle);
                Assert.True(Paints(idlePens, gridResource), $"idle chart grid is stale {at}");
                Assert.True(Paints(PenBrushes(still), sparkResource), $"chart line is stale {at}");
                Assert.True(Paints(PenBrushes(still), gridResource), $"chart grid is stale {at}");
                Assert.Same(owned, ownedChart.LineBrush);                  // a caller's own brush is respected
                Assert.Equal(frozenBefore, (sparkResource.IsFrozen, gridResource.IsFrozen));   // render froze no theme brush
                var idleHash = DrawingHash(idle);
                var gridColor = (gridResource as SolidColorBrush)?.Color;
                if (lastIdleHash != null && gridColor != lastGrid)
                    Assert.True(idleHash != lastIdleHash, $"idle chart pixels did not change {at}");
                (lastIdleHash, lastGrid) = (idleHash, gridColor);
            }
            Assert.True(mcpVm.HasPendingChanges);
        }
        finally { window.Close(); }
    }

    private static Window OffscreenWindow(object content, double width, double height) => new()
    {
        Content = content, Width = width, Height = height,
        ShowInTaskbar = false, ShowActivated = false, Left = -20000, Top = -20000,
    };

    private static double Contrast(Color a, Color b)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        static double Luminance(Color c) => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        double l1 = Luminance(a), l2 = Luminance(b);
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }
}
