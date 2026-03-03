using System.IO;
using System.Net;
using System.Windows;
using FluentFTP;
using FluentFTP.GnuTLS;
using FluentFTP.GnuTLS.Enums;
using FluentFTP.Proxy.AsyncProxy;
using GlDrive.Config;
using GlDrive.Ftp;
using Serilog;
using static GlDrive.Config.SearchMethod;

namespace GlDrive.UI;

public partial class ServerEditDialog : Window
{
    private readonly ServerConfig _serverConfig;
    private string _password = "";

    public ServerConfig Result => _serverConfig;

    public ServerEditDialog(ServerConfig? existing = null)
    {
        InitializeComponent();

        _serverConfig = existing ?? new ServerConfig();

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

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        TestResultText.Text = "Testing connection...";
        try
        {
            var host = HostBox.Text;
            var port = int.TryParse(PortBox.Text, out var p) ? p : 21;
            var username = UsernameBox.Text;
            var password = PasswordBox.Password;
            if (string.IsNullOrEmpty(password))
                password = CredentialStore.GetPassword(host, port, username) ?? "";

            AsyncFtpClient client;

            if (ProxyEnabledBox.IsChecked == true && !string.IsNullOrWhiteSpace(ProxyHostBox.Text))
            {
                var proxyPort = int.TryParse(ProxyPortBox.Text, out var pp) ? pp : 1080;
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
            client.ValidateCertificate += (_, e) => e.Accept = true;

            await client.Connect();
            var listing = await client.GetListing("/");
            await client.Disconnect();
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
        _serverConfig.Connection.Port = int.TryParse(PortBox.Text, out var p) ? p : 21;
        _serverConfig.Connection.Username = UsernameBox.Text;
        _serverConfig.Connection.RootPath = RootPathBox.Text;

        // Proxy
        if (ProxyEnabledBox.IsChecked == true && !string.IsNullOrWhiteSpace(ProxyHostBox.Text))
        {
            _serverConfig.Connection.Proxy ??= new ProxyConfig();
            _serverConfig.Connection.Proxy.Enabled = true;
            _serverConfig.Connection.Proxy.Host = ProxyHostBox.Text;
            _serverConfig.Connection.Proxy.Port = int.TryParse(ProxyPortBox.Text, out var pp) ? pp : 1080;
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
            var port = int.TryParse(PortBox.Text, out var p) ? p : 21;
            var username = UsernameBox.Text;
            var password = PasswordBox.Password;
            if (string.IsNullOrEmpty(password))
                password = CredentialStore.GetPassword(host, port, username) ?? "";

            AsyncFtpClient client;

            if (ProxyEnabledBox.IsChecked == true && !string.IsNullOrWhiteSpace(ProxyHostBox.Text))
            {
                var proxyPort = int.TryParse(ProxyPortBox.Text, out var pp) ? pp : 1080;
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
                .Where(i => i.Type == FtpObjectType.Directory && !NonContentDirs.Contains(i.Name))
                .ToList();

            var contentPaths = new List<string>();

            foreach (var dir in topDirs)
            {
                try
                {
                    var subItems = await ListDir(dir.FullName);
                    var subDirCount = subItems.Count(i => i.Type == FtpObjectType.Directory
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

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
