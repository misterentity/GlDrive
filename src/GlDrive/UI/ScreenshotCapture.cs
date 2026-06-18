using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Services;
using GlDrive.Tls;

namespace GlDrive.UI;

internal static class ScreenshotCapture
{
    private static readonly string OutputDir = Path.Combine(ConfigManager.AppDataPath, "screenshots");

    internal static void CaptureAll(AppConfig config)
    {
        Directory.CreateDirectory(OutputDir);

        // Apply the user's saved theme before capturing. Without this, screenshots
        // render with only DarkTheme.xaml (App.xaml's default merged dictionary)
        // because our --screenshots startup path returns BEFORE App.xaml.cs's
        // normal flow calls ThemeManager.ApplyTheme. End result: screenshots
        // showed basic dark styling but missed the 69KB CyberpunkTheme.xaml
        // brushes / effects / control templates that the actual running app
        // uses. The 'screenshots show the old UI' complaint was this mismatch.
        var themeName = config.Downloads.Theme;
        if (string.IsNullOrEmpty(themeName)) themeName = "Cyberpunk";
        try { ThemeManager.ApplyTheme(themeName); }
        catch (Exception ex) { Serilog.Log.Debug(ex, "Screenshot: theme apply failed (non-fatal)"); }

        var demoConfig = BuildDemoConfig();
        // Ensure the demo dashboard also reads the chosen theme, in case any
        // code path reads from config.Downloads.Theme during render.
        demoConfig.Downloads.Theme = themeName;
        CaptureWizard();
        CaptureDashboard(demoConfig);
        CaptureSettings(demoConfig);
    }

    private static AppConfig BuildDemoConfig()
    {
        var config = new AppConfig();
        config.Servers.Add(new ServerConfig
        {
            Name = "My Server",
            Enabled = true,
            Connection = { Host = "ftp.example.com", Port = 21, Username = "user" },
            Mount = { DriveLetter = "G", VolumeLabel = "glFTPd" }
        });
        config.Logging.Level = "Information";
        return config;
    }

    private static void CaptureWizard()
    {
        var names = new[] { "welcome", "connection", "tls", "mount", "confirm" };
        var wizard = new WizardWindow { DemoMode = true };
        wizard.Show();
        wizard.PreFillDemo();

        for (var i = 0; i < 5; i++)
        {
            wizard.ShowStep(i);
            wizard.UpdateLayout();
            DoEvents();
            RenderToFile(wizard, $"wizard-{i + 1}-{names[i]}.png");
        }

        wizard.Close();
    }

    private static void CaptureDashboard(AppConfig config)
    {
        var certManager = new CertificateManager();
        // persistToDisk=false: demo entries we Add() must NOT leak to the user's
        // real notifications.json. Same data populates the Notifications tab.
        var notificationStore = new NotificationStore(persistToDisk: false);
        SeedNotifications(notificationStore);

        var serverManager = new ServerManager(config, certManager, notificationStore);
        var dashboard = new DashboardWindow(serverManager, config, notificationStore);
        dashboard.Show();
        DoEvents();

        // Inject demo data into the VM's ObservableCollections.
        //
        // SECURITY: the Wishlist collection is populated by DashboardViewModel's
        // constructor from WishlistStore which Load()s the user's REAL
        // wishlist.json — that would leak the user's actual viewing interests
        // into a public README screenshot. We clear and replace with neutral
        // demo titles. The disk file is NOT touched (we only mutate the
        // ObservableCollection used for binding).
        if (dashboard.DataContext is DashboardViewModel vm)
        {
            foreach (var dl in BuildDemoDownloads()) vm.DownloadItems.Add(dl);
            foreach (var sr in BuildDemoSearchResults()) vm.SearchResults.Add(sr);
            vm.WishlistItems.Clear();
            foreach (var w in BuildDemoWishlist()) vm.WishlistItems.Add(w);
        }
        DoEvents();

        // Tab headers in DashboardWindow.xaml include disabled "section header"
        // TabItems (+ DASHBOARD, + TRANSFERS, + MEDIA, + COMMS) interleaved with
        // the real tabs. Selecting by index landed on disabled headers and WPF
        // silently fell back to whichever real tab was nearest — every screenshot
        // came out as the Overview tab. Select by header text instead.
        var tabControl = FindTabControl(dashboard);

        var dashboardTabs = new[]
        {
            ("notifications", "Notifications"),
            ("wishlist",      "Wishlist"),
            ("downloads",     "Downloads"),
            ("search",        "Search"),
            ("upcoming",      "Upcoming"),
            ("plex",          "Plex"),
        };

        if (tabControl != null)
        {
            foreach (var (file, header) in dashboardTabs)
            {
                if (!SelectTabByHeader(tabControl, header)) continue;
                dashboard.UpdateLayout();
                DoEvents();
                RenderToFile(dashboard, $"dashboard-{file}.png");
            }
        }

        dashboard.Close();
        serverManager.Dispose();
    }

