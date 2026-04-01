using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Xml.Linq;
using GlDrive.Config;
using GlDrive.Logging;
using GlDrive.Services;
using GlDrive.Tls;
using Serilog;

namespace GlDrive.UI;

public partial class SettingsWindow : Window
{
    private readonly AppConfig _config;
    private readonly ServerManager _serverManager;
    private readonly SettingsViewModel _vm;
    private readonly ObservableCollection<ServerListItem> _serverItems = new();
    private readonly ObservableCollection<CategoryPathItem> _categoryPaths = new();

    public SettingsWindow(AppConfig config, ServerManager serverManager)
    {
        InitializeComponent();
        _config = config;
        _serverManager = serverManager;
        _vm = new SettingsViewModel(config);
        DataContext = _vm;

        RefreshServerList();
        ServerGrid.ItemsSource = _serverItems;

        // Load category paths
        foreach (var kvp in config.Downloads.CategoryPaths)
            _categoryPaths.Add(new CategoryPathItem { Category = kvp.Key, Path = kvp.Value });
        CategoryPathGrid.ItemsSource = _categoryPaths;

        // Global skiplist
        GlobalSkiplistGrid.ItemsSource = _vm.GlobalSkiplist;
    }

    private void RefreshServerList()
    {
        _serverItems.Clear();
        foreach (var server in _config.Servers)
        {
            _serverItems.Add(new ServerListItem
            {
                Id = server.Id,
                Enabled = server.Enabled,
                Name = server.Name,
                Host = $"{server.Connection.Host}:{server.Connection.Port}",
                DriveLetter = $"{server.Mount.DriveLetter}:"
            });
        }
    }

