using System.IO;
using System.Net;
using System.Windows;
using FluentFTP;
using FluentFTP.GnuTLS;
using FluentFTP.GnuTLS.Enums;
using FluentFTP.Proxy.AsyncProxy;
using System.Collections.ObjectModel;
using GlDrive.Config;
using GlDrive.Ftp;
using Serilog;
using GlDrive.Spread;
using static GlDrive.Config.SearchMethod;

namespace GlDrive.UI;

public partial class ServerEditDialog : Window
{
    private readonly ServerConfig _serverConfig;
    private readonly ObservableCollection<SkiplistRule> _siteSkiplist = new();
    private readonly ObservableCollection<SectionMapping> _sectionMappings = new();
    private readonly Services.ServerManager? _serverManager;
    private string _password = "";

    public ServerConfig Result => _serverConfig;

    public ServerEditDialog(ServerConfig? existing = null, Services.ServerManager? serverManager = null)
    {
        _serverManager = serverManager;
        InitializeComponent();

        _serverConfig = existing ?? new ServerConfig();

        SiteSkiplistGrid.ItemsSource = _siteSkiplist;
        SectionMappingsGrid.ItemsSource = _sectionMappings;

        // Populate drive letter combo
        var used = DriveInfo.GetDrives().Select(d => d.Name[0]).ToHashSet();
        // If editing, include the current drive letter even if "in use" (it's our own mount)
        foreach (var c in Enumerable.Range('D', 23).Select(c => ((char)c).ToString())
                     .Where(c => !used.Contains(c[0]) || c == _serverConfig.Mount.DriveLetter))
            DriveLetterBox.Items.Add(c);

        // Populate search method combo
        SearchMethodBox.Items.Add("Auto");
        SearchMethodBox.Items.Add("SITE SEARCH");
        SearchMethodBox.Items.Add("Cached Index");
        SearchMethodBox.Items.Add("Live Crawl");
        SearchMethodBox.SelectedIndex = 0;

        if (existing != null)
        {
            NameBox.Text = existing.Name;
            HostBox.Text = existing.Connection.Host;
            PortBox.Text = existing.Connection.Port.ToString();
            UsernameBox.Text = existing.Connection.Username;
            RootPathBox.Text = existing.Connection.RootPath;

            // Proxy
            if (existing.Connection.Proxy != null)
            {
                ProxyEnabledBox.IsChecked = existing.Connection.Proxy.Enabled;
                ProxyHostBox.Text = existing.Connection.Proxy.Host;
                ProxyPortBox.Text = existing.Connection.Proxy.Port.ToString();
                ProxyUsernameBox.Text = existing.Connection.Proxy.Username;
                if (!string.IsNullOrEmpty(existing.Connection.Proxy.Username))
                {
                    var proxyPw = CredentialStore.GetProxyPassword(
                        existing.Connection.Proxy.Host, existing.Connection.Proxy.Port, existing.Connection.Proxy.Username);
                    if (!string.IsNullOrEmpty(proxyPw))
                        ProxyPasswordBox.Password = proxyPw;
                }
            }

            DriveLetterBox.SelectedItem = existing.Mount.DriveLetter;
            VolumeLabelBox.Text = existing.Mount.VolumeLabel;
            MountDriveBox.IsChecked = existing.Mount.MountDrive;
            AutoMountBox.IsChecked = existing.Mount.AutoMountOnStart;
            PreferTls12Box.IsChecked = existing.Tls.PreferTls12;

            // Cache
            CacheTtlBox.Text = existing.Cache.DirectoryListingTtlSeconds.ToString();
            MaxCachedDirsBox.Text = existing.Cache.MaxCachedDirectories.ToString();
            FileInfoTimeoutBox.Text = existing.Cache.FileInfoTimeoutMs.ToString();

            // Pool
            PoolSizeBox.Text = existing.Pool.PoolSize.ToString();
            KeepaliveBox.Text = existing.Pool.KeepaliveIntervalSeconds.ToString();

            // Notifications
            NotificationsEnabledBox.IsChecked = existing.Notifications.Enabled;
            PollIntervalBox.Text = existing.Notifications.PollIntervalSeconds.ToString();
            WatchPathBox.Text = existing.Notifications.WatchPath;
            ExcludedCategoriesBox.Text = string.Join(", ", existing.Notifications.ExcludedCategories);

            // Speed limit
            SpeedLimitBox.Text = existing.SpeedLimitKbps.ToString();

            // Search
            SearchPathsBox.Text = string.Join("\n", existing.Search.SearchPaths);
            SearchMaxDepthBox.Text = existing.Search.MaxDepth.ToString();
            SearchMethodBox.SelectedIndex = (int)existing.Search.Method;
            IndexCacheMinutesBox.Text = existing.Search.IndexCacheMinutes.ToString();

            // IRC
            IrcEnabledBox.IsChecked = existing.Irc.Enabled;
            IrcHostBox.Text = existing.Irc.Host;
            IrcPortBox.Text = existing.Irc.Port.ToString();
            IrcUseTlsBox.IsChecked = existing.Irc.UseTls;
            IrcNickBox.Text = existing.Irc.Nick;
            IrcAltNickBox.Text = existing.Irc.AltNick;
            IrcRealNameBox.Text = existing.Irc.RealName;
            IrcAutoConnectBox.IsChecked = existing.Irc.AutoConnect;
            IrcInviteNickBox.Text = existing.Irc.InviteNick;
            IrcFishEnabledBox.IsChecked = existing.Irc.FishEnabled;
            IrcFishModeBox.SelectedIndex = existing.Irc.FishMode == FishMode.CBC ? 1 : 0;
            IrcChannelsBox.Text = string.Join("\n", existing.Irc.Channels.Select(c =>
            {
                var prefix = c.AutoJoin ? "" : "-";
                return string.IsNullOrEmpty(c.Key) ? $"{prefix}{c.Name}" : $"{prefix}{c.Name} {c.Key}";
            }));

            if (!string.IsNullOrEmpty(existing.Irc.Host) && !string.IsNullOrEmpty(existing.Irc.Nick))
            {
                var ircPw = CredentialStore.GetIrcPassword(existing.Irc.Host, existing.Irc.Port, existing.Irc.Nick);
                if (!string.IsNullOrEmpty(ircPw))
                    IrcPasswordBox.Password = ircPw;
            }

            // Spread (Sections dict is derived from SectionMappings — grid loads below)
            SpreadPriorityBox.SelectedIndex = existing.SpreadSite.Priority switch
            {
                SitePriority.VeryLow => 0,
                SitePriority.Low => 1,
                SitePriority.Normal => 2,
                SitePriority.High => 3,
                SitePriority.VeryHigh => 4,
                _ => 2
            };
            SpreadMaxUpBox.Text = existing.SpreadSite.MaxUploadSlots.ToString();
            SpreadMaxDownBox.Text = existing.SpreadSite.MaxDownloadSlots.ToString();
            SpreadDownloadOnlyBox.IsChecked = existing.SpreadSite.DownloadOnly;
            SpreadAffilsBox.Text = string.Join(", ", existing.SpreadSite.Affils);

            // IRC announce rules
            IrcAnnounceRulesBox.Text = string.Join("\n", existing.Irc.AnnounceRules
                .Where(r => r.Enabled)
                .Select(r => string.IsNullOrEmpty(r.Channel) ? r.Pattern : $"{r.Channel} {r.Pattern}"));

            foreach (var rule in existing.SpreadSite.Skiplist)
                _siteSkiplist.Add(rule);

            foreach (var mapping in existing.SpreadSite.SectionMappings)
                _sectionMappings.Add(mapping);

            // Metadata filter
            var mf = existing.SpreadSite.MetadataFilter;
            MetaFilterEnabledBox.IsChecked = mf.Enabled;
            MetaMinRatingBox.Text = mf.MinImdbRating.ToString("0.#",
                System.Globalization.CultureInfo.InvariantCulture);
            MetaMinVotesBox.Text = mf.MinVotes.ToString();
            MetaTimeoutBox.Text = mf.LookupTimeoutSeconds.ToString();
            MetaAllowGenresBox.Text = mf.AllowGenres;
            MetaDenyGenresBox.Text = mf.DenyGenres;
            MetaSkipEndedBox.IsChecked = mf.SkipEndedShows;

            // Request Filler
            RequestFillerEnabledBox.IsChecked = existing.Irc.RequestFiller.Enabled;
            RequestFillerPatternBox.Text = existing.Irc.RequestFiller.Pattern;
            RequestFillerChannelBox.Text = existing.Irc.RequestFiller.Channel;
            RequestFillerMaxPerHourBox.Text = existing.Irc.RequestFiller.MaxPerHour.ToString();
            RequestFillerCooldownBox.Text = existing.Irc.RequestFiller.CooldownSeconds.ToString();

            // Load stored password hint
            var storedPw = CredentialStore.GetPassword(existing.Connection.Host, existing.Connection.Port, existing.Connection.Username);
            if (!string.IsNullOrEmpty(storedPw))
                PasswordBox.Password = storedPw;
        }
        else
        {
            if (DriveLetterBox.Items.Contains("G")) DriveLetterBox.SelectedItem = "G";
            else if (DriveLetterBox.Items.Count > 0) DriveLetterBox.SelectedIndex = 0;
        }
    }