    /// <summary>
    /// Select the first enabled TabItem in <paramref name="tabControl"/> whose
    /// header Grid contains a TextBlock with text matching
    /// <paramref name="targetText"/> (case-insensitive). Skips disabled
    /// section-header TabItems (the "+ DASHBOARD", "+ MEDIA" etc. dividers).
    /// </summary>
    private static bool SelectTabByHeader(TabControl tabControl, string targetText)
    {
        foreach (var item in tabControl.Items)
        {
            if (item is not TabItem ti || !ti.IsEnabled) continue;
            if (ExtractHeaderText(ti.Header).Contains(targetText, StringComparison.OrdinalIgnoreCase))
            {
                ti.IsSelected = true;
                return true;
            }
        }
        return false;
    }

    private static string ExtractHeaderText(object? header)
    {
        return header switch
        {
            null => "",
            string s => s,
            TextBlock tb => tb.Text ?? "",
            Panel panel => string.Join(" ",
                panel.Children.OfType<DependencyObject>()
                    .SelectMany(c => DescendantTextBlocks(c))
                    .Select(tb => tb.Text ?? "")),
            DependencyObject dep => string.Join(" ",
                DescendantTextBlocks(dep).Select(tb => tb.Text ?? "")),
            _ => header.ToString() ?? ""
        };
    }

    private static IEnumerable<TextBlock> DescendantTextBlocks(DependencyObject node)
    {
        if (node is TextBlock t) yield return t;
        if (node is Panel p)
            foreach (var c in p.Children.OfType<DependencyObject>())
                foreach (var d in DescendantTextBlocks(c)) yield return d;
        else if (node is ContentControl cc && cc.Content is DependencyObject ccd)
            foreach (var d in DescendantTextBlocks(ccd)) yield return d;
    }

    private static void SeedNotifications(NotificationStore store)
    {
        var now = DateTime.UtcNow;
        var demo = new (string Cat, string Release, int MinutesAgo)[]
        {
            ("x265",     "Sinners.2026.2160p.UHD.BluRay.x265-LIGHTBRiNGER",                      2),
            ("flac",     "Phoebe_Bridgers-Punisher_Live_From_The_Tropicana-WEB-FLAC-2026-MTD",   5),
            ("tv-hd",    "The.Outer.Worlds.S03E04.1080p.WEB.H264-CAKES",                         8),
            ("mp3",      "Aphex_Twin-Selected_Algorithm_Works-WEB-2026-FALCON",                 11),
            ("bookware", "Pluralsight.Distributed.Systems.Fundamentals.2026.BOOKWARE-GETH",     14),
            ("x264-hd",  "Dune.Part.Three.2026.1080p.BluRay.H264-RiSEHD",                       18),
            ("games",    "Hades.III.Update.v1.0.4-RAZOR1911",                                   24),
            ("tv-hd",    "Severance.S03E08.1080p.WEB.H264-NTb",                                 31),
        };
        foreach (var (cat, rel, mins) in demo)
        {
            store.Add(new NotificationItem
            {
                ServerId = "demo-site-a",
                ServerName = "site-a",
                Category = cat,
                ReleaseName = rel,
                RemotePath = $"/recent/{cat}/{rel}",
                Timestamp = now.AddMinutes(-mins),
            });
        }
    }

