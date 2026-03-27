using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GlDrive.Config;
using Microsoft.Win32;
using Renci.SshNet;
using Serilog;

namespace GlDrive.UI;

public partial class GlftpdInstallerWindow : Window
{
    private SshClient? _ssh;
    private SftpClient? _sftp;
    private bool _installing;
    private readonly DispatcherTimer _elapsedTimer;
    private Stopwatch? _stopwatch;

    // Path to the glftpd-installer repo (install.sh lives here)
    private static readonly string InstallerRepoPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "glftpd-installer");

    // Script checkboxes mapped to cache keys
    private readonly Dictionary<string, CheckBox> _scriptChecks = new();

    // Dynamic sections
    private readonly List<SectionEntry> _sections = new();

    public GlftpdInstallerWindow()
    {
        InitializeComponent();

        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick += (_, _) =>
        {
            if (_stopwatch != null)
                ElapsedLabel.Text = _stopwatch.Elapsed.ToString(@"mm\:ss");
        };

        // Map script checkboxes
        _scriptChecks["eur0presystem"] = ChkEur0presystem;
        _scriptChecks["slvprebw"] = ChkSlvprebw;
        _scriptChecks["ircadmin"] = ChkIrcadmin;
        _scriptChecks["request"] = ChkRequest;
        _scriptChecks["trial"] = ChkTrial;
        _scriptChecks["vacation"] = ChkVacation;
        _scriptChecks["whereami"] = ChkWhereami;
        _scriptChecks["precheck"] = ChkPrecheck;
        _scriptChecks["autonuke"] = ChkAutonuke;
        _scriptChecks["psxcimdb"] = ChkPsxcimdb;
        _scriptChecks["addip"] = ChkAddip;
        _scriptChecks["top"] = ChkTop;
        _scriptChecks["ircnick"] = ChkIrcnick;
        _scriptChecks["archiver"] = ChkArchiver;
        _scriptChecks["section_traffic"] = ChkSectionTraffic;
        _scriptChecks["botnuke"] = ChkBotnuke;

        // Default sections
        RebuildSections(3);
        if (_sections.Count >= 3)
        {
            _sections[0].Name.Text = "MP3";   _sections[0].Path.Text = "/site/MP3";
            _sections[1].Name.Text = "0DAY";  _sections[1].Path.Text = "/site/0DAY";
            _sections[2].Name.Text = "TV";    _sections[2].Path.Text = "/site/TV";
        }
    }

    // ── SSH Connection ──

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        var host = SshHost.Text.Trim();
        if (string.IsNullOrEmpty(host)) { ShowError("Enter a hostname"); return; }
        if (!int.TryParse(SshPort.Text, out var port)) { ShowError("Invalid port"); return; }
        var user = SshUsername.Text.Trim();
        if (string.IsNullOrEmpty(user)) { ShowError("Enter a username"); return; }

        Disconnect();

        try
        {
            var authMethods = new List<AuthenticationMethod>();
            var keyFile = SshKeyFile.Text.Trim();
            if (!string.IsNullOrEmpty(keyFile) && File.Exists(keyFile))
            {
                var pw = SshPassword.Password;
                authMethods.Add(string.IsNullOrEmpty(pw)
                    ? new PrivateKeyAuthenticationMethod(user, new PrivateKeyFile(keyFile))
                    : new PrivateKeyAuthenticationMethod(user, new PrivateKeyFile(keyFile, pw)));
            }
            if (!string.IsNullOrEmpty(SshPassword.Password))
                authMethods.Add(new PasswordAuthenticationMethod(user, SshPassword.Password));

            if (authMethods.Count == 0) { ShowError("Provide a password or key file"); return; }

            var connInfo = new ConnectionInfo(host, port, user, authMethods.ToArray());
            connInfo.Timeout = TimeSpan.FromSeconds(10);

            _ssh = new SshClient(connInfo);
            _ssh.Connect();

            _sftp = new SftpClient(connInfo);
            _sftp.Connect();
            _sftp.OperationTimeout = TimeSpan.FromSeconds(60);

            // Save password to credential store
            if (!string.IsNullOrEmpty(SshPassword.Password))
                CredentialStore.SaveSshPassword(host, port, user, SshPassword.Password);

            ConnectionStatus.Text = $"Connected to {host}:{port}";
            StatusBar.Text = $"Connected — {host}:{port}";
            AppendLog("SSH connection established", "success");
        }
        catch (Exception ex)
        {
            _ssh?.Dispose(); _ssh = null;
            _sftp?.Dispose(); _sftp = null;
            AppendLog($"Connection failed: {ex.Message}", "error");
            ShowError($"Connection failed: {ex.Message}");
        }
    }

    private void Disconnect()
    {
        try { _sftp?.Disconnect(); } catch { }
        try { _ssh?.Disconnect(); } catch { }
        _sftp?.Dispose(); _sftp = null;
        _ssh?.Dispose(); _ssh = null;
        ConnectionStatus.Text = "Disconnected";
        StatusBar.Text = "Disconnected";
    }

    private void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        Disconnect();
        AppendLog("Disconnected", "info");
    }

    private void BrowseKeyFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select SSH Key File",
            Filter = "Key files (*.pem;*.key;*.ppk)|*.pem;*.key;*.ppk|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true)
            SshKeyFile.Text = dlg.FileName;
    }

    // ── Dynamic Sections ──

    private void SectionCount_Changed(object sender, TextChangedEventArgs e)
    {
        if (int.TryParse(SectionCount.Text, out var count))
            RebuildSections(Math.Clamp(count, 1, 22));
    }

    private void RebuildSections(int count)
    {
        // Preserve existing values
        while (_sections.Count < count)
        {
            _sections.Add(new SectionEntry
            {
                Name = new TextBox { Text = "", Style = (Style)FindResource("FieldBox"), Margin = new Thickness(0, 3, 0, 3) },
                Path = new TextBox { Text = "/site/", Style = (Style)FindResource("FieldBox"), Margin = new Thickness(0, 3, 0, 3) },
                Dated = new CheckBox { Content = "Dated", Style = (Style)FindResource("ScriptCheck") }
            });
        }
        while (_sections.Count > count)
            _sections.RemoveAt(_sections.Count - 1);

        if (SectionsPanel == null) return;
        SectionsPanel.Children.Clear();
        for (int i = 0; i < _sections.Count; i++)
        {
            var sec = _sections[i];
            var num = i + 1;

            var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition());
            grid.RowDefinitions.Add(new RowDefinition());

            var nameLabel = new TextBlock { Text = $"Section {num}", Style = (Style)FindResource("FieldLabel") };
            Grid.SetRow(nameLabel, 0); Grid.SetColumn(nameLabel, 0);
            grid.Children.Add(nameLabel);
            Grid.SetRow(sec.Name, 0); Grid.SetColumn(sec.Name, 1);
            grid.Children.Add(sec.Name);
            Grid.SetRow(sec.Dated, 0); Grid.SetColumn(sec.Dated, 2);
            sec.Dated.Margin = new Thickness(8, 0, 0, 0);
            grid.Children.Add(sec.Dated);

            var pathLabel = new TextBlock { Text = $"Section {num} Path", Style = (Style)FindResource("FieldLabel") };
            Grid.SetRow(pathLabel, 1); Grid.SetColumn(pathLabel, 0);
            grid.Children.Add(pathLabel);
            Grid.SetRow(sec.Path, 1); Grid.SetColumn(sec.Path, 1);
            Grid.SetColumnSpan(sec.Path, 2);
            grid.Children.Add(sec.Path);

            SectionsPanel.Children.Add(grid);
        }
    }

    // ── Script Toggles ──

    private void SelectAllScripts_Click(object sender, RoutedEventArgs e)
    {
        foreach (var cb in _scriptChecks.Values) cb.IsChecked = true;
    }

    private void DeselectAllScripts_Click(object sender, RoutedEventArgs e)
    {
        foreach (var cb in _scriptChecks.Values) cb.IsChecked = false;
    }

    // ── Cache Generation ──

    private string GenerateCache()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# GLFTPD Unattended Installation Configuration");
        sb.AppendLine("# Generated by GlDrive Installer");
        sb.AppendLine();

        void Line(string key, string val, string comment) =>
            sb.AppendLine($"{key}=\"{val}\" # {comment}");

        Line("sitename", Sitename.Text, "Name of the site");
        Line("port", FtpPort.Text, "Port for the FTP");
        Line("device", Device.Text, "Device to use for /site");
        sb.AppendLine();

        sb.AppendLine("# Router Configuration");
        Line("router", BehindRouter.IsChecked == true ? "y" : "n", "Behind a router?");
        Line("pasv_addr", PasvAddr.Text, "Passive address");
        Line("pasv_ports", PasvPorts.Text, "Passive port range");
        sb.AppendLine();

        sb.AppendLine("# Channel Configuration");
        Line("channelnr", ChannelNr.Text, "Number of channels");
        Line("channame1", ChanName1.Text, "Channel 1");
        Line("channame2", ChanName2.Text, "Channel 2");
        Line("chanpass1", ChanPass1.Password, "Channel 1 password");
        Line("chanpass2", ChanPass2.Password, "Channel 2 password");
        Line("announcechannels", AnnounceChannels.Text, "Announce channels");
        Line("channelops", ChannelOps.Text, "Ops channel");
        sb.AppendLine();

        sb.AppendLine("# IRC Configuration");
        Line("ircnickname", IrcNickname.Text, "Bot owner IRC nick");

        // Build ircserver line: "host +port password" format
        var ircServerStr = IrcServer.Text.Trim();
        var ircPortStr = IrcPort.Text.Trim();
        if (IrcSsl.Text.Trim() == "1" && !ircPortStr.StartsWith('+'))
            ircPortStr = "+" + ircPortStr;
        var ircPassStr = IrcPass.Password;
        var fullIrcServer = $"{ircServerStr} {ircPortStr}";
        if (!string.IsNullOrEmpty(ircPassStr))
            fullIrcServer += $" {ircPassStr}";
        Line("ircserver", fullIrcServer, "IRC server (+ prefix = SSL)");

        Line("ircport", IrcPort.Text, "IRC port");
        Line("ircpass", IrcPass.Password, "IRC password");
        Line("ircssl", IrcSsl.Text, "IRC SSL");
        Line("ircident", IrcIdent.Text, "IRC ident");
        Line("ircrealname", IrcRealname.Text, "IRC realname");
        sb.AppendLine();

        sb.AppendLine("# Section Configuration");
        Line("sections", _sections.Count.ToString(), "Number of sections");
        for (int i = 0; i < _sections.Count; i++)
        {
            var sec = _sections[i];
            var n = i + 1;
            Line($"section{n}", sec.Name.Text, $"Section {n} name");
            Line($"section{n}dated", sec.Dated.IsChecked == true ? "y" : "n", "Dated?");
        }
        for (int i = 0; i < _sections.Count; i++)
        {
            var sec = _sections[i];
            Line($"sectionpath{i + 1}", sec.Path.Text, $"Path for section {i + 1}");
        }
        sb.AppendLine();

        sb.AppendLine("# Optional Scripts");
        foreach (var (key, cb) in _scriptChecks)
        {
            var cacheKey = key;
            var val = cb.IsChecked == true ? "y" : "n";
            Line(cacheKey, val, $"Install {cb.Content}");
        }
        Line("psxcimdbchan", PsxcImdbChan.Text, "PSXC-IMDB trigger channel");
        sb.AppendLine();

        sb.AppendLine("# Administrator Account");
        Line("username", AdminUsername.Text, "Admin username");
        Line("password", AdminPassword.Password, "Admin password");
        Line("ip", AdminIp.Text, "Admin IP mask");

        return sb.ToString();
    }

    // ── Validation ──

    private List<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Sitename.Text))
            errors.Add("Site name is required");
        else if (Sitename.Text.Contains(' '))
            errors.Add("Site name must not contain spaces");

        if (!int.TryParse(FtpPort.Text, out var port) || port < 1 || port > 65535)
            errors.Add("FTP port must be 1-65535");

        for (int i = 0; i < _sections.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(_sections[i].Name.Text))
                errors.Add($"Section {i + 1} name is empty");
        }

        return errors;
    }

    // ── Installation ──

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_ssh == null || !_ssh.IsConnected)
        {
            ShowError("Connect via SSH first");
            return;
        }
        if (_installing) return;

        var errors = Validate();
        if (errors.Count > 0)
        {
            ShowError("Fix the following:\n\n" + string.Join("\n", errors.Select(e => $"- {e}")));
            return;
        }

        var host = SshHost.Text;
        if (MessageBox.Show($"Install glFTPd on {host}?\n\nThis will modify the remote server.",
                "Confirm Installation", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _installing = true;
        InstallButton.IsEnabled = false;
        ProgressBar.Value = 0;
        _stopwatch = Stopwatch.StartNew();
        _elapsedTimer.Start();
        MainTabs.SelectedItem = LogTab;

        const string remotePath = "/tmp/glftpd-installer";
        const int totalSteps = 13;
        var stageStep = 4;
        var installOk = false;

        try
        {
            // Step 1: Create remote dir
            UpdateProgress(1, totalSteps, "CREATING DIR");
            await Task.Run(() =>
            {
                var cmd = _ssh!.RunCommand($"mkdir -p '{remotePath.Replace("'", "'\\''")}'");
                if (cmd.ExitStatus != 0) throw new Exception($"mkdir failed: {cmd.Error}");
            });
            AppendLog("Created installation directory");

            // Step 2: Transfer install.sh
            UpdateProgress(2, totalSteps, "TRANSFERRING");
            var installShPath = Path.Combine(InstallerRepoPath, "install.sh");
            if (!File.Exists(installShPath))
            {
                AppendLog($"install.sh not found at {installShPath}", "error");
                throw new FileNotFoundException("install.sh not found. Place glftpd-installer repo in Documents.");
            }
            await Task.Run(() => _sftp!.UploadFile(File.OpenRead(installShPath), $"{remotePath}/install.sh", true));
            AppendLog("Transferred install.sh");

            // Step 2b: Transfer packages directory if it exists
            var packagesPath = Path.Combine(InstallerRepoPath, "packages");
            if (Directory.Exists(packagesPath))
            {
                AppendLog("Transferring packages...");
                await Task.Run(() => UploadDirectory(packagesPath, $"{remotePath}/packages"));
                AppendLog("Transferred packages directory");
            }

            // Step 3: Transfer config
            UpdateProgress(3, totalSteps, "TRANSFERRING CONFIG");
            var cacheContent = GenerateCache();
            await Task.Run(() =>
            {
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(cacheContent));
                _sftp!.UploadFile(stream, $"{remotePath}/install.cache", true);
            });
            AppendLog("Transferred install.cache");

            // Step 4: chmod
            UpdateProgress(4, totalSteps, "SETTING UP");
            await Task.Run(() =>
            {
                var cmd = _ssh!.RunCommand($"chmod +x '{remotePath.Replace("'", "'\\''")}/install.sh'");
                if (cmd.ExitStatus != 0) throw new Exception($"chmod failed: {cmd.Error}");
            });
            AppendLog("Made install.sh executable");

            // Step 5+: Execute install.sh with streaming output
            UpdateProgress(stageStep, totalSteps, "INSTALLING");
            await Task.Run(() =>
            {
                using var shellCmd = _ssh!.CreateCommand($"sudo -S '{remotePath.Replace("'", "'\\''")}/install.sh'");
                shellCmd.CommandTimeout = TimeSpan.FromMinutes(30);

                var asyncResult = shellCmd.BeginExecute();

                // Send password for sudo
                using (var inputStream = shellCmd.CreateInputStream())
                {
                    var pwBytes = Encoding.UTF8.GetBytes(SshPassword.Password + "\n");
                    inputStream.Write(pwBytes, 0, pwBytes.Length);
                    inputStream.Flush();
                }

                // Stream stdout
                using var reader = new StreamReader(shellCmd.OutputStream, Encoding.UTF8);
                var buf = new char[4096];
                var lineBuf = new StringBuilder();

                while (!asyncResult.IsCompleted || !reader.EndOfStream)
                {
                    var read = reader.Read(buf, 0, buf.Length);
                    if (read > 0)
                    {
                        lineBuf.Append(buf, 0, read);
                        string current = lineBuf.ToString();
                        int newlineIdx;
                        while ((newlineIdx = current.IndexOf('\n')) >= 0)
                        {
                            var line = current[..newlineIdx].TrimEnd('\r');
                            current = current[(newlineIdx + 1)..];

                            Dispatcher.Invoke(() => AppendLog(line));

                            var stage = DetectStage(line);
                            if (stage != null)
                            {
                                stageStep++;
                                var step = Math.Min(stageStep, totalSteps - 1);
                                Dispatcher.Invoke(() => UpdateProgress(step, totalSteps, stage));
                            }
                        }
                        lineBuf.Clear();
                        lineBuf.Append(current);
                    }
                    else
                    {
                        Thread.Sleep(50);
                    }
                }

                shellCmd.EndExecute(asyncResult);

                // Remaining buffer
                if (lineBuf.Length > 0)
                    Dispatcher.Invoke(() => AppendLog(lineBuf.ToString()));

                var stderr = shellCmd.Error;
                if (!string.IsNullOrWhiteSpace(stderr))
                    Dispatcher.Invoke(() => AppendLog($"Errors:\n{stderr}", "error"));

                if (shellCmd.ExitStatus != 0)
                    throw new Exception($"install.sh exited with code {shellCmd.ExitStatus}");
            });

            installOk = true;
            UpdateProgress(totalSteps, totalSteps, "COMPLETE");
            await Task.Run(() => _ssh!.RunCommand($"rm -rf '{remotePath.Replace("'", "'\\''")}'"));
            AppendLog("Installation completed successfully!", "success");
            MessageBox.Show("Installation completed!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppendLog($"Error: {ex.Message}", "error");
            if (!installOk)
                AppendLog($"Remote files kept at {remotePath} for debugging", "warning");
            MessageBox.Show($"Installation failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _installing = false;
            InstallButton.IsEnabled = true;
            _elapsedTimer.Stop();
            _stopwatch?.Stop();
        }
    }

    // ── Stage detection (matches install.sh output) ──

    private static readonly (string Marker, string Label)[] InstallStages =
    [
        ("Ensuring that all required system packages", "PACKAGES"),
        ("Downloading all required script packages", "DOWNLOADING"),
        ("Server configuration", "SERVER CONFIG"),
        ("Installing: glftpd", "GLFTPD"),
        ("Installing: eggdrop", "EGGDROP"),
        ("Installing: pzs-ng", "PZS-NG"),
        ("FTP user configuration", "USER SETUP"),
        ("If you are planning to uninstall", "FINISHING"),
    ];

    private static string? DetectStage(string line)
    {
        foreach (var (marker, label) in InstallStages)
        {
            if (line.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return label;
        }
        return null;
    }

    // ── SFTP directory upload ──

    private void UploadDirectory(string localDir, string remoteDir)
    {
        _sftp!.CreateDirectory(remoteDir);

        foreach (var file in Directory.GetFiles(localDir))
        {
            using var stream = File.OpenRead(file);
            _sftp.UploadFile(stream, $"{remoteDir}/{Path.GetFileName(file)}", true);
        }

        foreach (var dir in Directory.GetDirectories(localDir))
        {
            UploadDirectory(dir, $"{remoteDir}/{Path.GetFileName(dir)}");
        }
    }

    // ── Progress & Logging ──

    private void UpdateProgress(int step, int total, string label)
    {
        Dispatcher.Invoke(() =>
        {
            ProgressBar.Value = (double)step / total * 100;
            ProgressLabel.Text = label;
            StepLabel.Text = $"{step}/{total}";
        });
    }

    private void AppendLog(string message, string tag = "")
    {
        Dispatcher.Invoke(() =>
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            LogOutput.AppendText($"[{timestamp}] {message}\n");
            LogOutput.ScrollToEnd();
        });
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => LogOutput.Clear();

    private void ExportLog_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            DefaultExt = ".log",
            Filter = "Log files (*.log)|*.log|Text files (*.txt)|*.txt|All files (*.*)|*.*",
            Title = "Export Installation Log"
        };
        if (dlg.ShowDialog() == true)
        {
            File.WriteAllText(dlg.FileName, LogOutput.Text);
            AppendLog($"Log exported to {dlg.FileName}", "success");
        }
    }

    // ── Profile Save/Load ──

    private void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            DefaultExt = ".json",
            Filter = "JSON profiles (*.json)|*.json|All files (*.*)|*.*",
            Title = "Save Configuration Profile"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var data = CollectProfile();
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(dlg.FileName, json);
            AppendLog($"Profile saved to {dlg.FileName}", "success");
        }
        catch (Exception ex)
        {
            ShowError($"Failed to save: {ex.Message}");
        }
    }

    private void LoadProfile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "JSON profiles (*.json)|*.json|All files (*.*)|*.*",
            Title = "Load Configuration Profile"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var json = File.ReadAllText(dlg.FileName);
            var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            if (data != null)
                ApplyProfile(data);
            AppendLog($"Profile loaded from {dlg.FileName}", "success");
        }
        catch (Exception ex)
        {
            ShowError($"Failed to load: {ex.Message}");
        }
    }

    private Dictionary<string, object> CollectProfile()
    {
        var d = new Dictionary<string, object>
        {
            ["ssh_host"] = SshHost.Text,
            ["ssh_port"] = SshPort.Text,
            ["ssh_username"] = SshUsername.Text,
            ["ssh_keyfile"] = SshKeyFile.Text,
            ["sitename"] = Sitename.Text,
            ["port"] = FtpPort.Text,
            ["device"] = Device.Text,
            ["router"] = BehindRouter.IsChecked == true ? "y" : "n",
            ["pasv_addr"] = PasvAddr.Text,
            ["pasv_ports"] = PasvPorts.Text,
            ["channelnr"] = ChannelNr.Text,
            ["channame1"] = ChanName1.Text,
            ["channame2"] = ChanName2.Text,
            ["announcechannels"] = AnnounceChannels.Text,
            ["channelops"] = ChannelOps.Text,
            ["ircnickname"] = IrcNickname.Text,
            ["ircserver"] = IrcServer.Text,
            ["ircport"] = IrcPort.Text,
            ["ircssl"] = IrcSsl.Text,
            ["ircident"] = IrcIdent.Text,
            ["ircrealname"] = IrcRealname.Text,
            ["psxcimdbchan"] = PsxcImdbChan.Text,
            ["admin_username"] = AdminUsername.Text,
            ["admin_ip"] = AdminIp.Text,
        };

        foreach (var (key, cb) in _scriptChecks)
            d[key] = cb.IsChecked == true ? "y" : "n";

        var sections = _sections.Select(s => new Dictionary<string, string>
        {
            ["name"] = s.Name.Text,
            ["path"] = s.Path.Text,
            ["dated"] = s.Dated.IsChecked == true ? "y" : "n",
        }).ToList();
        d["section_entries"] = sections;

        return d;
    }

    private void ApplyProfile(Dictionary<string, JsonElement> data)
    {
        string S(string key, string def = "") =>
            data.TryGetValue(key, out var v) ? v.GetString() ?? def : def;

        SshHost.Text = S("ssh_host");
        SshPort.Text = S("ssh_port", "22");
        SshUsername.Text = S("ssh_username");
        SshKeyFile.Text = S("ssh_keyfile");
        Sitename.Text = S("sitename");
        FtpPort.Text = S("port", "2010");
        Device.Text = S("device", "/dev/sda1");
        BehindRouter.IsChecked = S("router") == "y";
        PasvAddr.Text = S("pasv_addr");
        PasvPorts.Text = S("pasv_ports", "6000-7000");
        ChannelNr.Text = S("channelnr", "2");
        ChanName1.Text = S("channame1");
        ChanName2.Text = S("channame2");
        AnnounceChannels.Text = S("announcechannels");
        ChannelOps.Text = S("channelops");
        IrcNickname.Text = S("ircnickname");
        IrcServer.Text = S("ircserver");
        IrcPort.Text = S("ircport", "6667");
        IrcSsl.Text = S("ircssl", "0");
        IrcIdent.Text = S("ircident", "glftpd");
        IrcRealname.Text = S("ircrealname", "glFTPd Site Bot");
        PsxcImdbChan.Text = S("psxcimdbchan", "#main");
        AdminUsername.Text = S("admin_username", "admin");
        AdminIp.Text = S("admin_ip", "*@192.168.1.*");

        foreach (var (key, cb) in _scriptChecks)
            cb.IsChecked = S(key) == "y";

        if (data.TryGetValue("section_entries", out var secEl) && secEl.ValueKind == JsonValueKind.Array)
        {
            var secArr = secEl.EnumerateArray().ToList();
            SectionCount.Text = secArr.Count.ToString();
            RebuildSections(secArr.Count);
            for (int i = 0; i < secArr.Count && i < _sections.Count; i++)
            {
                var obj = secArr[i];
                _sections[i].Name.Text = obj.GetProperty("name").GetString() ?? "";
                _sections[i].Path.Text = obj.GetProperty("path").GetString() ?? "/site/";
                _sections[i].Dated.IsChecked = (obj.GetProperty("dated").GetString() ?? "n") == "y";
            }
        }
    }

    // ── Helpers ──

    private void Window_Closed(object sender, EventArgs e) => Disconnect();

    private static void ShowError(string msg) =>
        MessageBox.Show(msg, "Error", MessageBoxButton.OK, MessageBoxImage.Error);

    private class SectionEntry
    {
        public TextBox Name { get; init; } = null!;
        public TextBox Path { get; init; } = null!;
        public CheckBox Dated { get; init; } = null!;
    }
}