    private void AddServer_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ServerEditDialog(serverManager: _serverManager) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _config.Servers.Add(dialog.Result);
            RefreshServerList();
        }
    }

    private void EditServer_Click(object sender, RoutedEventArgs e)
    {
        if (ServerGrid.SelectedItem is not ServerListItem selected) return;

        var serverConfig = _config.Servers.FirstOrDefault(s => s.Id == selected.Id);
        if (serverConfig == null) return;

        var dialog = new ServerEditDialog(serverConfig, _serverManager) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            RefreshServerList();
        }
    }

    private async void RemoveServer_Click(object sender, RoutedEventArgs e)
    {
        var selected = ServerGrid.SelectedItems.Cast<ServerListItem>().ToList();
        if (selected.Count == 0) return;

        var msg = selected.Count == 1
            ? $"Remove server \"{selected[0].Name}\"?\nThis will unmount the drive and delete its configuration."
            : $"Remove {selected.Count} servers?\n\n{string.Join("\n", selected.Select(s => $"  • {s.Name}"))}\n\nThis will unmount all drives and delete their configurations.";

        var result = MessageBox.Show(msg, $"Remove {selected.Count} Server(s)",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        foreach (var server in selected)
        {
            await _serverManager.UnmountServerAsync(server.Id);
            _config.Servers.RemoveAll(s => s.Id == server.Id);
        }
        RefreshServerList();
    }

    private void EnableServer_Click(object sender, RoutedEventArgs e)
    {
        var selected = ServerGrid.SelectedItems.Cast<ServerListItem>().ToList();
        if (selected.Count == 0) return;

        foreach (var item in selected)
        {
            var server = _config.Servers.FirstOrDefault(s => s.Id == item.Id);
            if (server != null) server.Enabled = true;
        }
        RefreshServerList();
    }

    private void DisableServer_Click(object sender, RoutedEventArgs e)
    {
        var selected = ServerGrid.SelectedItems.Cast<ServerListItem>().ToList();
        if (selected.Count == 0) return;

        foreach (var item in selected)
        {
            var server = _config.Servers.FirstOrDefault(s => s.Id == item.Id);
            if (server != null) server.Enabled = false;
        }
        RefreshServerList();
    }

    private void ImportSites_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import Sites from FTPRush or FlashFXP",
            Filter = "Site files (*.json, *.xml, *.ftp, *.dat)|*.json;*.xml;*.ftp;*.dat|FTPRush (*.json, *.xml)|*.json;*.xml|FlashFXP (*.ftp, *.dat)|*.ftp;*.dat|All files|*.*"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            var file = dialog.FileName;
            var name = Path.GetFileName(file);
            var imported = SiteImporter.ImportAuto(file);

            if (imported.Count == 0)
            {
                MessageBox.Show("No sites found in the selected file.", "Import", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Skip duplicates (same host:port:user)
            var added = 0;
            foreach (var server in imported)
            {
                var exists = _config.Servers.Any(s =>
                    s.Connection.Host.Equals(server.Connection.Host, StringComparison.OrdinalIgnoreCase) &&
                    s.Connection.Port == server.Connection.Port &&
                    s.Connection.Username.Equals(server.Connection.Username, StringComparison.OrdinalIgnoreCase));
                if (exists) continue;

                _config.Servers.Add(server);
                added++;
            }

            RefreshServerList();

            var skipped = imported.Count - added;
            var msg = $"Imported {added} server(s)";
            if (skipped > 0) msg += $" ({skipped} duplicates skipped)";
            // FlashFXP .ftp and FTPRush .json exports include passwords (auto-saved to Credential Manager)
            var hasPasswords = file.EndsWith(".ftp", StringComparison.OrdinalIgnoreCase) ||
                               file.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
            if (!hasPasswords)
                msg += ".\n\nPasswords cannot be imported — please edit each server to set the password.";
            MessageBox.Show(msg, "Import Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            Log.Information("Imported {Added} sites from {File} ({Skipped} duplicates skipped)", added, name, skipped);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Site import failed");
            MessageBox.Show($"Import failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ClearCerts_Click(object sender, RoutedEventArgs e)
    {
        var certMgr = new CertificateManager();
        certMgr.ClearTrustedCertificates();
        _vm.RefreshCertsInfo();
        MessageBox.Show("Trusted certificates cleared.", "GlDrive", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void BrowseDownloadPath_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Download Folder",
            InitialDirectory = _vm.DownloadLocalPath
        };

        if (dialog.ShowDialog() == true)
            _vm.DownloadLocalPath = dialog.FolderName;
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        var logPath = Path.Combine(ConfigManager.AppDataPath, "logs");
        Directory.CreateDirectory(logPath);
        Process.Start("explorer.exe", logPath);
    }

    private void AddCategoryPath_Click(object sender, RoutedEventArgs e)
    {
        _categoryPaths.Add(new CategoryPathItem { Category = "", Path = "" });
    }

    private void BrowseCategoryPath_Click(object sender, RoutedEventArgs e)
    {
        if (CategoryPathGrid.SelectedItem is not CategoryPathItem selected) return;

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = $"Select folder for \"{selected.Category}\"",
            InitialDirectory = string.IsNullOrWhiteSpace(selected.Path) ? _vm.DownloadLocalPath : selected.Path
        };

        if (dialog.ShowDialog() == true)
        {
            selected.Path = dialog.FolderName;
            CategoryPathGrid.Items.Refresh();
        }
    }

    private void RemoveCategoryPath_Click(object sender, RoutedEventArgs e)
    {
        if (CategoryPathGrid.SelectedItem is CategoryPathItem selected)
            _categoryPaths.Remove(selected);
    }

    private void AddGlobalSkiplistRule_Click(object sender, RoutedEventArgs e)
    {
        _vm.GlobalSkiplist.Add(new SkiplistRule());
    }

    private void RemoveGlobalSkiplistRule_Click(object sender, RoutedEventArgs e)
    {
        if (GlobalSkiplistGrid.SelectedItem is SkiplistRule rule)
            _vm.GlobalSkiplist.Remove(rule);
    }

    private void ImportGlobalSkiplist_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import Skiplist Rules",
            Filter = "Text files (*.txt)|*.txt|All files|*.*"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var rules = SiteImporter.ImportSkiplist(dialog.FileName);
            foreach (var rule in rules)
                _vm.GlobalSkiplist.Add(rule);
            MessageBox.Show($"Imported {rules.Count} skiplist rule(s).", "Import", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Import failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _vm.ApplyTo(_config);

        // Save category paths
        _config.Downloads.CategoryPaths.Clear();
        foreach (var item in _categoryPaths)
        {
            if (!string.IsNullOrWhiteSpace(item.Category) && !string.IsNullOrWhiteSpace(item.Path))
                _config.Downloads.CategoryPaths[item.Category.Trim()] = item.Path.Trim();
        }

        ConfigManager.Save(_config);

        // Apply log level change at runtime
        SerilogSetup.SetLevel(_config.Logging.Level);

        // Apply theme change at runtime
        ThemeManager.ApplyTheme(_config.Downloads.Theme);

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

public class ServerListItem
{
    public string Id { get; set; } = "";
    public bool Enabled { get; set; }
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public string DriveLetter { get; set; } = "";
}

public class CategoryPathItem
{
    public string Category { get; set; } = "";
    public string Path { get; set; } = "";
}