    private static IEnumerable<DownloadItemVm> BuildDemoDownloads()
    {
        yield return new DownloadItemVm
        {
            Id = "d1",
            ReleaseName = "Sinners.2026.2160p.UHD.BluRay.x265-LIGHTBRiNGER",
            Category = "x265",
            ServerName = "site-a",
            Status = "Downloading",
            ProgressPercent = 64.2,
            ProgressText = "12.8 GiB / 19.9 GiB",
            SpeedDisplay = "84.2 MiB/s",
            LocalPath = @"D:\Movies\Sinners.2026.2160p.UHD.BluRay.x265-LIGHTBRiNGER",
        };
        yield return new DownloadItemVm
        {
            Id = "d2",
            ReleaseName = "The.Outer.Worlds.S03E04.1080p.WEB.H264-CAKES",
            Category = "tv-hd",
            ServerName = "site-a",
            Status = "Downloading",
            ProgressPercent = 31.7,
            ProgressText = "456 MiB / 1.44 GiB",
            SpeedDisplay = "42.1 MiB/s",
            LocalPath = @"D:\TV\The.Outer.Worlds.S03E04.1080p.WEB.H264-CAKES",
        };
        yield return new DownloadItemVm
        {
            Id = "d3",
            ReleaseName = "Aphex_Twin-Selected_Algorithm_Works-WEB-2026-FALCON",
            Category = "mp3",
            ServerName = "site-a",
            Status = "Extracting",
            ProgressPercent = 100,
            ProgressText = "172 MiB / 172 MiB",
            SpeedDisplay = "",
            LocalPath = @"D:\Music\Aphex_Twin-Selected_Algorithm_Works-WEB-2026-FALCON",
        };
        yield return new DownloadItemVm
        {
            Id = "d4",
            ReleaseName = "Severance.S03E07.1080p.WEB.H264-NTb",
            Category = "tv-hd",
            ServerName = "site-b",
            Status = "Completed",
            ProgressPercent = 100,
            ProgressText = "1.31 GiB / 1.31 GiB",
            SpeedDisplay = "",
            LocalPath = @"D:\TV\Severance.S03E07.1080p.WEB.H264-NTb",
        };
        yield return new DownloadItemVm
        {
            Id = "d5",
            ReleaseName = "Hades.III.Update.v1.0.4-RAZOR1911",
            Category = "games",
            ServerName = "site-c",
            Status = "Queued",
            ProgressPercent = 0,
            ProgressText = "0 B / 2.8 GiB",
            SpeedDisplay = "",
            LocalPath = @"D:\Games\Hades.III.Update.v1.0.4-RAZOR1911",
        };
        yield return new DownloadItemVm
        {
            Id = "d6",
            ReleaseName = "Dune.Part.Three.2026.1080p.BluRay.H264-RiSEHD",
            Category = "x264-hd",
            ServerName = "site-a",
            Status = "Failed",
            ProgressPercent = 18.4,
            ProgressText = "1.8 GiB / 9.7 GiB",
            SpeedDisplay = "",
            LocalPath = @"D:\Movies\Dune.Part.Three.2026.1080p.BluRay.H264-RiSEHD",
        };
    }

    private static IEnumerable<WishlistItemVm> BuildDemoWishlist()
    {
        yield return new WishlistItemVm { Id = "w1",  Title = "Sample Sci-Fi Drama",            Type = "TvShow", Year = "2026", Quality = "Q1080p", Status = "Watching",  GrabbedCount = "8" };
        yield return new WishlistItemVm { Id = "w2",  Title = "Demo Crime Anthology",           Type = "TvShow", Year = "2025", Quality = "Q1080p", Status = "Watching",  GrabbedCount = "3" };
        yield return new WishlistItemVm { Id = "w3",  Title = "Placeholder Feature Film",       Type = "Movie",  Year = "2026", Quality = "Q2160p", Status = "Watching",  GrabbedCount = "1" };
        yield return new WishlistItemVm { Id = "w4",  Title = "Fictitious Heist Movie",         Type = "Movie",  Year = "2026", Quality = "Q1080p", Status = "Completed", GrabbedCount = "1" };
        yield return new WishlistItemVm { Id = "w5",  Title = "Imaginary Period Drama",         Type = "TvShow", Year = "2025", Quality = "Q1080p", Status = "Watching",  GrabbedCount = "5" };
        yield return new WishlistItemVm { Id = "w6",  Title = "Made-Up Animated Series",        Type = "TvShow", Year = "2026", Quality = "Q720p",  Status = "Watching",  GrabbedCount = "12" };
        yield return new WishlistItemVm { Id = "w7",  Title = "Notional Documentary",           Type = "Movie",  Year = "2024", Quality = "Q1080p", Status = "Completed", GrabbedCount = "1" };
        yield return new WishlistItemVm { Id = "w8",  Title = "Hypothetical Thriller",          Type = "Movie",  Year = "2026", Quality = "Q2160p", Status = "Watching",  GrabbedCount = "0" };
        yield return new WishlistItemVm { Id = "w9",  Title = "Generic Comedy Special",         Type = "Movie",  Year = "2025", Quality = "Q1080p", Status = "Completed", GrabbedCount = "1" };
        yield return new WishlistItemVm { Id = "w10", Title = "Synthetic Mystery Miniseries",   Type = "TvShow", Year = "2026", Quality = "Q1080p", Status = "Watching",  GrabbedCount = "2" };
        yield return new WishlistItemVm { Id = "w11", Title = "Placeholder Action Sequel",      Type = "Movie",  Year = "2026", Quality = "Q2160p", Status = "Watching",  GrabbedCount = "0" };
        yield return new WishlistItemVm { Id = "w12", Title = "Test-Pattern Reality Show",      Type = "TvShow", Year = "2025", Quality = "Q720p",  Status = "Paused",    GrabbedCount = "4" };
    }

