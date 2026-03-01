using System.IO;
using System.Windows;
using FluentFTP;
using FluentFTP.GnuTLS;
using FluentFTP.GnuTLS.Enums;
using GlDrive.Config;
using Serilog;

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

        if (existing != null)
        {
            NameBox.Text = existing.Name;
            HostBox.Text = existing.Connection.Host;
            PortBox.Text = existing.Connection.Port.ToString();
            UsernameBox.Text = existing.Connection.Username;
            RootPathBox.Text = existing.Connection.RootPath;
            DriveLetterBox.SelectedItem = existing.Mount.DriveLetter;
            VolumeLabelBox.Text = existing.Mount.VolumeLabel;
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
            var password = PasswordBox.Password;
            if (string.IsNullOrEmpty(password))
                password = CredentialStore.GetPassword(HostBox.Text, int.Parse(PortBox.Text), UsernameBox.Text) ?? "";

            var client = new AsyncFtpClient(HostBox.Text, UsernameBox.Text, password, int.Parse(PortBox.Text));
            client.Config.EncryptionMode = FtpEncryptionMode.Explicit;
            client.Config.DataConnectionEncryption = true;
            client.Config.CustomStream = typeof(GnuTlsStream);
            client.Config.CustomStreamConfig = new GnuConfig { SecuritySuite = GnuSuite.Secure128 };
            client.ValidateCertificate += (_, e) => e.Accept = true;

            await client.Connect();
            var listing = await client.GetListing("/");
            await client.Disconnect();
            client.Dispose();

            TestResultText.Text = $"Success! Connected and listed {listing.Length} items in root.";
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
        _serverConfig.Mount.DriveLetter = DriveLetterBox.SelectedItem?.ToString() ?? "G";
        _serverConfig.Mount.VolumeLabel = VolumeLabelBox.Text;
        _serverConfig.Mount.AutoMountOnStart = AutoMountBox.IsChecked == true;
        _serverConfig.Tls.PreferTls12 = PreferTls12Box.IsChecked == true;

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

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
