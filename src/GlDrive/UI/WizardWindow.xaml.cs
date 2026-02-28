using System.IO;
using System.Windows;
using System.Windows.Controls;
using FluentFTP;
using FluentFTP.GnuTLS;
using FluentFTP.GnuTLS.Enums;
using GlDrive.Config;
using GlDrive.Tls;
using Serilog;

namespace GlDrive.UI;

public partial class WizardWindow : Window
{
    private int _step;
    private readonly AppConfig _config = new();
    private readonly CertificateManager _certManager = new();
    private string _password = "";
    private string _certFingerprint = "";

    // Step panels
    private readonly StackPanel _welcomePanel;
    private readonly StackPanel _connectionPanel;
    private readonly StackPanel _tlsPanel;
    private readonly StackPanel _mountPanel;
    private readonly StackPanel _confirmPanel;

    // Connection fields
    private readonly TextBox _hostBox;
    private readonly TextBox _portBox;
    private readonly TextBox _usernameBox;
    private readonly PasswordBox _passwordBox;
    private readonly TextBox _rootPathBox;

    // TLS fields
    private readonly TextBlock _certInfo;
    private readonly CheckBox _trustCertBox;

    // Mount fields
    private readonly ComboBox _driveLetterBox;
    private readonly TextBox _volumeLabelBox;
    private readonly CheckBox _autoMountBox;

    // Confirm fields
    private readonly TextBlock _summaryText;
    private readonly TextBlock _testResultText;

    public WizardWindow()
    {
        InitializeComponent();

        // Step 1: Welcome
        _welcomePanel = new StackPanel();
        _welcomePanel.Children.Add(new TextBlock
        {
            Text = "Welcome to GlDrive!",
            FontSize = 18, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 12)
        });
        _welcomePanel.Children.Add(new TextBlock
        {
            Text = "GlDrive mounts a glftpd FTPS server as a Windows drive letter, " +
                   "allowing you to access remote files directly from Explorer and any application.\n\n" +
                   "This wizard will help you configure your connection.",
            TextWrapping = TextWrapping.Wrap
        });

        // Step 2: Connection
        _connectionPanel = new StackPanel();
        _connectionPanel.Children.Add(Label("Host:"));
        _hostBox = new TextBox { Margin = new Thickness(0, 0, 0, 8) };
        _connectionPanel.Children.Add(_hostBox);
        _connectionPanel.Children.Add(Label("Port:"));
        _portBox = new TextBox { Text = "1337", Margin = new Thickness(0, 0, 0, 8), Width = 100, HorizontalAlignment = HorizontalAlignment.Left };
        _connectionPanel.Children.Add(_portBox);
        _connectionPanel.Children.Add(Label("Username:"));
        _usernameBox = new TextBox { Margin = new Thickness(0, 0, 0, 8) };
        _connectionPanel.Children.Add(_usernameBox);
        _connectionPanel.Children.Add(Label("Password:"));
        _passwordBox = new PasswordBox { Margin = new Thickness(0, 0, 0, 8) };
        _connectionPanel.Children.Add(_passwordBox);
        _connectionPanel.Children.Add(Label("Root Path:"));
        _rootPathBox = new TextBox { Text = "/", Margin = new Thickness(0, 0, 0, 8) };
        _connectionPanel.Children.Add(_rootPathBox);

