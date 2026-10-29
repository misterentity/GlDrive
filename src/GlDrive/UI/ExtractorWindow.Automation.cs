using System.IO;
using System.Text.Json;
using GlDrive.Services.Automation;

namespace GlDrive.UI;

public partial class ExtractorWindow
{
    internal object AutomationState() => new
    {
        extracting = _extracting,
        savedWatchersDeferred = _watchResumeDeferred,
        archives = Archives.ToArray(),
        settings = new ExtractorSettings
        {
            WatchFolders = _watchFolders.ToList(), WatchEnabled = _watchEnabled,
            DeleteAfterExtract = ChkDeleteAfter.IsChecked == true, ScanSubfolders = ChkRecursive.IsChecked == true,
            CreateSubfolder = ChkCreateSubfolder.IsChecked == true, OutputMode = OutputMode.SelectedIndex,
            OverwriteMode = OverwriteMode.SelectedIndex, CustomOutputPath = CustomOutputPath.Text
        }
    };

    internal AutomationStateApproval CaptureAutomationApproval(string operation, JsonElement arguments)
    {
        Dispatcher.VerifyAccess();
        if (_extracting) throw new InvalidOperationException("Wait for extraction to finish before requesting approval.");
        var settings = operation == "extractor.settings"
            ? arguments.GetProperty("settings").Deserialize<ExtractorSettings>(_jsonOptions) ?? throw new ArgumentException("settings required")
            : new ExtractorSettings
            {
                WatchFolders = _watchFolders.ToList(), WatchEnabled = _watchEnabled,
                DeleteAfterExtract = ChkDeleteAfter.IsChecked == true, ScanSubfolders = ChkRecursive.IsChecked == true,
                CreateSubfolder = ChkCreateSubfolder.IsChecked == true, OutputMode = OutputMode.SelectedIndex,
                OverwriteMode = OverwriteMode.SelectedIndex, CustomOutputPath = CustomOutputPath.Text
            };
        var paths = Archives.Where(a => a.Status != "Done").Select(a => a.FilePath).ToArray();
        var description = (operation == "extractor.start" ? $"Extract {paths.Length} queued archives." : "Change extractor settings.") +
            $"\nDelete source archives after success: {settings.DeleteAfterExtract}. Overwrite mode: {settings.OverwriteMode} (0 overwrite, 1 skip, 2 rename)." +
            $"\nOutput mode: {settings.OutputMode} (0 beside archive, 1 archive subfolder, 2 custom). Custom path: {settings.CustomOutputPath}." +
            $"\nCreate subfolder: {settings.CreateSubfolder}. Watch enabled: {settings.WatchEnabled}. Recursive scan: {settings.ScanSubfolders}." +
            "\nWatch folders:\n" + string.Join("\n", (settings.WatchFolders ?? []).Take(10)) +
            "\nQueued archives:\n" + string.Join("\n", paths.Take(10));
        if (paths.Length > 10 || settings.WatchFolders?.Count > 10) description += "\nAdditional paths omitted; inspect the local extractor or deny.";
        var secrets = new[] { ArchivePassword.Password, arguments.TryGetProperty("password", out var password) ? password.GetString() ?? "" : "" };
        return new(AutomationApprovalState(), description, secrets, this);
    }

    internal void ValidateAutomationApproval(AutomationStateApproval approval)
    {
        Dispatcher.VerifyAccess();
        approval.Validate(AutomationApprovalState(), this);
    }

    private object AutomationApprovalState() => new
    {
        state = AutomationState(), password = ArchivePassword.Password
    };

    internal async Task<object> AutomationExecute(string action, JsonElement arguments, CancellationToken ct)
    {
        Dispatcher.VerifyAccess();
        ct.ThrowIfCancellationRequested();
        if (action == "extractor.state") return AutomationState();
        if (action == "extractor.cancel") { _cts?.Cancel(); return AutomationState(); }
        if (_extracting) throw new InvalidOperationException("Wait for extraction to finish or cancel it first");
        if (action == "extractor.add")
        {
            var paths = arguments.GetProperty("paths").EnumerateArray().Select(p => p.GetString()!).ToArray();
            if (paths.Any(p => !Path.IsPathFullyQualified(p) || (!File.Exists(p) && !Directory.Exists(p))))
                throw new ArgumentException("Each archive or directory must be an existing absolute path");
            AddPaths(paths);
        }
        else if (action == "extractor.remove")
        {
            var path = arguments.GetProperty("path").GetString();
            var item = Archives.FirstOrDefault(a => string.Equals(a.FilePath, path, StringComparison.OrdinalIgnoreCase))
                ?? throw new KeyNotFoundException("Archive not found");
            Archives.Remove(item);
        }
        else if (action == "extractor.clear") Archives.Clear();
        else if (action == "extractor.settings")
        {
            var settings = arguments.GetProperty("settings").Deserialize<ExtractorSettings>(new JsonSerializerOptions(_jsonOptions)
            { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow })
                ?? throw new ArgumentException("settings required");
            if (settings.OutputMode is < 0 or > 2 || settings.OverwriteMode is < 0 or > 2)
                throw new ArgumentException("outputMode and overwriteMode must be 0, 1 or 2");
            if (settings.WatchFolders == null || settings.WatchFolders.Any(p => !Path.IsPathFullyQualified(p) || !Directory.Exists(p)))
                throw new ArgumentException("Watch folders must exist and be absolute paths");
            if (settings.WatchEnabled && settings.WatchFolders.Count == 0) throw new ArgumentException("Watch folders required");
            if (settings.OutputMode == 2 && !Path.IsPathFullyQualified(settings.CustomOutputPath)) throw new ArgumentException("Custom output must be absolute");
            StopAllWatchers();
            _watchResumeDeferred = false;
            _watchEnabled = false;
            _initialized = false;
            try
            {
                _watchFolders.Clear(); _watchFolders.AddRange(settings.WatchFolders);
                WatchFoldersText.Text = string.Join("; ", _watchFolders);
                ChkDeleteAfter.IsChecked = settings.DeleteAfterExtract;
                ChkRecursive.IsChecked = settings.ScanSubfolders;
                ChkCreateSubfolder.IsChecked = settings.CreateSubfolder;
                OutputMode.SelectedIndex = settings.OutputMode;
                OverwriteMode.SelectedIndex = settings.OverwriteMode;
                CustomOutputPath.Text = settings.CustomOutputPath;
                WatchToggle.IsChecked = settings.WatchEnabled;
                WatchToggle.Content = "OFF";
            }
            finally { _initialized = true; }
            if (settings.WatchEnabled) WatchToggle_Click(this, new System.Windows.RoutedEventArgs());
            SaveSettings(throwOnError: true);
        }
        else if (action == "extractor.start")
        {
            if (arguments.TryGetProperty("password", out var password)) ArchivePassword.Password = password.GetString() ?? "";
            using var cancellation = ct.Register(() => Dispatcher.BeginInvoke(() => _cts?.Cancel()));
            try { await ExtractAllAsync(); }
            finally { ArchivePassword.Clear(); }
        }
        else throw new KeyNotFoundException("Unknown extractor action");
        return AutomationState();
    }
}
