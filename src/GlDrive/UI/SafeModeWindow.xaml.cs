using System.Diagnostics;
using System.Windows;
using GlDrive.Config;
using GlDrive.Services;
using GlDrive.Services.Recovery;

namespace GlDrive.UI;

public partial class SafeModeWindow : Window
{
    private readonly AppConfig _config;
    private string? _rollbackGeneration;
    private volatile bool _busy;
    private bool _closed;
    private volatile bool _recoveryDialogOpen;
    private volatile bool _restartPending;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly UpdateChecker _updater;

    internal SafeModeWindow(AppConfig config, CrashRecoveryStartup startup)
    {
        _config = config;
        _updater = new UpdateChecker
        {
            PreparationTimeout = TimeSpan.FromMinutes(10),
            AutoInstallEnabled = () => config.Recovery.AutoInstallUpdates,
            CanInstallNow = () => !_busy && !_recoveryDialogOpen && !_restartPending
        };
        InitializeComponent();
        ExplanationText.Text = $"Repeated crash restarts were detected in the same launch chain (threshold {startup.Settings.CrashLoopThreshold} within {startup.Settings.CrashLoopWindowMinutes} minutes). " +
            "Safe mode keeps the engine offline while you inspect diagnostics or recover. Signed update checks continue, and normal operation will retry automatically.";
        StatusText.Text = $"Normal operation will retry in {SafeModeRecoverySchedule.ResumeDelay(startup.Settings).TotalMinutes:0.#} minutes, after any active recovery or update completes.";
        _updater.RestartRequested += () => Dispatcher.BeginInvoke(() =>
        {
            App.MarkExiting("safe mode update");
            Busy(false);
            Application.Current.Shutdown();
        });
        Loaded += async (_, _) =>
        {
            _ = RefreshRollback();
            try
            {
                await SafeModeRecoverySchedule.RunAsync(startup.Settings,
                    () => _updater.StartPeriodicCheck(_lifetime.Token), TryResume, _lifetime.Token);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _lifetime.Cancel();
            _updater.Dispose();
            _lifetime.Dispose();
        };
        Closing += (_, e) => { if (_busy) e.Cancel = true; };
    }

    private async Task RefreshRollback()
    {
        try
        {
            var status = await Task.Run(() => UpdateProbation.GetStatus());
            if (_closed) return;
            _rollbackGeneration = status.RollbackAvailable ? status.Generation : null;
            RollbackButton.IsEnabled = _rollbackGeneration != null;
            RollbackText.Text = status.Message;
        }
        catch { if (!_closed) RollbackText.Text = "Rollback information is unavailable. Retained build files have not been removed."; }
    }

    private void Diagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (!_busy) new LogConsoleWindow(_config) { Owner = this }.Show();
    }

    private void Recovery_Click(object sender, RoutedEventArgs e)
    {
        // Keep the parent restart/rollback controls disabled during an import or export.
        // This is a user-opened offline dialog, never an automation approval wait.
        if (!_busy)
        {
            using var operation = _updater.TryBeginRecoveryOperation();
            if (operation == null) { StatusText.Text = "An update or restart is already in progress. Recovery is available after it completes."; return; }
            _recoveryDialogOpen = true;
            try { new RecoveryCenterWindow(_config, offline: true) { Owner = this }.ShowDialog(); }
            finally { _recoveryDialogOpen = false; }
        }
    }

    private async void Rollback_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _rollbackGeneration == null) return;
        using var operation = _updater.TryBeginRecoveryOperation();
        if (operation == null) { StatusText.Text = "An update or restart is already in progress. Rollback is available after it completes."; return; }
        if (MessageBox.Show(this, "Restore the verified previous local build and block automatic reinstallation of this version?\n\nWindows will request administrator permission. Profile data is kept as it is.",
            "Roll back GlDrive", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        Busy(true);
        try
        {
            var generation = _rollbackGeneration;
            var result = await Task.Run(() => UpdateChecker.TryLaunchRollback(generation));
            StatusText.Text = result.Message;
            if (result.Started)
            {
                App.MarkExiting("local rollback");
                Busy(false);
                Application.Current.Shutdown();
            }
        }
        catch { StatusText.Text = "Rollback could not start; GlDrive remains offline. No restart was requested."; }
        finally { Busy(false); }
    }

    private void Resume_Click(object sender, RoutedEventArgs e)
    {
        TryResume();
    }

    private bool TryResume()
    {
        if (_closed || _busy || _recoveryDialogOpen) return false;
        // Close admission before sampling an active installer, so a later periodic tick
        // cannot begin an install while normal restart is being handed off.
        _restartPending = true;
        if (!_updater.TryStopForRecoveryRestart()) return false;
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--restart-after"); start.ArgumentList.Add(Environment.ProcessId.ToString());
            if (Process.Start(start) == null) throw new InvalidOperationException();
            App.MarkExiting("safe mode resume");
            Application.Current.Shutdown();
            return true;
        }
        catch
        {
            _restartPending = false;
            _updater.RecoveryRestartFailed();
            StatusText.Text = "Normal restart could not start. Updates remain available and restart will be retried.";
        }
        return false;
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        App.MarkExiting("safe mode exit");
        Application.Current.Shutdown();
    }

    private void Busy(bool value)
    {
        _busy = value;
        ResumeButton.IsEnabled = ExitButton.IsEnabled = !value;
        RollbackButton.IsEnabled = !value && _rollbackGeneration != null;
    }
}
