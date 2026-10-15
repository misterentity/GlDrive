using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Downloads.Segmented;
using GlDrive.UI;
using GlDrive.UI.StreamMatrix;
using Xunit;

namespace GlDrive.Tests;

[CollectionDefinition("WpfRender", DisableParallelization = true)]
public sealed class WpfRenderCollection { }

/// <summary>
/// Renders the multi-stream UI (stream matrix, a downloads row with lane chips, the
/// STREAMS &amp; UPLINKS settings panel) with fake data in every theme, through the real App
/// resources and ThemeManager, to PNGs under %TEMP%\gldrive-ui-render. Doubles as a check that
/// the XAML loads and every new theme key exists in all three dictionaries.
/// </summary>
[Collection("WpfRender")]
public class StreamMatrixRenderTests
{
    public static readonly string OutputDir = Path.Combine(Path.GetTempPath(), "gldrive-ui-render");

    private static readonly string[] NewKeys =
    [
        "LaneBrush0", "LaneBrush1", "LaneBrush2", "LaneBrush3", "LaneBrush4", "LaneBrush5",
        "LaneGlowEffect", "LaneGlowOpacity",
        "StreamMatrixBackgroundBrush", "StreamMatrixHeaderBrush", "StreamMatrixBorderBrush",
        "StreamCellPendingBrush", "StreamCellCachedBrush", "StreamFileSeparatorBrush", "StreamGridLineBrush",
        "StreamSparkBrush", "StreamSparkFillBrush", "StreamTextBrush", "StreamTextDimBrush", "StreamTextFaintBrush",
        "StreamCommentBrush", "StreamGoodBrush", "StreamWarnBrush", "StreamBadBrush",
        "StreamLedOnBrush", "StreamLedOffBrush", "StreamLedGlowEffect",
        "StreamUsageBarBrush", "StreamUsageTrackBrush", "StreamChipBackgroundBrush", "StreamMonoFontFamily",
    ];

