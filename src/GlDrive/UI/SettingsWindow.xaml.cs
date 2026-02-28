using System.Diagnostics;
using System.IO;
using System.Windows;
using FluentFTP;
using FluentFTP.GnuTLS;
using FluentFTP.GnuTLS.Enums;
using GlDrive.Config;
using GlDrive.Services;
using GlDrive.Tls;
using Serilog;

namespace GlDrive.UI;

public partial class SettingsWindow : Window
{
    private readonly AppConfig _config;
    private readonly MountService _mountService;
    private readonly SettingsViewModel _vm;

    public SettingsWindow(AppConfig config, MountService mountService)
    {
        InitializeComponent();
        _config = config;
        _mountService = mountService;
        _vm = new SettingsViewModel(config);
        DataContext = _vm;
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        TestResultText.Text = "Testing connection...";
        try
        {
            var password = PasswordBox.Password;
            if (string.IsNullOrEmpty(password))
                password = CredentialStore.GetPassword(_vm.Host, int.Parse(_vm.Port), _vm.Username) ?? "";

            var client = new AsyncFtpClient(_vm.Host, _vm.Username, password, int.Parse(_vm.Port));
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

    private void ClearCerts_Click(object sender, RoutedEventArgs e)
    {
        var certMgr = new CertificateManager(_config.Tls.CertificateFingerprintFile);
        certMgr.ClearTrustedCertificates();
        _vm.RefreshCertsInfo();
        MessageBox.Show("Trusted certificates cleared.", "GlDrive", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        var logPath = Path.Combine(ConfigManager.AppDataPath, "logs");
        Directory.CreateDirectory(logPath);
        Process.Start("explorer.exe", logPath);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _vm.ApplyTo(_config);

        // Save password if entered
        if (!string.IsNullOrEmpty(PasswordBox.Password))
        {
            CredentialStore.SavePassword(
                _config.Connection.Host,
                _config.Connection.Port,
                _config.Connection.Username,
                PasswordBox.Password);
        }

        ConfigManager.Save(_config);
        Log.Information("Settings saved");
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
