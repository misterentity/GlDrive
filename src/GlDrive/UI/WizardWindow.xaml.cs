using System.IO;
using System.Windows;
using System.Windows.Controls;
using FluentFTP;
using FluentFTP.GnuTLS;
using FluentFTP.GnuTLS.Enums;
using GlDrive.Config;
using GlDrive.Ftp;
using GlDrive.Tls;
using Serilog;

namespace GlDrive.UI;

public partial class WizardWindow : Window
{
    private int _step;
    private readonly AppConfig _config = new();
    private readonly ServerConfig _serverConfig = new() { Name = "Server 1" };
    private readonly CertificateManager _certManager = new();
    private string _password = "";
    private string _certFingerprint = "";
    internal bool DemoMode;

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
            FontSize = 26, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 16)
        });
        _welcomePanel.Children.Add(new TextBlock
        {
            Text = "GlDrive mounts a glftpd FTPS server as a Windows drive letter, " +
                   "allowing you to access remote files directly from Explorer and any application.\n\n" +
                   "Have your server address, port, username and password ready. " +
                   "You will verify the connection, choose a drive letter, and review your settings before saving.",
            TextWrapping = TextWrapping.Wrap, FontSize = 14, LineHeight = 23
        });

        // Step 2: Connection
        _connectionPanel = new StackPanel();
        _hostBox = new TextBox();
        _portBox = new TextBox { Text = "1337", MaxLength = 5 };
        var addressFields = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        addressFields.ColumnDefinitions.Add(new ColumnDefinition());
        addressFields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        var hostField = Field("Host (required):", _hostBox);
        hostField.Margin = new Thickness(0, 0, 14, 0);
        addressFields.Children.Add(hostField);
        var portField = Field("Port:", _portBox);
        Grid.SetColumn(portField, 1);
        addressFields.Children.Add(portField);
        _connectionPanel.Children.Add(addressFields);

        _usernameBox = new TextBox();
        _passwordBox = new PasswordBox();
        var credentials = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        credentials.ColumnDefinitions.Add(new ColumnDefinition());
        credentials.ColumnDefinitions.Add(new ColumnDefinition());
        var usernameField = Field("Username (required):", _usernameBox);
        usernameField.Margin = new Thickness(0, 0, 14, 0);
        credentials.Children.Add(usernameField);
        var passwordField = Field("Password:", _passwordBox);
        Grid.SetColumn(passwordField, 1);
        credentials.Children.Add(passwordField);
        _connectionPanel.Children.Add(credentials);

        _rootPathBox = new TextBox { Text = "/", Margin = new Thickness(0, 0, 0, 8) };
        _connectionPanel.Children.Add(Field("Root path:", _rootPathBox));
        _connectionPanel.Children.Add(new TextBlock
        {
            Text = "Use / for the server root, or enter the remote folder you want to mount.",
            TextWrapping = TextWrapping.Wrap, FontSize = 13
        });
        System.Windows.Automation.AutomationProperties.SetName(_hostBox, "Server host, required");
        System.Windows.Automation.AutomationProperties.SetName(_portBox, "Server port");
        System.Windows.Automation.AutomationProperties.SetName(_usernameBox, "Username, required");
        System.Windows.Automation.AutomationProperties.SetName(_passwordBox, "Password");
        System.Windows.Automation.AutomationProperties.SetName(_rootPathBox, "Remote root path");

        // Step 3: TLS
        _tlsPanel = new StackPanel();
        _tlsPanel.Children.Add(new TextBlock
        {
            Text = "TLS Certificate Verification",
            FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12)
        });
        _certInfo = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
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
        _summaryText = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap, LineHeight = 28, Margin = new Thickness(0, 0, 0, 12) };
        _confirmPanel.Children.Add(_summaryText);
        _testResultText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        _confirmPanel.Children.Add(_testResultText);

        ShowStep(0);
    }

    internal void ShowStep(int step)
    {
        _step = step;
        ValidationText.Text = "";
        var titles = new[] { "Step 1 of 5 — Welcome", "Step 2 of 5 — Connection", "Step 3 of 5 — TLS Certificate",
                             "Step 4 of 5 — Mount Options", "Step 5 of 5 — Review settings" };
        StepIndicator.Text = titles[step];
        StepProgress.Value = step + 1;
        BackButton.IsEnabled = step > 0;
        NextButton.Content = step switch
        {
            0 => "Get started",
            1 => "Verify connection",
            3 => "Review settings",
            4 => "Save connection",
            _ => "Continue"
        };

        StepContent.Content = step switch
        {
            0 => _welcomePanel,
            1 => _connectionPanel,
            2 => _tlsPanel,
            3 => _mountPanel,
            4 => _confirmPanel,
            _ => null
        };

        if (step == 2 && !DemoMode) AttemptTlsConnect();
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
            ServerEditDialog.AttachTofuValidation(client, _hostBox.Text, int.Parse(_portBox.Text), _certManager);
            client.ValidateCertificate += (_, e) =>
            {
                fingerprint = _certManager.GetFingerprint(e.Certificate);
            };

            await GuardedFtpLogin.ConnectAsync(client,
                new FtpLoginAccount(_hostBox.Text, int.Parse(_portBox.Text), _usernameBox.Text), manualRetry: true);
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
                ValidationText.Text = "Enter a server host and username to continue.";
                if (string.IsNullOrWhiteSpace(_hostBox.Text)) _hostBox.Focus();
                else _usernameBox.Focus();
                return;
            }
            if (!int.TryParse(_portBox.Text, out var port) || port is < 1 or > 65535)
            {
                ValidationText.Text = "Enter a port between 1 and 65535.";
                _portBox.Focus();
                _portBox.SelectAll();
                return;
            }
            _password = _passwordBox.Password;
        }

        if (_step == 4)
        {
            // Build ServerConfig
            _serverConfig.Connection.Host = _hostBox.Text;
            _serverConfig.Connection.Port = int.TryParse(_portBox.Text, out var p) ? p : 21;
            _serverConfig.Connection.Username = _usernameBox.Text;
            _serverConfig.Connection.RootPath = _rootPathBox.Text;
            _serverConfig.Mount.DriveLetter = _driveLetterBox.SelectedItem?.ToString() ?? "G";
            _serverConfig.Mount.VolumeLabel = _volumeLabelBox.Text;
            _serverConfig.Mount.AutoMountOnStart = _autoMountBox.IsChecked == true;
            _serverConfig.Name = _hostBox.Text;

            // Add to config and save
            _config.Servers.Add(_serverConfig);
            ConfigManager.Save(_config, "wizard");

            if (!string.IsNullOrEmpty(_password))
            {
                CredentialStore.SavePassword(
                    _serverConfig.Connection.Host,
                    _serverConfig.Connection.Port,
                    _serverConfig.Connection.Username,
                    _password);
            }

            Log.Information("Wizard completed, config saved with server {ServerName}", _serverConfig.Name);
            DialogResult = true;
            Close();
            return;
        }

        ShowStep(_step + 1);
    }

    internal void PreFillDemo()
    {
        _hostBox.Text = "ftp.example.com";
        _portBox.Text = "21";
        _usernameBox.Text = "myuser";
        _rootPathBox.Text = "/";
        _certInfo.Text = "Server certificate SHA-256 fingerprint:\n\n" +
            "A1:B2:C3:D4:E5:F6:78:90:AB:CD:EF:01:23:45:67:89\n" +
            "A1:B2:C3:D4:E5:F6:78:90:AB:CD:EF:01:23:45:67:89\n\n" +
            "Do you trust this certificate?";
        _trustCertBox.IsChecked = true;
    }

    private static StackPanel Field(string label, Control control)
    {
        var panel = new StackPanel();
        panel.Children.Add(Label(label));
        panel.Children.Add(control);
        return panel;
    }

    private static TextBlock Label(string text) =>
        new() { Text = text, FontSize = 13, Margin = new Thickness(0, 0, 0, 5) };

    private static string FormatFingerprint(string fp) =>
        string.Join(":", Enumerable.Range(0, fp.Length / 2).Select(i => fp.Substring(i * 2, 2)));
}