        // Step 3: TLS
        _tlsPanel = new StackPanel();
        _tlsPanel.Children.Add(new TextBlock
        {
            Text = "TLS Certificate Verification",
            FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12)
        });
        _certInfo = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        _tlsPanel.Children.Add(_certInfo);
        _trustCertBox = new CheckBox { Content = "Trust this certificate", Margin = new Thickness(0, 0, 0, 8) };
        _tlsPanel.Children.Add(_trustCertBox);

        // Step 4: Mount
        _mountPanel = new StackPanel();
        _mountPanel.Children.Add(Label("Drive Letter:"));
        _driveLetterBox = new ComboBox { Width = 80, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8) };
        var used = DriveInfo.GetDrives().Select(d => d.Name[0]).ToHashSet();
        foreach (var c in Enumerable.Range('D', 23).Select(c => ((char)c).ToString()).Where(c => !used.Contains(c[0])))
            _driveLetterBox.Items.Add(c);
        if (_driveLetterBox.Items.Contains("G")) _driveLetterBox.SelectedItem = "G";
        else if (_driveLetterBox.Items.Count > 0) _driveLetterBox.SelectedIndex = 0;
        _mountPanel.Children.Add(_driveLetterBox);
        _mountPanel.Children.Add(Label("Volume Label:"));
        _volumeLabelBox = new TextBox { Text = "glFTPd", Margin = new Thickness(0, 0, 0, 8) };
        _mountPanel.Children.Add(_volumeLabelBox);
        _autoMountBox = new CheckBox { Content = "Auto-mount on start", IsChecked = true, Margin = new Thickness(0, 8, 0, 0) };
        _mountPanel.Children.Add(_autoMountBox);

        // Step 5: Confirm
        _confirmPanel = new StackPanel();
        _summaryText = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        _confirmPanel.Children.Add(_summaryText);
        _testResultText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        _confirmPanel.Children.Add(_testResultText);

        ShowStep(0);
    }

    private void ShowStep(int step)
    {
        _step = step;
        var titles = new[] { "Step 1 of 5 — Welcome", "Step 2 of 5 — Connection", "Step 3 of 5 — TLS Certificate",
                             "Step 4 of 5 — Mount Options", "Step 5 of 5 — Confirm & Connect" };
        StepIndicator.Text = titles[step];
        BackButton.IsEnabled = step > 0;
        NextButton.Content = step == 4 ? "Finish" : "Next";

        StepContent.Content = step switch
        {
            0 => _welcomePanel,
            1 => _connectionPanel,
            2 => _tlsPanel,
            3 => _mountPanel,
            4 => _confirmPanel,
            _ => null
        };

        if (step == 2) AttemptTlsConnect();
        if (step == 4) BuildSummary();
    }

    private async void AttemptTlsConnect()
    {
        _certInfo.Text = "Connecting to verify TLS certificate...";
        _trustCertBox.IsChecked = false;

        try
        {
            var client = new AsyncFtpClient(_hostBox.Text, _usernameBox.Text, _passwordBox.Password, int.Parse(_portBox.Text));
            client.Config.EncryptionMode = FtpEncryptionMode.Explicit;
            client.Config.DataConnectionEncryption = true;
            client.Config.CustomStream = typeof(GnuTlsStream);
            client.Config.CustomStreamConfig = new GnuConfig
            {
                SecuritySuite = GnuSuite.Secure128,
                AdvancedOptions = [GnuAdvanced.NoTickets]
            };

            string fingerprint = "";
            client.ValidateCertificate += (_, e) =>
            {
                fingerprint = _certManager.GetFingerprint(e.Certificate);
                e.Accept = true; // Accept for verification display
            };

            await client.Connect();
            await client.Disconnect();
            client.Dispose();

            _certFingerprint = fingerprint;
            _certInfo.Text = $"Server certificate SHA-256 fingerprint:\n\n{FormatFingerprint(fingerprint)}\n\nDo you trust this certificate?";
            _trustCertBox.IsChecked = true;
        }
        catch (Exception ex)
        {
            _certInfo.Text = $"Could not connect: {ex.Message}\n\nYou can continue and configure later.";
            _trustCertBox.IsChecked = false;
        }
    }

    private void BuildSummary()
    {
        _summaryText.Text = $"Host: {_hostBox.Text}:{_portBox.Text}\n" +
                            $"Username: {_usernameBox.Text}\n" +
                            $"Root Path: {_rootPathBox.Text}\n" +
                            $"Drive Letter: {_driveLetterBox.SelectedItem}:\n" +
                            $"Volume Label: {_volumeLabelBox.Text}\n" +
                            $"Auto-mount: {(_autoMountBox.IsChecked == true ? "Yes" : "No")}";
        _testResultText.Text = "";
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_step > 0) ShowStep(_step - 1);
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_step == 1)
        {
            // Validate connection fields
            if (string.IsNullOrWhiteSpace(_hostBox.Text) || string.IsNullOrWhiteSpace(_usernameBox.Text))
            {
                MessageBox.Show("Host and Username are required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _password = _passwordBox.Password;
        }

        if (_step == 2 && _trustCertBox.IsChecked == true && !string.IsNullOrEmpty(_certFingerprint))
        {
            var key = $"{_hostBox.Text}:{_portBox.Text}";
            _certManager.TrustCertificate(key, _certFingerprint);
        }

        if (_step == 4)
        {
            // Save config
            _config.Connection.Host = _hostBox.Text;
            _config.Connection.Port = int.TryParse(_portBox.Text, out var p) ? p : 21;
            _config.Connection.Username = _usernameBox.Text;
            _config.Connection.RootPath = _rootPathBox.Text;
            _config.Mount.DriveLetter = _driveLetterBox.SelectedItem?.ToString() ?? "G";
            _config.Mount.VolumeLabel = _volumeLabelBox.Text;
            _config.Mount.AutoMountOnStart = _autoMountBox.IsChecked == true;

            ConfigManager.Save(_config);

            if (!string.IsNullOrEmpty(_password))
            {
                CredentialStore.SavePassword(
                    _config.Connection.Host,
                    _config.Connection.Port,
                    _config.Connection.Username,
                    _password);
            }

            Log.Information("Wizard completed, config saved");
            DialogResult = true;
            Close();
            return;
        }

        ShowStep(_step + 1);
    }

    private static TextBlock Label(string text) =>
        new() { Text = text, Margin = new Thickness(0, 0, 0, 4) };

    private static string FormatFingerprint(string fp) =>
        string.Join(":", Enumerable.Range(0, fp.Length / 2).Select(i => fp.Substring(i * 2, 2)));
}
