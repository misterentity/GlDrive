using System.Diagnostics;
using System.Windows;
using GlDrive.Config;
using GlDrive.Irc;
using GlDrive.Services;
using GlDrive.Services.Automation;
using GlDrive.Services.Recovery;
using Microsoft.Win32;

namespace GlDrive.UI;

public partial class RecoveryCenterWindow : Window
{
    private readonly AppConfig _config;
    private readonly bool _offline;
    private readonly Func<string, FishKeyStore?>? _liveKeys;
    private readonly PortableBackupService _backups;
    private bool _busy;
    private bool _closed;
    private string? _rollbackGeneration;

    public RecoveryCenterWindow(AppConfig config, bool offline = false, Func<string, FishKeyStore?>? liveKeys = null)
    {
        InitializeComponent();
        _config = config; _offline = offline; _liveKeys = liveKeys;
        _backups = new(config, new ConfigurationAutomation(config), () => _offline,
            liveKeys == null ? null : id => liveKeys(id) is { } store
                ? store.PersistenceHealthy ? store.GetAllKeys() : throw new InvalidOperationException("FiSH storage requires recovery")
                : PortableBackupService.ReadFishFile(id));
        ModeText.Text = offline
            ? "Offline recovery: no engine services are running. Import validates settings, preserves current secrets unless replacement is selected, and requires a normal restart."
            : "Inventory and export are available while running. Import requires a restart into offline recovery so it cannot race with active connections.";
        ImportButton.IsEnabled = ReplaceSecrets.IsEnabled = offline;
        OfflineButton.Content = offline ? "Restart normally" : "Restart into offline recovery";
        Loaded += async (_, _) => await Task.WhenAll(RefreshInventory(), RefreshRollback());
        Closed += (_, _) => _closed = true;
        Closing += (_, e) => { if (_busy) { e.Cancel = true; StatusText.Text = "Wait for the current backup operation to finish."; } };
    }

    private async Task RefreshInventory()
    {
        try
        {
            InventoryGrid.ItemsSource = await Task.Run(() => RecoveryInventory.Read(_config, ConfigManager.AppDataPath, _liveKeys));
            InventoryGrid.Items.Refresh();
        }
        catch { StatusText.Text = "Inventory is unavailable. The original profile is unchanged; recovery controls remain available."; }
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshInventory();

    private async Task RefreshRollback()
    {
        try
        {
            var status = await Task.Run(() => UpdateProbation.GetStatus());
            if (_closed) return;
            _rollbackGeneration = status.RollbackAvailable ? status.Generation : null;
            RollbackButton.IsEnabled = !_busy && _offline && _rollbackGeneration != null;
            RollbackText.Text = status.Message + (!_offline && status.RollbackAvailable
                ? " Restart into offline recovery to roll back while the engine and updater are stopped." : "");
        }
        catch { if (!_closed) RollbackText.Text = "Rollback evidence is unavailable. Retained build files have not been removed."; }
    }

    private async void Rollback_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !_offline || _rollbackGeneration == null) return;
        if (MessageBox.Show(this, "Restore the verified previous local build and block automatic reinstallation of this version?\n\nWindows will request administrator permission. New optional settings are preserved in a restricted recovery copy so the previous build can read the profile.",
            "Roll back GlDrive", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        Busy(true);
        try
        {
            var generation = _rollbackGeneration;
            var result = await Task.Run(() => UpdateChecker.TryLaunchRollback(generation));
            StatusText.Text = result.Message;
            if (result.Started)
            {
                Busy(false);
                App.MarkExiting("local rollback");
                Application.Current.Shutdown();
            }
        }
        catch { StatusText.Text = "Rollback could not start; this recovery session remains open."; }
        finally { Busy(false); }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (!ValidateExportPassphrase()) return;
        var picker = new SaveFileDialog { Filter = "Encrypted GlDrive backup (*.gldbackup)|*.gldbackup", FileName = "GlDrive-backup.gldbackup" };
        if (picker.ShowDialog(this) != true) return;
        var passphrase = PassphraseBox.Password; PassphraseBox.Clear(); ConfirmPassphraseBox.Clear();
        Busy(true);
        try
        {
            var summary = await _backups.ExportAsync(picker.FileName, passphrase);
            StatusText.Text = $"Backup verified: {summary.Servers} server settings, {summary.FishKeys} FiSH keys, {summary.Credentials} credentials; {summary.SkippedFishKeys} invalid FiSH entries skipped. Keep your passphrase separately.";
        }
        catch { StatusText.Text = "Backup failed. Use a new filename and a passphrase of at least 12 characters; verify credential access and available disk space."; }
        finally { Busy(false); }
    }

    internal bool ValidateExportPassphrase()
    {
        if (PassphraseBox.Password.Length >= 12 && string.Equals(PassphraseBox.Password, ConfirmPassphraseBox.Password, StringComparison.Ordinal)) return true;
        StatusText.Text = "Enter a passphrase of at least 12 characters and repeat it exactly in the confirmation field before exporting.";
        return false;
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !_offline) return;
        var picker = new OpenFileDialog { Filter = "Encrypted GlDrive backup (*.gldbackup)|*.gldbackup" };
        if (picker.ShowDialog(this) != true) return;
        var passphrase = PassphraseBox.Password; PassphraseBox.Clear();
        var replace = ReplaceSecrets.IsChecked == true;
        Busy(true);
        try
        {
            var summary = await _backups.InspectAsync(picker.FileName, passphrase);
            var recovery = await Task.Run(() => RecoveryConfigurationFactory.Create(_config, _offline, replace));
            if (recovery.NeedsExplicitRepair && !replace)
            {
                StatusText.Text = "Current settings are unreadable, so their secret values cannot be compared. Select explicit replacement to repair from this backup; the damaged source will be preserved before saving.";
                return;
            }
            if (MessageBox.Show(this, $"Import settings from {summary.CreatedUtc:u} for {summary.Servers} server(s)?\n\n" +
                    (replace ? "You selected replacement of existing secrets, including newer local values." : "Existing local secrets will be preserved.") +
                    "\n\nA normal restart is required afterward.", "Import validated backup", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            var importer = new PortableBackupService(_config, recovery.Configuration, () => _offline);
            var result = await importer.ImportAsync(picker.FileName, passphrase, replace, expectedDigest: summary.Digest);
            StatusText.Text = $"Settings imported. Secrets: {result.ImportedSecrets} imported, {result.PreservedSecrets} preserved, {result.FailedSecrets} failed. Restart normally when ready.";
            await RefreshInventory();
        }
        catch { StatusText.Text = "Import did not complete. Check the passphrase, backup validity and credential access. Review local settings before restarting."; }
        finally { Busy(false); }
    }

    private void Busy(bool busy)
    {
        _busy = busy;
        ExportButton.IsEnabled = OfflineButton.IsEnabled = !busy;
        ImportButton.IsEnabled = ReplaceSecrets.IsEnabled = !busy && _offline;
        RollbackButton.IsEnabled = !busy && _offline && _rollbackGeneration != null;
    }

    private void Offline_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--restart-after"); start.ArgumentList.Add(Environment.ProcessId.ToString());
        if (!_offline) start.ArgumentList.Add("--recovery");
        try
        {
            Process.Start(start);
            App.MarkExiting("local recovery restart");
            Application.Current.Shutdown();
        }
        catch { StatusText.Text = "Could not start recovery restart. Current session is still running."; }
    }
}