    private void ClearCert_Click(object sender, RoutedEventArgs e)
    {
        var host = HostBox.Text;
        var port = int.TryParse(PortBox.Text, out var p) ? Math.Clamp(p, 1, 65535) : 21;
        var key = $"{host}:{port}";
        var certMgr = new Tls.CertificateManager();
        certMgr.RemoveTrustedCertificate(key);
        TestResultText.Text = $"Certificate cleared for {key}. Will be re-accepted on next connect.";
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        TestResultText.Text = "Testing connection...";
        try
        {
            var host = HostBox.Text;
            var port = int.TryParse(PortBox.Text, out var p) ? Math.Clamp(p, 1, 65535) : 21;
            var username = UsernameBox.Text;
            var password = PasswordBox.Password;
            if (string.IsNullOrEmpty(password))
                password = CredentialStore.GetPassword(host, port, username) ?? "";

            AsyncFtpClient client;

            if (ProxyEnabledBox.IsChecked == true && !string.IsNullOrWhiteSpace(ProxyHostBox.Text))
            {
                var proxyPort = int.TryParse(ProxyPortBox.Text, out var pp) ? Math.Clamp(pp, 1, 65535) : 1080;
                var proxyUser = ProxyUsernameBox.Text ?? "";
                var proxyPw = !string.IsNullOrEmpty(proxyUser) ? ProxyPasswordBox.Password : "";

                var profile = new FtpProxyProfile
                {
                    ProxyHost = ProxyHostBox.Text,
                    ProxyPort = proxyPort,
                    ProxyCredentials = !string.IsNullOrEmpty(proxyUser)
                        ? new NetworkCredential(proxyUser, proxyPw)
                        : null,
                    FtpHost = host,
                    FtpPort = port,
                    FtpCredentials = new NetworkCredential(username, password),
                };
                client = new AsyncFtpClientSocks5Proxy(profile);
            }
            else
            {
                client = new AsyncFtpClient(host, username, password, port);
            }

            client.Config.EncryptionMode = FtpEncryptionMode.Explicit;
            client.Config.DataConnectionEncryption = true;
            client.Config.CustomStream = typeof(GnuTlsStream);
            var gnuConfig = new GnuConfig { SecuritySuite = GnuSuite.Secure128 };
            if (PreferTls12Box.IsChecked == true)
                gnuConfig.AdvancedOptions = [GnuAdvanced.NoTickets];
            client.Config.CustomStreamConfig = gnuConfig;
            string? certFingerprint = null;
            client.ValidateCertificate += (control, e) =>
            {
                e.Accept = true;
                if (e.Certificate != null)
                {
                    using var cert2 = new System.Security.Cryptography.X509Certificates.X509Certificate2(e.Certificate);
                    certFingerprint = Convert.ToHexString(
                        cert2.GetCertHash(System.Security.Cryptography.HashAlgorithmName.SHA256));
                }
            };

            await client.Connect();
            var listing = await client.GetListing("/");
            await client.Disconnect();

            // Auto-trust the certificate so subsequent connects succeed
            if (certFingerprint != null)
            {
                var certMgr = new Tls.CertificateManager();
                certMgr.TrustCertificate($"{host}:{port}", certFingerprint);
            }
            client.Dispose();

            var via = ProxyEnabledBox.IsChecked == true ? " (via SOCKS5 proxy)" : "";
            TestResultText.Text = $"Success! Connected{via} and listed {listing.Length} items in root.";
        }
        catch (Exception ex)
        {
            TestResultText.Text = $"Failed: {ex.Message}";
            Log.Warning(ex, "Test connection failed");
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(HostBox.Text) || string.IsNullOrWhiteSpace(UsernameBox.Text))
        {
            MessageBox.Show("Host and Username are required.", "Validation",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _serverConfig.Name = string.IsNullOrWhiteSpace(NameBox.Text) ? HostBox.Text : NameBox.Text;
        _serverConfig.Connection.Host = HostBox.Text;
        _serverConfig.Connection.Port = int.TryParse(PortBox.Text, out var p) ? Math.Clamp(p, 1, 65535) : 21;
        _serverConfig.Connection.Username = UsernameBox.Text;
        _serverConfig.Connection.RootPath = RootPathBox.Text;

        // Proxy
        if (ProxyEnabledBox.IsChecked == true && !string.IsNullOrWhiteSpace(ProxyHostBox.Text))
        {
            _serverConfig.Connection.Proxy ??= new ProxyConfig();
            _serverConfig.Connection.Proxy.Enabled = true;
            _serverConfig.Connection.Proxy.Host = ProxyHostBox.Text;
            _serverConfig.Connection.Proxy.Port = int.TryParse(ProxyPortBox.Text, out var pp) ? Math.Clamp(pp, 1, 65535) : 1080;
            _serverConfig.Connection.Proxy.Username = ProxyUsernameBox.Text ?? "";

            if (!string.IsNullOrEmpty(ProxyUsernameBox.Text) && !string.IsNullOrEmpty(ProxyPasswordBox.Password))
            {
                CredentialStore.SaveProxyPassword(
                    _serverConfig.Connection.Proxy.Host,
                    _serverConfig.Connection.Proxy.Port,
                    _serverConfig.Connection.Proxy.Username,
                    ProxyPasswordBox.Password);
            }
        }
        else
        {
            if (_serverConfig.Connection.Proxy != null)
                _serverConfig.Connection.Proxy.Enabled = false;
        }

        _serverConfig.Mount.DriveLetter = DriveLetterBox.SelectedItem?.ToString() ?? "G";
        _serverConfig.Mount.VolumeLabel = VolumeLabelBox.Text;
        _serverConfig.Mount.MountDrive = MountDriveBox.IsChecked == true;
        _serverConfig.Mount.AutoMountOnStart = AutoMountBox.IsChecked == true;
        _serverConfig.Tls.PreferTls12 = PreferTls12Box.IsChecked == true;
        _serverConfig.SpeedLimitKbps = int.TryParse(SpeedLimitBox.Text, out var sl) ? Math.Clamp(sl, 0, 100000) : 0;

        // Cache
        _serverConfig.Cache.DirectoryListingTtlSeconds = int.TryParse(CacheTtlBox.Text, out var ttl) ? Math.Clamp(ttl, 5, 300) : 30;
        _serverConfig.Cache.MaxCachedDirectories = int.TryParse(MaxCachedDirsBox.Text, out var mcd) ? Math.Clamp(mcd, 50, 5000) : 500;
        _serverConfig.Cache.FileInfoTimeoutMs = int.TryParse(FileInfoTimeoutBox.Text, out var fit) ? Math.Clamp(fit, 100, 10000) : 1000;

        // Pool
        _serverConfig.Pool.PoolSize = int.TryParse(PoolSizeBox.Text, out var ps) ? Math.Clamp(ps, 1, 10) : 3;
        _serverConfig.Pool.KeepaliveIntervalSeconds = int.TryParse(KeepaliveBox.Text, out var ka) ? Math.Clamp(ka, 10, 120) : 30;

        // Notifications
        _serverConfig.Notifications.Enabled = NotificationsEnabledBox.IsChecked == true;
        _serverConfig.Notifications.PollIntervalSeconds = int.TryParse(PollIntervalBox.Text, out var pi) ? Math.Clamp(pi, 10, 600) : 60;
        _serverConfig.Notifications.WatchPath = string.IsNullOrWhiteSpace(WatchPathBox.Text) ? "/recent" : WatchPathBox.Text;
        _serverConfig.Notifications.ExcludedCategories = (ExcludedCategoriesBox.Text ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0).ToList();

        // Search
        _serverConfig.Search.SearchPaths = (SearchPathsBox.Text ?? "/")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0).ToList();
        if (_serverConfig.Search.SearchPaths.Count == 0)
            _serverConfig.Search.SearchPaths = ["/"];
        _serverConfig.Search.MaxDepth = int.TryParse(SearchMaxDepthBox.Text, out var md) ? Math.Clamp(md, 1, 10) : 2;
        _serverConfig.Search.Method = (SearchMethod)(SearchMethodBox.SelectedIndex >= 0 ? SearchMethodBox.SelectedIndex : 0);
        _serverConfig.Search.IndexCacheMinutes = int.TryParse(IndexCacheMinutesBox.Text, out var icm) ? Math.Clamp(icm, 1, 1440) : 60;

        // IRC
        _serverConfig.Irc.Enabled = IrcEnabledBox.IsChecked == true;
        _serverConfig.Irc.Host = IrcHostBox.Text ?? "";
        _serverConfig.Irc.Port = int.TryParse(IrcPortBox.Text, out var ircPort) ? Math.Clamp(ircPort, 1, 65535) : 6697;
        _serverConfig.Irc.UseTls = IrcUseTlsBox.IsChecked == true;
        _serverConfig.Irc.Nick = IrcNickBox.Text ?? "";
        _serverConfig.Irc.AltNick = IrcAltNickBox.Text ?? "";
        _serverConfig.Irc.RealName = string.IsNullOrWhiteSpace(IrcRealNameBox.Text) ? "GlDrive" : IrcRealNameBox.Text;
        _serverConfig.Irc.AutoConnect = IrcAutoConnectBox.IsChecked == true;
        _serverConfig.Irc.InviteNick = IrcInviteNickBox.Text ?? "";
        _serverConfig.Irc.FishEnabled = IrcFishEnabledBox.IsChecked == true;
        _serverConfig.Irc.FishMode = IrcFishModeBox.SelectedIndex == 1 ? FishMode.CBC : FishMode.ECB;

        // Parse channels (prefix with - to disable auto-join)
        _serverConfig.Irc.Channels = (IrcChannelsBox.Text ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.Length > 0)
            .Select(line =>
            {
                var autoJoin = true;
                if (line.StartsWith('-'))
                {
                    autoJoin = false;
                    line = line[1..];
                }
                var parts = line.Split(' ', 2);
                return new IrcChannelConfig
                {
                    Name = parts[0],
                    Key = parts.Length > 1 ? parts[1] : "",
                    AutoJoin = autoJoin
                };
            }).ToList();

        // Spread — SectionMappings is the source of truth; derive the
        // legacy Sections dict so SpreadJob / DashboardViewModel / MountService
        // keep working without changes.
        _serverConfig.SpreadSite.SectionMappings = _sectionMappings.ToList();
        _serverConfig.SpreadSite.Sections = _sectionMappings
            .Where(m => m.Enabled && !string.IsNullOrWhiteSpace(m.RemoteSection)
                        && !string.IsNullOrWhiteSpace(m.Path))
            .GroupBy(m => m.RemoteSection, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Path, StringComparer.OrdinalIgnoreCase);
        _serverConfig.SpreadSite.Priority = SpreadPriorityBox.SelectedIndex switch
        {
            0 => SitePriority.VeryLow,
            1 => SitePriority.Low,
            2 => SitePriority.Normal,
            3 => SitePriority.High,
            4 => SitePriority.VeryHigh,
            _ => SitePriority.Normal
        };
        _serverConfig.SpreadSite.MaxUploadSlots = int.TryParse(SpreadMaxUpBox.Text, out var mu) ? Math.Clamp(mu, 1, 10) : 3;
        _serverConfig.SpreadSite.MaxDownloadSlots = int.TryParse(SpreadMaxDownBox.Text, out var md2) ? Math.Clamp(md2, 1, 10) : 3;
        _serverConfig.SpreadSite.DownloadOnly = SpreadDownloadOnlyBox.IsChecked == true;
        _serverConfig.SpreadSite.Affils = (SpreadAffilsBox.Text ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0).ToList();
        _serverConfig.SpreadSite.Skiplist = _siteSkiplist.ToList();

        // Metadata filter
        var metaFilter = _serverConfig.SpreadSite.MetadataFilter;
        metaFilter.Enabled = MetaFilterEnabledBox.IsChecked == true;
        metaFilter.MinImdbRating = double.TryParse(MetaMinRatingBox.Text,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var mr)
            ? Math.Clamp(mr, 0, 10) : 0;
        metaFilter.MinVotes = int.TryParse(MetaMinVotesBox.Text, out var mv)
            ? Math.Max(0, mv) : 0;
        metaFilter.LookupTimeoutSeconds = int.TryParse(MetaTimeoutBox.Text, out var mt)
            ? Math.Clamp(mt, 1, 60) : 5;
        metaFilter.AllowGenres = MetaAllowGenresBox.Text?.Trim() ?? "";
        metaFilter.DenyGenres = MetaDenyGenresBox.Text?.Trim() ?? "";
        metaFilter.SkipEndedShows = MetaSkipEndedBox.IsChecked == true;

        // Request Filler
        _serverConfig.Irc.RequestFiller.Enabled = RequestFillerEnabledBox.IsChecked == true;
        var rfPattern = RequestFillerPatternBox.Text?.Trim();
        if (!string.IsNullOrEmpty(rfPattern))
            _serverConfig.Irc.RequestFiller.Pattern = rfPattern;
        _serverConfig.Irc.RequestFiller.Channel = RequestFillerChannelBox.Text?.Trim() ?? "";
        _serverConfig.Irc.RequestFiller.MaxPerHour =
            int.TryParse(RequestFillerMaxPerHourBox.Text, out var rfMax) ? Math.Clamp(rfMax, 1, 100) : 10;
        _serverConfig.Irc.RequestFiller.CooldownSeconds =
            int.TryParse(RequestFillerCooldownBox.Text, out var rfCd) ? Math.Clamp(rfCd, 0, 3600) : 60;

        // IRC announce rules
        _serverConfig.Irc.AnnounceRules = (IrcAnnounceRulesBox.Text ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.Length > 0)
            .Select(line =>
            {
                // Format: "#channel pattern" or just "pattern"
                string channel = "", pattern;
                if (line.StartsWith('#'))
                {
                    var spaceIdx = line.IndexOf(' ');
                    if (spaceIdx > 0)
                    {
                        channel = line[..spaceIdx];
                        pattern = line[(spaceIdx + 1)..].Trim();
                    }
                    else
                    {
                        pattern = line;
                    }
                }
                else
                {
                    pattern = line;
                }
                return new IrcAnnounceRule { Enabled = true, Channel = channel, Pattern = pattern, AutoRace = true };
            }).ToList();

        // Save IRC password
        if (!string.IsNullOrEmpty(IrcPasswordBox.Password) && !string.IsNullOrEmpty(_serverConfig.Irc.Host)
            && !string.IsNullOrEmpty(_serverConfig.Irc.Nick))
        {
            CredentialStore.SaveIrcPassword(_serverConfig.Irc.Host, _serverConfig.Irc.Port,
                _serverConfig.Irc.Nick, IrcPasswordBox.Password);
        }

        // Save password
        _password = PasswordBox.Password;
        if (!string.IsNullOrEmpty(_password))
        {
            CredentialStore.SavePassword(
                _serverConfig.Connection.Host,
                _serverConfig.Connection.Port,
                _serverConfig.Connection.Username,
                _password);
        }

        DialogResult = true;
        Close();
    }

    private static readonly HashSet<string> NonContentDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".", "..", ".banner", ".message", "etc", "bin", "dev", "usr", "tmp", "proc", "lib"
    };