    private static IEnumerable<SearchResultVm> BuildDemoSearchResults()
    {
        long Gib(double g) => (long)(g * 1024 * 1024 * 1024);
        yield return new SearchResultVm { ReleaseName = "Sinners.2026.2160p.UHD.BluRay.x265-LIGHTBRiNGER", Category = "x265", RemotePath = "/x265/Sinners.2026.2160p.UHD.BluRay.x265-LIGHTBRiNGER", Size = Gib(19.9), SizeText = "19.9 GiB", ServerName = "site-a" };
        yield return new SearchResultVm { ReleaseName = "Sinners.2026.1080p.BluRay.H264-RiSEHD",            Category = "x264-hd", RemotePath = "/x264-hd/Sinners.2026.1080p.BluRay.H264-RiSEHD", Size = Gib(9.4), SizeText = "9.4 GiB", ServerName = "site-a" };
        yield return new SearchResultVm { ReleaseName = "Sinners.2026.1080p.WEB.H264-NAISU",                 Category = "x264-hd", RemotePath = "/x264-hd/Sinners.2026.1080p.WEB.H264-NAISU", Size = Gib(7.1), SizeText = "7.1 GiB", ServerName = "site-b" };
        yield return new SearchResultVm { ReleaseName = "Sinners.2026.720p.BluRay.H264-NAISU",               Category = "x264-hd", RemotePath = "/x264-hd/Sinners.2026.720p.BluRay.H264-NAISU", Size = Gib(4.6), SizeText = "4.6 GiB", ServerName = "site-b" };
        yield return new SearchResultVm { ReleaseName = "Sinners.2026.MULTi.1080p.BluRay.x265-LIGHTBRiNGER", Category = "x265", RemotePath = "/x265/Sinners.2026.MULTi.1080p.BluRay.x265-LIGHTBRiNGER", Size = Gib(13.2), SizeText = "13.2 GiB", ServerName = "site-c" };
        yield return new SearchResultVm { ReleaseName = "Sinners.2026.PROPER.2160p.UHD.BluRay.DV.HDR.x265-NCmt", Category = "x265", RemotePath = "/x265/Sinners.2026.PROPER.2160p.UHD.BluRay.DV.HDR.x265-NCmt", Size = Gib(28.7), SizeText = "28.7 GiB", ServerName = "site-c" };
        yield return new SearchResultVm { ReleaseName = "Sinners.2026.BDRip.x264-PUTRiD",                    Category = "x264-sd", RemotePath = "/x264-sd/Sinners.2026.BDRip.x264-PUTRiD", Size = Gib(1.5), SizeText = "1.5 GiB", ServerName = "site-b" };
    }

    private static void CaptureSettings(AppConfig config)
    {
        var certManager = new CertificateManager();
        var notificationStore = new NotificationStore();
        var serverManager = new ServerManager(config, certManager, notificationStore);
        var settings = new SettingsWindow(config, serverManager);
        settings.Show();
        DoEvents();

        var tabControl = FindTabControl(settings);

        var settingsTabs = new[]
        {
            ("servers",     "Servers"),
            ("performance", "Performance"),
            ("downloads",   "Downloads"),
            ("diagnostics", "Diagnostics"),
        };

        if (tabControl != null)
        {
            foreach (var (file, header) in settingsTabs)
            {
                if (!SelectTabByHeader(tabControl, header)) continue;
                settings.UpdateLayout();
                DoEvents();
                RenderToFile(settings, $"settings-{file}.png");
            }
        }

        settings.Close();
        serverManager.Dispose();
    }

    private static void RenderToFile(Window window, string filename)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        var width = (int)(window.ActualWidth * dpi.DpiScaleX);
        var height = (int)(window.ActualHeight * dpi.DpiScaleY);

        var rtb = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        rtb.Render(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));

        var path = Path.Combine(OutputDir, filename);
        using var fs = new FileStream(path, FileMode.Create);
        encoder.Save(fs);
    }

    private static TabControl? FindTabControl(Window window)
    {
        return FindVisualChild<TabControl>(window);
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            var result = FindVisualChild<T>(child);
            if (result != null) return result;
        }
        return null;
    }

    private static void DoEvents()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new System.Windows.Threading.DispatcherOperationCallback(_ =>
            {
                frame.Continue = false;
                return null;
            }), null);
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }
}
