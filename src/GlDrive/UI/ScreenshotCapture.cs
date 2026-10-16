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
    private static RenderArtifactSession? _artifacts;

    internal static void CaptureAll(AppConfig config, string? themeOverride = null, string? outputDirectory = null)
    {
        var outputRoot = Path.Combine(ConfigManager.AppDataPath, "screenshots");
        if (themeOverride is "Dark" or "Light" or "Cyberpunk")
            outputRoot = Path.Combine(outputRoot, themeOverride);
        using var artifacts = new RenderArtifactSession(outputRoot, outputDirectory);
        _artifacts = artifacts;
        try
        {
            // Apply the user's saved theme before capturing. Without this, screenshots
            // render with only DarkTheme.xaml (App.xaml's default merged dictionary)
            // because our --screenshots startup path returns BEFORE App.xaml.cs's
            // normal flow calls ThemeManager.ApplyTheme. End result: screenshots
            // showed basic dark styling but missed the 69KB CyberpunkTheme.xaml
            // brushes / effects / control templates that the actual running app
            // uses. The 'screenshots show the old UI' complaint was this mismatch.
            var themeName = themeOverride ?? config.Downloads.Theme;
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
            CaptureUtilityWindows(demoConfig);
            artifacts.Complete();
        }
        finally { _artifacts = null; }
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
        config.Downloads.LocalPath = @"C:\Downloads\GlDrive";
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
        var dashboard = new DashboardWindow(serverManager, config, notificationStore, demoMode: true);
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
            vm.MountedServerStatus.Clear();
            vm.MountedServerStatus.Add(new OverviewServerVm
            {
                Name = "My Server", Host = "ftp.example.com:21", StatusLine = "OFFLINE", SiteTag = "OFFLINE",
                CapacityUsedDisplay = "0", CapacityTotalDisplay = "4"
            });
            dashboard.DataContext = null;
            dashboard.DataContext = vm;
        }
        DoEvents();
        // Show the selected-row treatment in the Downloads captures.
        if (dashboard.FindName("DownloadsGrid") is ListBox demoDownloads && demoDownloads.Items.Count > 1)
            demoDownloads.SelectedIndex = 1;

        using var demoBrowse = new BrowseViewModel(serverManager, config);
        ((TabItem)dashboard.FindName("BrowseTab")).DataContext = demoBrowse;
        using var demoPlayer = new PlayerViewModel(serverManager, config);
        demoPlayer.SelectedMovie = new MediaCardVm { Title = "Sample Open Film", Year = "2026" };
        demoPlayer.TorrentResults.Add(new TorrentResultVm
        {
            Title = "Sample.Open.Film.2026.1080p.WEB-GROUP", Seeds = 24, Leeches = 3,
            Size = "1.2 GiB", Uploader = "Demo indexer", DetailUrl = "https://example.invalid/demo"
        });
        demoPlayer.SelectedTorrentResult = demoPlayer.TorrentResults[0];
        demoPlayer.PlayerStatus = "Choose Play Torrent or queue its content in Downloads";
        ((Grid)dashboard.FindName("PlayerTab")).DataContext = demoPlayer;
        if (dashboard.FindName("NowPlayingMode") is System.Windows.Controls.Primitives.ToggleButton nowPlaying)
            nowPlaying.IsChecked = true;
        using var demoSpread = new SpreadViewModel(serverManager, config);
        demoSpread.RaceHistory.Clear();
        demoSpread.SessionSummary = "Demo session: 1 active race";
        demoSpread.SpreadSections.Add("MOVIES");
        demoSpread.SelectedSection = "MOVIES";
        demoSpread.SpreadJobs.Add(new GlDrive.Spread.SpreadJobVm
        {
            Id = "demo-race", Release = "Sample.Movie.2026.1080p.WEB-GROUP", Section = "MOVIES",
            ProgressPercent = 48, Status = "Transferring", SourceDisplay = "site-a", DestDisplay = "site-b",
            FilesDoneTotalDisplay = "24 / 50", Score = 32000, ScoreLabel = "32,000"
        });
        ((TabItem)dashboard.FindName("SpreadTab")).DataContext = demoSpread;

        // Tab headers in DashboardWindow.xaml include disabled "section header"
        // TabItems (+ DASHBOARD, + TRANSFERS, + MEDIA, + COMMS) interleaved with
        // the real tabs. Selecting by index landed on disabled headers and WPF
        // silently fell back to whichever real tab was nearest — every screenshot
        // came out as the Overview tab. Select by header text instead.
        var tabControl = FindTabControl(dashboard);

        var dashboardTabs = new[]
        {
            ("overview", "Overview"),
            ("player", "Player"),
            ("spread", "Spread"),
            ("notifications", "Notifications"),
            ("wishlist",      "Wishlist"),
            ("downloads",     "Downloads"),
            ("search",        "Search"),
            ("upcoming",      "Upcoming"),
            ("plex",          "Plex"),
            ("irc", "IRC"),
            ("predb", "PreDB"),
            ("browse", "Browse"),
            ("mounts", "Mounts"),
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

        if (tabControl != null)
        {
            dashboard.Width = dashboard.MinWidth;
            dashboard.Height = dashboard.MinHeight;
            foreach (var header in new[] { "Downloads", "Search", "Notifications", "Browse", "Overview", "Player", "Spread" })
            {
                SelectTabByHeader(tabControl, header);
                dashboard.UpdateLayout();
                DoEvents();
                RenderToFile(dashboard, $"dashboard-{header.ToLowerInvariant()}-narrow.png");
            }
            if (dashboard.DataContext is DashboardViewModel demoVm)
            {
                CaptureTorrentQueue(dashboard, tabControl, demoVm);
                demoVm.DownloadItems.Clear();
                demoVm.WishlistItems.Clear();
                demoVm.SearchResults.Clear();
                demoVm.NotificationItems.Clear();
                dashboard.DataContext = null;
                dashboard.DataContext = demoVm;
                foreach (var header in new[] { "Downloads", "Wishlist", "Search", "Notifications" })
                {
                    SelectTabByHeader(tabControl, header);
                    DoEvents();
                    RenderToFile(dashboard, $"dashboard-{header.ToLowerInvariant()}-empty.png");
                }
            }
        }
        dashboard.Close();
        serverManager.Dispose();
    }

    private static void CaptureTorrentQueue(DashboardWindow dashboard, TabControl tabs, DashboardViewModel vm)
    {
        // In-memory presentation only: never start a torrent engine or persist sample items.
        vm.DownloadItems.Clear();
        var ftp = BuildDemoDownloads().First();
        vm.DownloadItems.Add(ftp);
        var samples = new[]
        {
            new DownloadItemVm { Id = "torrent-metadata", ReleaseName = "Sample.Open.Film.2026.1080p.WEB-GROUP", Status = "Downloading", IsIndeterminate = true, ProgressText = "Fetching metadata — waiting for peers" },
            new DownloadItemVm { Id = "torrent-active", ReleaseName = "Sample.Documentary.2026.2160p.WEB-GROUP", Status = "Downloading", ProgressPercent = 38.4, ProgressText = "38% — Downloading · 8 peers", SpeedDisplay = "12.4 MiB/s" },
            new DownloadItemVm { Id = "torrent-paused", ReleaseName = "Sample.Series.S01E02.1080p.WEB-GROUP", Status = "Paused", ProgressPercent = 62, ProgressText = "Paused at 62%" },
            new DownloadItemVm { Id = "torrent-blocked", ReleaseName = "Sample.Archive.2026-GROUP", Status = "Failed", RequiresContentApproval = true, ProgressText = "Blocked: 1 executable file — choose Review to skip it or Cancel" },
            new DownloadItemVm { Id = "torrent-completed", ReleaseName = "Sample.Open.Album.2026.FLAC-GROUP", Status = "Completed", ProgressPercent = 100, ProgressText = "Done" }
        };
        foreach (var row in samples)
        {
            row.SourceKind = DownloadSourceKind.Torrent;
            row.ServerId = ServerManager.TorrentDownloadQueueId;
            row.ServerName = "BitTorrent";
            row.Category = "Torrents";
            row.LocalPath = @"C:\Downloads\GlDrive\Synthetic-torrent-demo";
            vm.DownloadItems.Add(row);
        }
        dashboard.DataContext = null;
        dashboard.DataContext = vm;
        SelectTabByHeader(tabs, "Downloads");
        var list = (ListBox)dashboard.FindName("DownloadsGrid");
        dashboard.Width = Math.Min(1440, SystemParameters.WorkArea.Width);
        dashboard.Height = Math.Min(900, SystemParameters.WorkArea.Height);
        list.SelectedItem = samples[3];
        dashboard.UpdateLayout();
        DoEvents();
        RenderToFile(dashboard, "dashboard-torrents.png");
        list.SelectedItem = samples[2];
        DoEvents();
        RenderToFile(dashboard, "dashboard-torrents-paused.png");
        dashboard.Width = dashboard.MinWidth;
        dashboard.Height = dashboard.MinHeight;
        list.SelectedItem = samples[3];
        dashboard.UpdateLayout();
        list.ScrollIntoView(samples[^1]);
        DoEvents();
        RenderToFile(dashboard, "dashboard-torrents-narrow.png");
        list.SelectedItem = samples[1];
        FindVisualChild<ScrollViewer>(list)?.ScrollToTop();
        DoEvents();
        RenderToFile(dashboard, "dashboard-torrents-narrow-top.png");
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
        var notificationStore = new NotificationStore(persistToDisk: false);
        var serverManager = new ServerManager(config, certManager, notificationStore);
        var settings = new SettingsWindow(config, serverManager, readLocalState: false);
        settings.Show();
        DoEvents();

        var tabControl = FindTabControl(settings);

        var settingsTabs = new[]
        {
            ("servers",     "Servers"),
            ("performance", "Performance"),
            ("streams", "STREAMS"),
            ("spread", "Spread"),
            ("agent", "AI-AGENT"),
            ("downloads",   "Downloads"),
            ("diagnostics", "Diagnostics"),
            ("mcp",         "MCP"),
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

            // The multiline Desktop configuration must remain usable at the minimum window size.
            if (settings.DataContext is SettingsViewModel vm && SelectTabByHeader(tabControl, "MCP"))
            {
                vm.Mcp.SelectedClient = "Claude Desktop";
                settings.Width = settings.MinWidth;
                settings.Height = settings.MinHeight;
                settings.UpdateLayout();
                DoEvents();
                RenderToFile(settings, "settings-mcp-desktop-narrow.png");
                var panel = FindVisualChild<McpSettingsPanel>(settings);
                if (panel != null) FindVisualChild<ScrollViewer>(panel)?.ScrollToEnd();
                DoEvents();
                RenderToFile(settings, "settings-mcp-desktop-narrow-scrolled.png");
            }
        }

        settings.Close();
        serverManager.Dispose();
    }

    private static void CaptureUtilityWindows(AppConfig demoConfig)
    {
        foreach (var (window, name) in new (Window, string)[]
        {
            (new ServerEditDialog(demoConfig.Servers[0]), "dialog-server"),
            (new RuleTestDialog(demoConfig.Servers[0].SpreadSite), "dialog-rule-test"),
            (new TagRulesDialog(new SectionMapping { IrcSection = "MOVIES", RemoteSection = "MOVIES" }), "dialog-tag-rules"),
            (new FirstRunAgentConsentDialog("Demo model"), "dialog-agent-consent"),
            (new RevertRunsDialog(), "dialog-revert-runs"),
            (new MetadataSearchDialog(MediaType.TvShow, ""), "dialog-metadata-search"),
            (new GlftpdInstallerWindow(), "dialog-installer")
        })
        {
            window.Show();
            window.UpdateLayout();
            DoEvents();
            RenderToFile(window, name + ".png");
            window.Close();
        }

        // These views use only in-memory sample data. In particular, extractor demo mode
        // skips saved watch folders and never writes extractor settings when closed.
        var extractor = new ExtractorWindow(demoMode: true);
        extractor.Archives.Add(new ArchiveItem
        {
            FileName = "Sample.Documentary.2026.1080p.WEB.part01.rar",
            FilePath = @"C:\Downloads\Sample.Documentary.2026\Sample.Documentary.2026.1080p.WEB.part01.rar",
            ArchiveType = "RAR", SizeText = "2.4 GB", EntryCount = 8, Status = "Done", Progress = 100
        });
        extractor.Archives.Add(new ArchiveItem
        {
            FileName = "Sample.Series.S01E02.2160p.WEB.part01.rar",
            FilePath = @"C:\Downloads\Sample.Series.S01E02\Sample.Series.S01E02.2160p.WEB.part01.rar",
            ArchiveType = "RAR", SizeText = "4.8 GB", EntryCount = 12, Status = "Extracting", Progress = 44.6
        });
        extractor.Archives.Add(new ArchiveItem
        {
            FileName = "Sample.Album.2026.FLAC.zip",
            FilePath = @"C:\Downloads\Sample.Album.2026\Sample.Album.2026.FLAC.zip",
            ArchiveType = "ZIP", SizeText = "420 MB", Status = "Queued"
        });
        extractor.Archives.Add(new ArchiveItem
        {
            FileName = "Sample.Archive.With.A.Long.Descriptive.Name.part01.rar",
            FilePath = @"C:\Downloads\Sample.Archive\Sample.Archive.With.A.Long.Descriptive.Name.part01.rar",
            ArchiveType = "RAR", SizeText = "1.8 GB", EntryCount = 5, Status = "Error", Progress = 18,
            ErrorMessage = "Missing volume: part03.rar. Add the remaining archive parts, then try again."
        });
        ((ComboBox)extractor.FindName("OutputMode")).SelectedIndex = 2;
        ((TextBox)extractor.FindName("CustomOutputPath")).Text = demoConfig.Downloads.LocalPath;
        ((TextBox)extractor.FindName("WatchFoldersText")).Text = @"C:\Downloads\Incoming";
        ((TextBlock)extractor.FindName("StatusText")).Text = "4 archives — 1 complete — 1 needs attention";
        ((ProgressBar)extractor.FindName("TotalProgress")).Value = 41;
        ((TextBlock)extractor.FindName("TotalProgressText")).Text = "41%";
        extractor.Show();
        ((DataGrid)extractor.FindName("ArchiveGrid")).SelectedIndex = 3;
        extractor.UpdateLayout();
        DoEvents();
        RenderToFile(extractor, "extractor-populated.png");
        extractor.Width = extractor.MinWidth;
        extractor.Height = extractor.MinHeight;
        extractor.UpdateLayout();
        ((DataGrid)extractor.FindName("ArchiveGrid")).ScrollIntoView(extractor.Archives[3]);
        DoEvents();
        RenderToFile(extractor, "extractor-minimum.png");
        extractor.Close();

        var cleanup = new CleanupWindow();
        cleanup.Show();
        cleanup.UpdateLayout();
        DoEvents();
        RenderToFile(cleanup, "cleanup-empty.png");
        cleanup.Items.Add(new CleanupItem
        {
            FolderName = "Sample.Documentary.2026.1080p.WEB",
            FolderPath = @"C:\Media\Documentaries\Sample.Documentary.2026.1080p.WEB",
            ArchiveCount = 24, ArchiveSize = 2576980377L, SizeText = "2.4 GB", MediaCount = 1
        });
        cleanup.Items.Add(new CleanupItem
        {
            FolderName = "Sample.Series.S01E02.2160p.WEB",
            FolderPath = @"C:\Media\Series\Sample.Series.S01E02.2160p.WEB",
            ArchiveCount = 12, ArchiveSize = 1288490188L, SizeText = "1.2 GB", MediaCount = 1
        });
        cleanup.Items.Add(new CleanupItem
        {
            FolderName = "Sample.Album.2026.FLAC", FolderPath = @"C:\Media\Music\Sample.Album.2026.FLAC",
            ArchiveCount = 1, ArchiveSize = 440401920L, SizeText = "420 MB", MediaCount = 10, Status = "Cleaned"
        });
        ((TextBox)cleanup.FindName("ScanPath")).Text = @"C:\Media";
        ((TextBlock)cleanup.FindName("ScanHint")).Visibility = Visibility.Collapsed;
        ((TextBlock)cleanup.FindName("SummaryText")).Text = "2 folders with leftover archives — 3.6 GB reclaimable";
        ((Button)cleanup.FindName("CleanAllButton")).IsEnabled = true;
        ((DataGrid)cleanup.FindName("ResultsGrid")).SelectedIndex = 0;
        cleanup.Width = cleanup.MinWidth;
        cleanup.Height = cleanup.MinHeight;
        cleanup.UpdateLayout();
        DoEvents();
        RenderToFile(cleanup, "cleanup-populated-minimum.png");
        cleanup.Close();

        var now = DateTime.UtcNow;
        var auditRows = new System.Collections.ObjectModel.ObservableCollection<GlDrive.AiAgent.AuditRow>
        {
            new() { Ts = now.AddHours(-2).ToString("O"), RunId = "demo-run-001", Category = "pool-sizing",
                Target = "/servers/demo/pool/maxConnections", Confidence = .96, Applied = true },
            new() { Ts = now.AddHours(-3).ToString("O"), RunId = "demo-run-001", Category = "site-priority",
                Target = "/servers/demo/priority", Confidence = .88, Applied = true, Undone = true }
        };
        var suggestions = new System.Collections.ObjectModel.ObservableCollection<GlDrive.AiAgent.AuditRow>
        {
            new() { Ts = now.AddHours(-2).ToString("O"), RunId = "demo-run-002", Category = "section-mapping",
                Target = "/servers/demo/sections/documentaries", Confidence = .62,
                RejectionReason = "Below the configured confidence threshold" },
            new() { Ts = now.AddHours(-2).ToString("O"), RunId = "demo-run-002", Category = "pool-sizing",
                Target = "/servers/demo/pool/maxConnections", Confidence = .91, Applied = true, DryRun = true,
                RejectionReason = "Preview only; settings were not changed" }
        };
        var frozen = new System.Collections.ObjectModel.ObservableCollection<GlDrive.AiAgent.FreezeEntry>
        {
            new("/servers/demo/excludedCategories", now.AddDays(-1).ToString("O"), "Keep my category preferences")
        };
        var demoCommand = new RelayCommand(() => { });
        var agent = new AgentView(demoMode: true);
        var agentTabs = ((DockPanel)agent.Content).Children.OfType<TabControl>().Single();
        // Anonymous demo properties are read-only; the production memo editor normally
        // writes to AgentViewModel, so use a one-way binding for this rendering fixture.
        var memoGrid = (Grid)((TabItem)agentTabs.Items[4]).Content;
        memoGrid.Children.OfType<TextBox>().Single().SetBinding(TextBox.TextProperty,
            new System.Windows.Data.Binding("Memo") { Mode = System.Windows.Data.BindingMode.OneWay });
        agent.DataContext = new
            {
                BriefMarkdown = "Latest agent report\n\nConnection reliability improved over the last seven days. " +
                    "Two changes were applied and two suggestions are ready for review.\n\n" +
                    "Applied changes\n• Adjusted the connection pool to match recent usage.\n" +
                    "• Updated server priority for more reliable transfers.\n\n" +
                    "Needs your attention\nReview the proposed section mapping in Suggestions. " +
                    "Your frozen category preferences were preserved.",
                AuditRows = auditRows, Suggestions = suggestions, Frozen = frozen,
                Memo = "Prefer reliable transfers over peak speed.\nKeep my excluded categories unchanged.",
                RunNowCommand = demoCommand, RefreshCommand = demoCommand, UndoAuditRowCommand = demoCommand,
                ApplyAnywayCommand = demoCommand, DismissSuggestionCommand = demoCommand,
                UnfreezeCommand = demoCommand, SaveMemoCommand = demoCommand
        };
        var agentWindow = new Window
        {
            Title = "AI Agent", Width = 1100, Height = 680, WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = agent
        };
        agentWindow.Show();
        DoEvents();
        if (agentTabs != null)
        {
            var names = new[] { "brief", "audit", "suggestions", "frozen", "memo" };
            for (var i = 0; i < names.Length; i++)
            {
                agentTabs.SelectedIndex = i;
                agentWindow.UpdateLayout();
                DoEvents();
                RenderToFile(agentWindow, $"agent-{names[i]}.png");
            }
            auditRows.Clear();
            suggestions.Clear();
            frozen.Clear();
            for (var i = 1; i <= 3; i++)
            {
                agentTabs.SelectedIndex = i;
                agentWindow.UpdateLayout();
                DoEvents();
                RenderToFile(agentWindow, $"agent-{names[i]}-empty.png");
            }
        }
        agentWindow.Close();

        const long volume = 1L << 30;
        var map = new GlDrive.Downloads.Segmented.TransferMap(
            [new("sample.part01.rar", volume, [new(0, 768L << 20)]),
             new("sample.part02.rar", volume, [new(0, 384L << 20)])],
            [new(0, 512L << 20, 768L << 20, volume, "Ethernet", "demo-wired"),
             new(1, 0, 384L << 20, volume, "race login", null)]);
        var matrix = new StreamMatrix.StreamMatrixViewModel("Sample.Documentary.2026.1080p.WEB.Multi.Part.Release");
        for (var tick = 0; tick < 120; tick++)
        {
            double wiredRate = (38 + 5 * Math.Sin(tick / 8.0)) * (1 << 20);
            double raceRate = (18 + 3 * Math.Cos(tick / 5.0)) * (1 << 20);
            matrix.Update(new DownloadProgress(1152L << 20, 2 * volume, wiredRate + raceRate, "sample.part02.rar",
                [new("Ethernet", "demo-wired", 768L << 20, wiredRate, GlDrive.Downloads.Segmented.LaneState.Active),
                 new("race login", null, 384L << 20, raceRate, GlDrive.Downloads.Segmented.LaneState.Active),
                 new("Mobile tether", "demo-mobile", 64L << 20, 0, GlDrive.Downloads.Segmented.LaneState.Yielded)], map),
                force: true);
        }
        var streamPanel = new StackPanel { Margin = new Thickness(16) };
        streamPanel.Children.Add(new StreamMatrix.StreamMatrixView { DataContext = matrix, Margin = new Thickness(0, 0, 0, 16) });
        streamPanel.Children.Add(new DownloadRowView
        {
            DataContext = new DownloadItemVm
            {
                ReleaseName = matrix.ReleaseName, Status = "Downloading", ServerName = "Demo server", Category = "Documentaries",
                ProgressText = "1.13 GiB / 2.00 GiB", ProgressPercent = 56.25, SpeedDisplay = matrix.HeaderAgg,
                Matrix = matrix, StreamsText = "2 active streams · 1 yielded"
            }
        });
        var streamWindow = new Window
        {
            Title = "Download streams", Width = 1000, Height = 520, Content = streamPanel,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        streamWindow.Show();
        streamWindow.UpdateLayout();
        DoEvents();
        RenderToFile(streamWindow, "stream-matrix.png");
        streamWindow.Width = 760;
        streamWindow.UpdateLayout();
        DoEvents();
        RenderToFile(streamWindow, "stream-matrix-narrow.png");
        streamWindow.Close();
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

        (_artifacts ?? throw new InvalidOperationException("No render session is active.")).Write(filename, encoder.Save);
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