    private async void DiscoverPaths_Click(object sender, RoutedEventArgs e)
    {
        DiscoverResultText.Text = "Discovering paths...";
        try
        {
            var host = HostBox.Text;
            var port = int.TryParse(PortBox.Text, out var p) ? Math.Clamp(p, 1, 65535) : 21;
            var username = UsernameBox.Text;
            var password = PasswordBox.Password;
            if (string.IsNullOrEmpty(password))
                password = CredentialStore.GetPassword(host, port, username) ?? "";

            AsyncFtpClient client;

            if (ProxyEnabledBox.IsChecked == true && !string.IsNullOrWhiteSpace(ProxyHostBox.Text))
            {
                var proxyPort = int.TryParse(ProxyPortBox.Text, out var pp) ? Math.Clamp(pp, 1, 65535) : 1080;
                var proxyUser = ProxyUsernameBox.Text ?? "";
                var proxyPw = !string.IsNullOrEmpty(proxyUser) ? ProxyPasswordBox.Password : "";

                var profile = new FtpProxyProfile
                {
                    ProxyHost = ProxyHostBox.Text,
                    ProxyPort = proxyPort,
                    ProxyCredentials = !string.IsNullOrEmpty(proxyUser)
                        ? new NetworkCredential(proxyUser, proxyPw)
                        : null,
                    FtpHost = host,
                    FtpPort = port,
                    FtpCredentials = new NetworkCredential(username, password),
                };
                client = new AsyncFtpClientSocks5Proxy(profile);
            }
            else
            {
                client = new AsyncFtpClient(host, username, password, port);
            }

            client.Config.EncryptionMode = FtpEncryptionMode.Explicit;
            client.Config.DataConnectionEncryption = true;
            client.Config.CustomStream = typeof(GnuTlsStream);
            var gnuConfig = new GnuConfig { SecuritySuite = GnuSuite.Secure128 };
            if (PreferTls12Box.IsChecked == true)
                gnuConfig.AdvancedOptions = [GnuAdvanced.NoTickets];
            client.Config.CustomStreamConfig = gnuConfig;
            client.ValidateCertificate += (_, ev) => ev.Accept = true;

            await client.Connect();

            var useCpsv = client.Capabilities.Contains(FtpCapability.CPSV);
            var controlHost = host;
            if (useCpsv)
                Log.Information("Discover: server supports CPSV — using BNC-compatible listings");

            async Task<FtpListItem[]> ListDir(string path)
            {
                if (useCpsv)
                    return await CpsvDataHelper.ListDirectory(client, path, controlHost);
                return await client.GetListing(path, FtpListOption.AllFiles);
            }

            // List root to find top-level dirs
            var rootPath = string.IsNullOrWhiteSpace(RootPathBox.Text) ? "/" : RootPathBox.Text.TrimEnd('/');
            if (string.IsNullOrEmpty(rootPath)) rootPath = "/";
            var rootItems = await ListDir(rootPath);

            var topDirs = rootItems
                .Where(i => (i.Type == FtpObjectType.Directory || i.Type == FtpObjectType.Link)
                    && !NonContentDirs.Contains(i.Name))
                .ToList();

            var contentPaths = new List<string>();

            foreach (var dir in topDirs)
            {
                try
                {
                    var subItems = await ListDir(dir.FullName);
                    var subDirCount = subItems.Count(i => (i.Type == FtpObjectType.Directory || i.Type == FtpObjectType.Link)
                        && !NonContentDirs.Contains(i.Name));
                    if (subDirCount >= 2)
                        contentPaths.Add(dir.FullName);
                }
                catch { }
            }

            await client.Disconnect();
            client.Dispose();

            if (contentPaths.Count > 0)
            {
                SearchPathsBox.Text = string.Join("\n", contentPaths);
                DiscoverResultText.Text = $"Found {contentPaths.Count} content path(s).";
            }
            else
            {
                SearchPathsBox.Text = rootPath;
                DiscoverResultText.Text = "No content sections found, using root path.";
            }
        }
        catch (Exception ex)
        {
            DiscoverResultText.Text = $"Discovery failed: {ex.Message}";
            Log.Warning(ex, "Path discovery failed");
        }
    }