    [Fact]
    public void RendersEveryThemeWithAllKeys()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Assert.Null(Application.Current);
                RenderAll();
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                // Configuration/snapshot automation dispatches through Application.Current.
                // Leaving this singleton attached to a departed STA strands those operations
                // forever. Shutdown posts a callback, so pump it before stopping the dispatcher.
                try
                {
                    if (Application.Current is { } application)
                    {
                        Assert.True(application.Dispatcher.CheckAccess(), "The render test must own its Application dispatcher.");
                        application.Shutdown();
                        Pump();
                    }
                    Assert.Null(Application.Current);
                    var dispatcher = Dispatcher.CurrentDispatcher;
                    dispatcher.InvokeShutdown();
                    Assert.True(dispatcher.HasShutdownFinished);
                }
                catch (Exception cleanupFailure)
                {
                    failure = failure == null ? cleanupFailure : new AggregateException(failure, cleanupFailure);
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(90)), "UI render hung (a modal dialog or a stuck dispatcher)");
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
        Assert.Null(Application.Current);
    }

    private static void RenderAll()
    {
        // A plain Application carrying App.xaml's resources. Never instantiate GlDrive.App here:
        // its OnStartup runs on the first dispatcher pump and is the production startup path.
        if (Application.Current == null)
        {
            // Pack URIs like "pack://application:,,,/UI/Themes/..." resolve against this, not the test host.
            typeof(Application).GetField("_resourceAssembly", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .SetValue(null, typeof(App).Assembly);
            new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, Resources = LoadAppResources() };
        }
        Directory.CreateDirectory(OutputDir);

        var application = Application.Current!;
        var originalButtonStyle = application.Resources[typeof(Button)];
        var originalScanline = application.TryFindResource("ScanlineOverlayBrush");
        // Switch away from Cyberpunk as well: its theme-only resources must not leak
        // into the light or dark UI after a live theme change.
        foreach (var theme in new[] { "Cyberpunk", "Dark", "Light", "Cyberpunk" })
        {
            var dict = new ResourceDictionary { Source = new Uri($"pack://application:,,,/GlDrive;component/UI/Themes/{theme}Theme.xaml") };
            var missing = NewKeys.Where(k => !dict.Contains(k)).ToList();
            Assert.True(missing.Count == 0, $"{theme} theme is missing: {string.Join(", ", missing)}");

            ThemeManager.ApplyTheme(theme);
            var matrix = BuildMatrix();
            var name = theme.ToLowerInvariant();

            Render(new StreamMatrixView { DataContext = matrix, Width = 1100 }, $"multistream-matrix-{name}.png");
            Render(BuildRows(matrix), $"multistream-row-{name}.png");
            Render(BuildSettings(), $"multistream-settings-{name}.png");
        }

        AssertUplinkTableFitsNarrowSettings();
        ThemeManager.ApplyTheme("Light");
        Assert.Same(originalScanline, application.TryFindResource("ScanlineOverlayBrush"));
        Assert.Null(application.TryFindResource("CyberpunkPrimaryButtonStyle"));
        Assert.Same(originalButtonStyle, application.Resources[typeof(Button)]);
    }

    // Settings opens no narrower than MinWidth=900; after the window and tab chrome the panel gets
    // well under 800px. The uplink table's fixed columns once took all of it, collapsing the
    // adapter column to zero so you couldn't tell which network a row edited (Codex UI review).
    private static void AssertUplinkTableFitsNarrowSettings()
    {
        var panel = BuildSettings();
        panel.Width = 760;
        var root = new Window
        {
            Content = panel, SizeToContent = SizeToContent.WidthAndHeight, WindowStyle = WindowStyle.None,
            ShowInTaskbar = false, ShowActivated = false, Left = -20000, Top = -20000,
        };
        root.Show();
        Pump();
        panel.UpdateLayout();
        var names = FindAll<TextBlock>(panel).Where(t => t.Text is "Ethernet" or "Cellular (5G tether)").ToList();
        root.Close();
        Assert.NotEmpty(names);
        Assert.All(names, t => Assert.True(t.ActualWidth >= 100, $"adapter column only {t.ActualWidth:0}px wide at 760px"));
    }

    // A 6-volume release mid-transfer: Ethernet and the race login racing through it, a tether
    // lane that yielded early, and the first volume resumed from an earlier session.
    private static StreamMatrixViewModel BuildMatrix()
    {
        const long Vol = 400L << 20;
        long[] sizes = [Vol, Vol, Vol, Vol, Vol, Vol / 2, 4096, 812];
        var names = new[] { "rel.r00", "rel.r01", "rel.r02", "rel.r03", "rel.r04", "rel.r05", "rel.sfv", "rel.nfo" };
        var vm = new StreamMatrixViewModel("Backrooms.2026.1080p.BluRay.x264-GRP");
        var done = sizes.Select(_ => new ByteRangeSet()).ToArray();
        done[0].Add(new ByteRange(0, 150L << 20)); // resumed

        // lane -> (file, start, pos, end)
        var heads = new Dictionary<string, (int File, long Start, long Pos, long End)>
        {
            ["Ethernet"] = (0, 150L << 20, 150L << 20, Vol),
            ["race login"] = (1, 0, 0, Vol),
            ["Tether"] = (2, 0, 0, Vol),
        };
        var rates = new Dictionary<string, double> { ["Ethernet"] = 42.1, ["race login"] = 18.4, ["Tether"] = 6.2 };
        var bytes = heads.Keys.ToDictionary(k => k, _ => 0L);
        int nextFile = 3;
        var rng = new Random(7);

        for (int tick = 0; tick < 120; tick++)
        {
            foreach (var lane in heads.Keys.ToList())
            {
                if (lane == "Tether" && tick >= 80) continue; // yielded
                var h = heads[lane];
                double jitter = 0.75 + rng.NextDouble() * 0.5 + Math.Sin(tick / 9.0 + lane.Length) * 0.15;
                long step = (long)(rates[lane] * jitter * (1 << 20) / 6);
                long to = Math.Min(h.End, h.Pos + step);
                done[h.File].Add(new ByteRange(h.Pos, to));
                bytes[lane] += to - h.Pos;
                h.Pos = to;
                if (h.Pos >= h.End && nextFile < sizes.Length)
                    h = (nextFile, 0, 0, sizes[nextFile++]);
                heads[lane] = h;
            }

            var live = heads.Where(kv => kv.Key != "Tether" || tick < 80).ToList();
            var map = new TransferMap(
                sizes.Select((s, i) => new FileMap(names[i], s, done[i].Ranges.ToArray())).ToList(),
                live.Where(kv => kv.Value.Pos < kv.Value.End)
                    .Select(kv => new SegmentSnapshot(kv.Value.File, kv.Value.Start, kv.Value.Pos, kv.Value.End, kv.Key,
                        kv.Key == "Ethernet" ? "{8A3F21C0-4C1E-4B7A-9D55-0E2F7A1B3C4D}" : kv.Key == "Tether" ? "{5D0C9E77-1A2B-4C3D-8E9F-102030405060}" : null))
                    .ToList());
            var lanes = new List<LaneStats>
            {
                new("Ethernet", "{8A3F21C0-4C1E-4B7A-9D55-0E2F7A1B3C4D}", bytes["Ethernet"], rates["Ethernet"] * (1 << 20) * (0.9 + 0.2 * Math.Sin(tick / 7.0)), LaneState.Active),
                new("race login", "default", bytes["race login"], rates["race login"] * (1 << 20) * (0.9 + 0.2 * Math.Cos(tick / 5.0)), LaneState.Active),
                new("Tether", "{5D0C9E77-1A2B-4C3D-8E9F-102030405060}", bytes["Tether"], tick < 80 ? rates["Tether"] * (1 << 20) : 0, tick < 80 ? LaneState.Active : LaneState.Yielded),
            };
            long doneBytes = done.Sum(d => d.TotalLength);
            double agg = lanes.Where(l => l.State == LaneState.Active).Sum(l => l.BytesPerSecond);
            vm.Update(new DownloadProgress(doneBytes, sizes.Sum(), agg, names[Math.Min(nextFile - 1, names.Length - 1)], lanes, map), force: true);
        }
        return vm;
    }

    private static ResourceDictionary LoadAppResources([System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        var xaml = File.ReadAllText(Path.Combine(Path.GetDirectoryName(here)!, "..", "GlDrive", "App.xaml"));
        int open = xaml.IndexOf("<Application.Resources>", StringComparison.Ordinal) + "<Application.Resources>".Length;
        int close = xaml.IndexOf("</Application.Resources>", StringComparison.Ordinal);
        var dict = xaml[open..close].Trim()
            .Replace("Source=\"UI/Themes/", "Source=\"pack://application:,,,/GlDrive;component/UI/Themes/")
            .Replace("<ResourceDictionary>",
                "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
                "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">", StringComparison.Ordinal);
        return (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(dict);
    }

    private static FrameworkElement BuildRows(StreamMatrixViewModel matrix)
    {
        var lanes = matrix.Lanes.Count;
        var multi = new DownloadItemVm
        {
            ReleaseName = matrix.ReleaseName, Status = "Downloading", ServerName = "site-a", Category = "x264-hd",
            ProgressText = $"{matrix.Percent:0}% — rel.r04", ProgressPercent = matrix.Percent, SpeedDisplay = "60.5 MB/s",
            Matrix = matrix,
            StreamsText = "2 streams · Ethernet ×1, race login ×1 · 1 yielded",
        };
        var http = new StreamMatrixViewModel("GlDrive-3.10.160-update.zip");
        http.Update(new DownloadProgress(70L << 20, 120L << 20, 31L << 20, "update.zip",
        [
            new LaneStats("Ethernet", "{8A3F21C0}", 40L << 20, 12.5 * (1 << 20), LaneState.Active),
            new LaneStats("Ethernet", "{8A3F21C0}", 30L << 20, 11.0 * (1 << 20), LaneState.Active),
            new LaneStats("5G tether", "{5D0C9E77}", 10L << 20, 7.6 * (1 << 20), LaneState.Active),
        ]), force: true);
        var second = new DownloadItemVm
        {
            ReleaseName = http.ReleaseName, Status = "Downloading", ServerName = "github.com", Category = "update",
            ProgressText = "58% — update.zip", ProgressPercent = 58, SpeedDisplay = "31.0 MB/s", Matrix = http,
            StreamsText = "3 streams · Ethernet ×2, 5G tether ×1",
        };
        var panel = new StackPanel { Width = 1100 };
        panel.Children.Add(new DownloadRowView { DataContext = multi });
        panel.Children.Add(new DownloadRowView { DataContext = second });
        return panel;
    }

    private static FrameworkElement BuildSettings()
    {
        var config = new AppConfig();
        config.Downloads.SpeedLimitKbps = 0;
        config.Downloads.MultiStream.SlowModeKbps = 2048;
        var vm = new SettingsViewModel(config, readLocalState: false);
        vm.Networks.Clear();
        const long GiB = 1L << 30;
        vm.Networks.Add(new NetworkRowViewModel
        {
            AdapterId = "{8A3F21C0}", AdapterName = "Ethernet", IPv4 = "192.168.1.20", IsConnected = true,
            Use = true, FriendlyName = "Fiber", LimitKbps = "0", AllowanceGb = "0", UsedBytes = 412 * GiB,
        });
        vm.Networks.Add(new NetworkRowViewModel
        {
            AdapterId = "{5D0C9E77}", AdapterName = "Cellular (5G tether)", IPv4 = "10.84.3.117", IsConnected = true,
            Use = true, FriendlyName = "5G tether", LimitKbps = "8192", AllowanceGb = "50", Period = AllowancePeriod.Monthly,
            UsedBytes = 31 * GiB + GiB / 2,
        });
        vm.Networks.Add(new NetworkRowViewModel
        {
            AdapterId = "{77AA0011}", AdapterName = "Wi-Fi 2 (hotel)", IsConnected = false,
            Use = false, FriendlyName = "", LimitKbps = "0", AllowanceGb = "5", Period = AllowancePeriod.Daily,
            UsedBytes = 6 * GiB,
        });
        return new StreamsSettingsPanel { DataContext = vm, Width = 1100 };
    }

    private static void Render(FrameworkElement content, string file)
    {
        var host = new Border { Padding = new Thickness(16), Child = content };
        host.SetResourceReference(Border.BackgroundProperty, "BackgroundBrush");
        var root = new Window
        {
            Content = host, SizeToContent = SizeToContent.WidthAndHeight, WindowStyle = WindowStyle.None,
            ShowInTaskbar = false, ShowActivated = false, Left = -20000, Top = -20000,
        };
        root.Show();
        Pump();
        host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        host.Arrange(new Rect(host.DesiredSize));
        host.UpdateLayout();
        Pump();
        foreach (var grid in FindAll<SegmentGridControl>(host)) grid.PulsePhase = 0.25;
        host.InvalidateVisual();
        Pump();

        const double scale = 1.5;
        var w = (int)Math.Ceiling(host.ActualWidth * scale);
        var h = (int)Math.Ceiling(host.ActualHeight * scale);
        var rtb = new RenderTargetBitmap(w, h, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(host);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = File.Create(Path.Combine(OutputDir, file))) encoder.Save(fs);
        root.Close();
    }

    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var d in FindAll<T>(child)) yield return d;
        }
    }

    private static void Pump() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
}