    private async void AiSetup_Click(object sender, RoutedEventArgs e)
    {
        var apiKey = CredentialStore.GetApiKey("openrouter") ?? "";
        if (string.IsNullOrEmpty(apiKey))
        {
            MessageBox.Show("Set your OpenRouter API key in Settings → Downloads first.",
                "AI Setup", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var host = HostBox.Text;
        var port = int.TryParse(PortBox.Text, out var p) ? Math.Clamp(p, 1, 65535) : 21;
        var username = UsernameBox.Text;
        var password = PasswordBox.Password;
        if (string.IsNullOrEmpty(password))
            password = CredentialStore.GetPassword(host, port, username) ?? "";

        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(username))
        {
            MessageBox.Show("Enter host and username first.", "AI Setup",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        TestResultText.Text = "Connecting for AI analysis...";

        try
        {
            // Connect and get SITE RULES
            var client = new AsyncFtpClient(host, username, password, port);
            client.Config.EncryptionMode = FtpEncryptionMode.Explicit;
            client.Config.DataConnectionEncryption = true;
            client.Config.CustomStream = typeof(GnuTlsStream);
            var gnuConfig = new GnuConfig { SecuritySuite = GnuSuite.Secure128 };
            if (PreferTls12Box.IsChecked == true)
                gnuConfig.AdvancedOptions = [GnuAdvanced.NoTickets];
            client.Config.CustomStreamConfig = gnuConfig;
            client.ValidateCertificate += (_, ev) => ev.Accept = true;

            await client.Connect();
            var reply = await client.Execute("SITE RULES");
            await client.Disconnect();
            client.Dispose();

            var rulesText = reply.InfoMessages ?? reply.Message ?? "";
            if (string.IsNullOrWhiteSpace(rulesText))
            {
                TestResultText.Text = "SITE RULES returned empty response.";
                return;
            }

            // Gather current config context from the section mappings grid
            var currentSections = _sectionMappings
                .Where(m => !string.IsNullOrWhiteSpace(m.RemoteSection) && !string.IsNullOrWhiteSpace(m.Path))
                .GroupBy(m => m.RemoteSection, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Path, StringComparer.OrdinalIgnoreCase);
            var currentAffils = (SpreadAffilsBox.Text ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

            // Sample directory contents from each section so AI can see actual releases
            TestResultText.Text = "Sampling section contents...";
            var sectionSamples = new Dictionary<string, List<string>>();
            try
            {
                var sampleClient = new AsyncFtpClient(host, username, password, port);
                sampleClient.Config.EncryptionMode = FtpEncryptionMode.Explicit;
                sampleClient.Config.DataConnectionEncryption = true;
                sampleClient.Config.CustomStream = typeof(GnuTlsStream);
                var gnuConfig2 = new GnuConfig { SecuritySuite = GnuSuite.Secure128 };
                if (PreferTls12Box.IsChecked == true)
                    gnuConfig2.AdvancedOptions = [GnuAdvanced.NoTickets];
                sampleClient.Config.CustomStreamConfig = gnuConfig2;
                sampleClient.ValidateCertificate += (_, ev) => ev.Accept = true;
                await sampleClient.Connect();

                // Also scan the notification watch path for sections
                var watchPath = WatchPathBox.Text?.Trim() ?? "/recent";
                var pathsToSample = new Dictionary<string, string>(currentSections);

                // Add watch path subdirs if not already in sections
                try
                {
                    var watchItems = await sampleClient.GetListing(watchPath);
                    foreach (var item in watchItems.Where(i => i.Type is FtpObjectType.Directory or FtpObjectType.Link))
                    {
                        var sectionName = item.Name.ToUpper().Replace("-", "_").Replace(" ", "_");
                        pathsToSample.TryAdd(sectionName, item.FullName);
                    }
                }
                catch { }

                foreach (var (section, path) in pathsToSample.Take(10))
                {
                    try
                    {
                        var items = await sampleClient.GetListing(path);
                        var dirs = items
                            .Where(i => i.Type is FtpObjectType.Directory or FtpObjectType.Link)
                            .OrderByDescending(i => i.Modified)
                            .Take(5)
                            .Select(i => i.Name)
                            .ToList();
                        if (dirs.Count > 0)
                            sectionSamples[section] = dirs;
                    }
                    catch { }
                }

                await sampleClient.Disconnect();
                sampleClient.Dispose();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Failed to sample section contents for AI");
            }

            // Read model from saved config
            var savedConfig = ConfigManager.Load();
            var model = savedConfig.Downloads.OpenRouterModel;
            if (string.IsNullOrEmpty(model)) model = "openai/gpt-oss-120b:free";

            TestResultText.Text = $"Analyzing with AI ({model})...";

            using var aiClient = new OpenRouterClient(apiKey, model);
            var result = await aiClient.AnalyzeSiteRules(rulesText, currentSections, currentAffils, sectionSamples);

            if (result == null)
            {
                // AI failed — use deterministic fallback parser
                result = new AiSetupResult { Explanation = "AI unavailable — used rule-text parser" };
            }

            // Always supplement with deterministic parser to catch patterns AI may have missed
            var fallbackRules = OpenRouterClient.ParseRulesFallback(rulesText, currentSections);
            var existingAiPatterns = result.SkiplistRules.Select(r => r.Pattern).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var rule in fallbackRules)
            {
                if (!existingAiPatterns.Contains(rule.Pattern))
                {
                    result.SkiplistRules.Add(rule);
                    existingAiPatterns.Add(rule.Pattern);
                }
            }

            // Build summary and apply
            var changes = new List<string>();

            if (result.MaxUploadSlots.HasValue)
            {
                SpreadMaxUpBox.Text = result.MaxUploadSlots.Value.ToString();
                changes.Add($"Max uploads: {result.MaxUploadSlots.Value}");
            }
            if (result.MaxDownloadSlots.HasValue)
            {
                SpreadMaxDownBox.Text = result.MaxDownloadSlots.Value.ToString();
                changes.Add($"Max downloads: {result.MaxDownloadSlots.Value}");
            }

            if (result.Affils.Count > 0)
            {
                var existingAffils = (SpreadAffilsBox.Text ?? "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var a in result.Affils) existingAffils.Add(a);
                SpreadAffilsBox.Text = string.Join(", ", existingAffils.OrderBy(a => a));
                changes.Add($"Affils: {result.Affils.Count} groups");
            }

            if (result.SkiplistRules.Count > 0)
            {
                // Remove old AI-generated section-scoped rules (keep user's manual global rules)
                // This prevents accumulation of garbage rules from previous AI runs
                var toRemove = _siteSkiplist.Where(r => r.Section != null).ToList();
                var removedCount = toRemove.Count;
                foreach (var r in toRemove) _siteSkiplist.Remove(r);

                var existingPatterns = _siteSkiplist.Select(r => r.Pattern).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var added = 0;
                foreach (var rule in result.SkiplistRules)
                {
                    if (!existingPatterns.Contains(rule.Pattern))
                    {
                        _siteSkiplist.Add(rule.ToSkiplistRule());
                        existingPatterns.Add(rule.Pattern);
                        added++;
                    }
                }
                if (added > 0 || removedCount > 0)
                    changes.Add($"{added} skiplist rules (replaced {removedCount} old section rules)");
            }

            if (result.SuggestedSections.Count > 0)
            {
                var added = 0;
                foreach (var (name, path) in result.SuggestedSections)
                {
                    var existing = _sectionMappings.FirstOrDefault(m =>
                        m.RemoteSection.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (existing != null)
                    {
                        if (string.IsNullOrEmpty(existing.Path))
                        {
                            existing.Path = path;
                            added++;
                        }
                        continue;
                    }
                    _sectionMappings.Add(new SectionMapping
                    {
                        IrcSection = name,
                        RemoteSection = name,
                        Path = path,
                        TriggerRegex = ".*",
                        Enabled = true
                    });
                    added++;
                }
                if (added > 0)
                    changes.Add($"{added} section mapping(s) added");
            }

            TestResultText.Text = changes.Count > 0
                ? $"AI applied: {string.Join(", ", changes)}"
                : "AI found no additional configuration to suggest.";

            MessageBox.Show(
                $"AI Analysis Complete ({model}):\n\n" +
                (changes.Count > 0
                    ? string.Join("\n", changes.Select(c => $"  • {c}"))
                    : "No new changes suggested.") +
                $"\n\n{result.Explanation}",
                "AI Setup", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            TestResultText.Text = $"AI Setup failed: {ex.Message}";
            Log.Warning(ex, "AI Setup failed");
        }
    }

    private async void QuickSetup_Click(object sender, RoutedEventArgs e)
    {
        var results = new List<string>();

        // 1. Auto-detect sections
        try
        {
            AutoDetectSections_Click(sender, e);
            // Wait for it to finish (it's async void, track via the grid)
            await Task.Delay(500);
            while (!SectionMappingsGrid.IsEnabled) await Task.Delay(500);
            results.Add($"Sections: {_sectionMappings.Count} detected");
        }
        catch { results.Add("Sections: detection failed"); }

        // 2. Add default skiplist rules (if none exist)
        if (_siteSkiplist.Count == 0)
        {
            AddDefaultSkiplistRules_Click(sender, e);
            results.Add($"Skiplist: {_siteSkiplist.Count} default rules added");
        }
        else
        {
            results.Add($"Skiplist: {_siteSkiplist.Count} existing rules kept");
        }

        // 3. Detect site rules (slots, affils, denied patterns)
        try
        {
            DetectSiteRules_Click(sender, e);
            await Task.Delay(500);
            results.Add("Site rules: detection started");
        }
        catch { results.Add("Site rules: detection failed"); }

        // 4. Add common IRC announce patterns if none configured
        var existingRules = (IrcAnnounceRulesBox.Text ?? "").Trim();
        if (string.IsNullOrEmpty(existingRules))
        {
            AddCommonAnnouncePatterns_Click(sender, e);
            var ruleCount = (IrcAnnounceRulesBox.Text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
            results.Add($"IRC patterns: {ruleCount} common patterns added");
        }
        else
        {
            var ruleCount = existingRules.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
            results.Add($"IRC patterns: {ruleCount} existing rules kept");
        }

        MessageBox.Show(string.Join("\n", results), "Quick Setup Complete",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void AutoDetectSections_Click(object sender, RoutedEventArgs e)
    {
        SectionMappingsGrid.IsEnabled = false;
        TestResultText.Text = "";

        try
        {
            var host = HostBox.Text;
            var port = int.TryParse(PortBox.Text, out var p) ? Math.Clamp(p, 1, 65535) : 21;
            var username = UsernameBox.Text;
            var password = PasswordBox.Password;
            if (string.IsNullOrEmpty(password))
                password = CredentialStore.GetPassword(host, port, username) ?? "";

            AsyncFtpClient client;

            if (ProxyEnabledBox.IsChecked == true && !string.IsNullOrWhiteSpace(ProxyHostBox.Text))
            {
                var proxyPort = int.TryParse(ProxyPortBox.Text, out var pp) ? Math.Clamp(pp, 1, 65535) : 1080;
                var proxyUser = ProxyUsernameBox.Text ?? "";
                var proxyPw = !string.IsNullOrEmpty(proxyUser) ? ProxyPasswordBox.Password : "";

                var profile = new FtpProxyProfile
                {
                    ProxyHost = ProxyHostBox.Text,
                    ProxyPort = proxyPort,
                    ProxyCredentials = !string.IsNullOrEmpty(proxyUser)
                        ? new NetworkCredential(proxyUser, proxyPw) : null,
                    FtpHost = host, FtpPort = port,
                    FtpCredentials = new NetworkCredential(username, password),
                };
                client = new AsyncFtpClientSocks5Proxy(profile);
            }
            else
            {
                client = new AsyncFtpClient(host, username, password, port);
            }

            client.Config.EncryptionMode = FtpEncryptionMode.Explicit;
            client.Config.DataConnectionEncryption = true;
            client.Config.CustomStream = typeof(GnuTlsStream);
            var gnuConfig = new GnuConfig { SecuritySuite = GnuSuite.Secure128 };
            if (PreferTls12Box.IsChecked == true)
                gnuConfig.AdvancedOptions = [GnuAdvanced.NoTickets];
            client.Config.CustomStreamConfig = gnuConfig;
            client.ValidateCertificate += (_, ev) => ev.Accept = true;

            TestResultText.Text = "Connecting...";
            await client.Connect();

            var useCpsv = client.Capabilities.Contains(FtpCapability.CPSV);
            var controlHost = host;

            async Task<FtpListItem[]> ListDir(string path)
            {
                if (useCpsv)
                    return await CpsvDataHelper.ListDirectory(client, path, controlHost);
                return await client.GetListing(path, FtpListOption.AllFiles);
            }

            // For spreading/racing, only /incoming/ matters by default
            var rootPath = string.IsNullOrWhiteSpace(RootPathBox.Text) ? "/" : RootPathBox.Text.TrimEnd('/');
            if (string.IsNullOrEmpty(rootPath)) rootPath = "/";

            var scanAll = ScanAllDirsBox.IsChecked == true;
            if (!scanAll)
            {
                var incomingPath = rootPath == "/" ? "/incoming" : rootPath + "/incoming";
                TestResultText.Text = $"Checking {incomingPath}...";
                try
                {
                    var incomingItems = await ListDir(incomingPath);
                    var hasDirs = incomingItems.Any(i =>
                        (i.Type == FtpObjectType.Directory || i.Type == FtpObjectType.Link)
                        && !NonContentDirs.Contains(i.Name));
                    if (hasDirs)
                        rootPath = incomingPath;
                }
                catch { }
            }

            TestResultText.Text = $"Scanning {rootPath}...";

            var sections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            bool IsDirOrLink(FtpListItem i) =>
                (i.Type == FtpObjectType.Directory || i.Type == FtpObjectType.Link)
                && !NonContentDirs.Contains(i.Name);

            FtpListItem[] rootItems;
            try { rootItems = await ListDir(rootPath); }
            catch { rootItems = []; }

            var dirs = rootItems.Where(IsDirOrLink).ToList();

            foreach (var dir in dirs)
            {
                try
                {
                    TestResultText.Text = $"Checking {dir.Name}...";
                    var subItems = await ListDir(dir.FullName);
                    var subDirCount = subItems.Count(IsDirOrLink);

                    if (subDirCount >= 2)
                    {
                        var sectionName = dir.Name.ToUpper()
                            .Replace(" ", "_").Replace("-", "_");
                        sections.TryAdd(sectionName, dir.FullName);
                    }
                }
                catch { }
            }

            var watchPath = WatchPathBox.Text?.Trim();
            if (!string.IsNullOrEmpty(watchPath) && watchPath != rootPath)
            {
                try
                {
                    TestResultText.Text = $"Scanning {watchPath}...";
                    var watchItems = await ListDir(watchPath);
                    foreach (var dir2 in watchItems.Where(IsDirOrLink))
                    {
                        var sectionName = dir2.Name.ToUpper()
                            .Replace(" ", "_").Replace("-", "_");
                        sections.TryAdd(sectionName, dir2.FullName);
                    }
                }
                catch { }
            }

            await client.Disconnect();
            client.Dispose();

            if (sections.Count > 0)
            {
                // Merge discovered sections into the SectionMappings grid.
                // Don't touch existing rows (preserve user edits, enabled flags, tag rules).
                var added = 0;
                foreach (var (name, path) in sections.OrderBy(kv => kv.Key))
                {
                    var existing = _sectionMappings.FirstOrDefault(m =>
                        m.RemoteSection.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (existing != null)
                    {
                        // Update path if missing, leave everything else alone
                        if (string.IsNullOrEmpty(existing.Path))
                        {
                            existing.Path = path;
                            added++;
                        }
                        continue;
                    }
                    _sectionMappings.Add(new SectionMapping
                    {
                        IrcSection = name,
                        RemoteSection = name,
                        Path = path,
                        TriggerRegex = ".*",
                        Enabled = true
                    });
                    added++;
                }
                TestResultText.Text = $"Auto-detect: {added} section(s) added/updated.";
            }
            else
            {
                TestResultText.Text = "No content sections detected.";
                MessageBox.Show("No content sections detected. Ensure the root path points to a directory containing section folders.",
                    "Auto-Detect", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            TestResultText.Text = $"Auto-detect failed: {ex.Message}";
            MessageBox.Show($"Auto-detect failed: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            Log.Warning(ex, "Section auto-detect failed");
        }
        finally
        {
            SectionMappingsGrid.IsEnabled = true;
        }
    }

    private static Dictionary<string, string> ParseSections(string? text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(text)) return result;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.Contains('=')) continue;
            var key = line[..line.IndexOf('=')].Trim();
            var val = line[(line.IndexOf('=') + 1)..].Trim();
            if (key.Length > 0 && val.Length > 0)
                result[key] = val;
        }
        return result;
    }

    private void AddSiteSkiplistRule_Click(object sender, RoutedEventArgs e)
    {
        _siteSkiplist.Add(new SkiplistRule());
    }

    private void RemoveSiteSkiplistRule_Click(object sender, RoutedEventArgs e)
    {
        if (SiteSkiplistGrid.SelectedItem is SkiplistRule rule)
            _siteSkiplist.Remove(rule);
    }

    private void AddSectionMapping_Click(object sender, RoutedEventArgs e)
    {
        _sectionMappings.Add(new SectionMapping
        {
            IrcSection = "",
            RemoteSection = "",
            Path = "",
            TriggerRegex = ".*",
            Enabled = true
        });
    }

    private void RemoveSectionMapping_Click(object sender, RoutedEventArgs e)
    {
        if (SectionMappingsGrid.SelectedItem is SectionMapping m)
            _sectionMappings.Remove(m);
    }

    private void EditTagRules_Click(object sender, RoutedEventArgs e)
    {
        if (SectionMappingsGrid.SelectedItem is not SectionMapping mapping)
        {
            MessageBox.Show("Select a mapping first.", "Tag Rules",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new TagRulesDialog(mapping) { Owner = this };
        dlg.ShowDialog();
    }

    private void TestRules_Click(object sender, RoutedEventArgs e)
    {
        // Snapshot current unsaved rules into a temp site config for evaluation
        var tempSite = new SiteSpreadConfig
        {
            Skiplist = _siteSkiplist.ToList(),
            SectionMappings = _sectionMappings.ToList(),
            Affils = (SpreadAffilsBox.Text ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList()
        };

        var dlg = new RuleTestDialog(tempSite) { Owner = this };
        dlg.ShowDialog();
    }

    private void ImportSiteSkiplist_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import Skiplist Rules",
            Filter = "Text files (*.txt)|*.txt|All files|*.*"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var rules = Config.SiteImporter.ImportSkiplist(dialog.FileName);
            foreach (var rule in rules)
                _siteSkiplist.Add(rule);
            MessageBox.Show($"Imported {rules.Count} skiplist rule(s).", "Import", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Import failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AddCommonAnnouncePatterns_Click(object sender, RoutedEventArgs e)
    {
        var patterns = new[]
        {
            "#announce (?<section>\\w+) :: (?<release>\\S+)",
            "#pre \\[(?<section>[^\\]]+)\\]\\s*(?<release>\\S+)",
            "#announce NEW in (?<section>\\w+):\\s*(?<release>\\S+)",
        };

        var existing = (IrcAnnounceRulesBox.Text ?? "").Trim();
        var lines = new List<string>();
        if (!string.IsNullOrEmpty(existing))
            lines.AddRange(existing.Split('\n'));

        var existingSet = lines.Select(l => l.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var p in patterns)
        {
            if (!existingSet.Contains(p))
                lines.Add(p);
        }

        IrcAnnounceRulesBox.Text = string.Join("\n", lines.Where(l => !string.IsNullOrWhiteSpace(l)));
    }

    private void DetectIrcPatterns_Click(object sender, RoutedEventArgs e)
    {
        if (_serverManager == null)
        {
            MessageBox.Show("Server manager not available. Save and reopen this dialog.", "Detect",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var patterns = _serverManager.DetectIrcPatterns(_serverConfig.Id);
        if (patterns.Count == 0)
        {
            MessageBox.Show(
                "No patterns detected yet. Make sure IRC is connected and the bot has been active in channels for a few minutes.\n\n" +
                "The detector needs at least 20 messages from a nick that contain scene-style release names.",
                "No Patterns Found", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var existing = (IrcAnnounceRulesBox.Text ?? "").Trim();
        var lines = new List<string>();
        if (!string.IsNullOrEmpty(existing))
            lines.AddRange(existing.Split('\n'));

        var existingSet = lines.Select(l => l.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;

        foreach (var p in patterns.OrderByDescending(p => p.Confidence))
        {
            var rule = string.IsNullOrEmpty(p.Channel) ? p.SuggestedPattern : $"{p.Channel} {p.SuggestedPattern}";
            if (!existingSet.Contains(rule))
            {
                lines.Add(rule);
                added++;
            }
        }

        IrcAnnounceRulesBox.Text = string.Join("\n", lines.Where(l => !string.IsNullOrWhiteSpace(l)));

        var details = string.Join("\n", patterns.Select(p =>
            $"  {p.Channel} from {p.BotNick} ({p.MessageCount} msgs, {p.Confidence:P0} confidence)\n" +
            $"    Pattern: {p.SuggestedPattern}\n" +
            $"    Sample: {p.SampleMessages.FirstOrDefault() ?? ""}"));

        MessageBox.Show(
            $"Detected {patterns.Count} pattern(s), added {added} new rule(s):\n\n{details}",
            "Patterns Detected", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void AddDefaultSkiplistRules_Click(object sender, RoutedEventArgs e)
    {
        var defaults = new List<SkiplistRule>
        {
            new() { Pattern = "*incomplete*", Action = SkiplistAction.Deny, MatchDirectories = true, MatchFiles = false },
            new() { Pattern = "*NUKED*", Action = SkiplistAction.Deny, MatchDirectories = true, MatchFiles = true },
            new() { Pattern = ".message", Action = SkiplistAction.Deny, MatchDirectories = true, MatchFiles = true },
            new() { Pattern = ".banner", Action = SkiplistAction.Deny, MatchDirectories = true, MatchFiles = true },
            new() { Pattern = "*.jpg", Action = SkiplistAction.Deny, MatchDirectories = false, MatchFiles = true },
            new() { Pattern = "*.png", Action = SkiplistAction.Deny, MatchDirectories = false, MatchFiles = true },
            new() { Pattern = "*.txt", Action = SkiplistAction.Deny, MatchDirectories = false, MatchFiles = true },
            new() { Pattern = "*.url", Action = SkiplistAction.Deny, MatchDirectories = false, MatchFiles = true },
        };

        // Only add rules that don't already exist (by pattern)
        var existing = _siteSkiplist.Select(r => r.Pattern).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var rule in defaults)
        {
            if (!existing.Contains(rule.Pattern))
            {
                _siteSkiplist.Add(rule);
                added++;
            }
        }

        if (added == 0)
            MessageBox.Show("All default rules already exist.", "Skiplist", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void DetectSiteRules_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var host = HostBox.Text;
            var port = int.TryParse(PortBox.Text, out var p) ? Math.Clamp(p, 1, 65535) : 21;
            var username = UsernameBox.Text;
            var password = PasswordBox.Password;
            if (string.IsNullOrEmpty(password))
                password = CredentialStore.GetPassword(host, port, username) ?? "";

            if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(username))
            {
                MessageBox.Show("Enter host and username first.", "Detect Rules",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            AsyncFtpClient client;

            if (ProxyEnabledBox.IsChecked == true && !string.IsNullOrWhiteSpace(ProxyHostBox.Text))
            {
                var proxyPort = int.TryParse(ProxyPortBox.Text, out var pp) ? Math.Clamp(pp, 1, 65535) : 1080;
                var proxyUser = ProxyUsernameBox.Text ?? "";
                var proxyPw = !string.IsNullOrEmpty(proxyUser) ? ProxyPasswordBox.Password : "";

                var profile = new FtpProxyProfile
                {
                    ProxyHost = ProxyHostBox.Text,
                    ProxyPort = proxyPort,
                    ProxyCredentials = !string.IsNullOrEmpty(proxyUser)
                        ? new NetworkCredential(proxyUser, proxyPw) : null,
                    FtpHost = host, FtpPort = port,
                    FtpCredentials = new NetworkCredential(username, password),
                };
                client = new AsyncFtpClientSocks5Proxy(profile);
            }
            else
            {
                client = new AsyncFtpClient(host, username, password, port);
            }

            client.Config.EncryptionMode = FtpEncryptionMode.Explicit;
            client.Config.DataConnectionEncryption = true;
            client.Config.CustomStream = typeof(GnuTlsStream);
            var gnuConfig = new GnuConfig { SecuritySuite = GnuSuite.Secure128 };
            if (PreferTls12Box.IsChecked == true)
                gnuConfig.AdvancedOptions = [GnuAdvanced.NoTickets];
            client.Config.CustomStreamConfig = gnuConfig;
            client.ValidateCertificate += (_, ev) => ev.Accept = true;

            TestResultText.Text = "Connecting for SITE RULES...";
            await client.Connect();

            var reply = await client.Execute("SITE RULES");

            await client.Disconnect();
            client.Dispose();

            if (!reply.Success)
            {
                TestResultText.Text = $"SITE RULES failed: {reply.Code} {reply.Message}";
                return;
            }

            // FluentFTP stores the full multi-line response in InfoMessages
            var fullResponse = reply.InfoMessages ?? reply.Message ?? "";
            if (string.IsNullOrWhiteSpace(fullResponse))
            {
                TestResultText.Text = "SITE RULES returned empty response.";
                return;
            }

            Log.Information("SITE RULES raw response ({Length} chars):\n{Response}", fullResponse.Length, fullResponse);
            var parsed = SiteRulesParser.Parse(fullResponse);
            Log.Information("SITE RULES parsed: {RuleCount} rules, {SkipCount} skiplist, {AffilCount} affils, maxUp={Up} maxDown={Down}",
                parsed.RawRules.Count, SiteRulesParser.ToSkiplistRules(parsed).Count,
                parsed.Affils.Count, parsed.MaxUploads, parsed.MaxDownloads);
            var changes = new List<string>();

            // Apply max upload/download slots
            if (parsed.MaxUploads.HasValue)
            {
                SpreadMaxUpBox.Text = parsed.MaxUploads.Value.ToString();
                changes.Add($"Max uploads: {parsed.MaxUploads.Value}");
            }
            if (parsed.MaxDownloads.HasValue)
            {
                SpreadMaxDownBox.Text = parsed.MaxDownloads.Value.ToString();
                changes.Add($"Max downloads: {parsed.MaxDownloads.Value}");
            }
            if (parsed.MaxSimultaneous.HasValue)
            {
                // Apply to both if no specific upload/download limits
                if (!parsed.MaxUploads.HasValue)
                    SpreadMaxUpBox.Text = parsed.MaxSimultaneous.Value.ToString();
                if (!parsed.MaxDownloads.HasValue)
                    SpreadMaxDownBox.Text = parsed.MaxSimultaneous.Value.ToString();
                changes.Add($"Max simultaneous: {parsed.MaxSimultaneous.Value}");
            }

            // Apply affils
            if (parsed.Affils.Count > 0)
            {
                var currentAffils = (SpreadAffilsBox.Text ?? "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var a in parsed.Affils)
                    currentAffils.Add(a);
                SpreadAffilsBox.Text = string.Join(", ", currentAffils.OrderBy(a => a));
                changes.Add($"Affils: {string.Join(", ", parsed.Affils)}");
            }

            // Apply skiplist rules from parsed rules
            var skiplistRules = SiteRulesParser.ToSkiplistRules(parsed);
            if (skiplistRules.Count > 0)
            {
                var existingPatterns = _siteSkiplist.Select(r => r.Pattern).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var added = 0;
                foreach (var rule in skiplistRules)
                {
                    if (!existingPatterns.Contains(rule.Pattern))
                    {
                        _siteSkiplist.Add(rule);
                        existingPatterns.Add(rule.Pattern);
                        added++;
                    }
                }
                if (added > 0)
                    changes.Add($"{added} skiplist rule(s)");
            }

            // Apply denied content patterns
            if (parsed.DeniedPatterns.Count > 0)
                changes.Add($"Denied: {string.Join(", ", parsed.DeniedPatterns)}");

            if (changes.Count > 0)
            {
                TestResultText.Text = $"SITE RULES applied: {string.Join("; ", changes)}";

                // Show full rules in a detail dialog
                var rulesText = string.Join("\n", parsed.RawRules.Select((r, i) => $"{i + 1}. {r}"));
                MessageBox.Show(
                    $"Detected {parsed.RawRules.Count} rule(s). Applied:\n\n" +
                    string.Join("\n", changes.Select(c => $"  • {c}")) +
                    $"\n\nFull rules:\n{rulesText}",
                    "SITE RULES", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                TestResultText.Text = "SITE RULES returned no actionable rules.";
                var rulesText = string.Join("\n", parsed.RawRules.Select((r, i) => $"{i + 1}. {r}"));
                if (parsed.RawRules.Count > 0)
                {
                    MessageBox.Show(
                        $"Found {parsed.RawRules.Count} rule(s) but none could be auto-applied:\n\n{rulesText}",
                        "SITE RULES", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }
        catch (Exception ex)
        {
            TestResultText.Text = $"SITE RULES failed: {ex.Message}";
            Log.Warning(ex, "SITE RULES detection failed");
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
