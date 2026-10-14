using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Globalization;
using GlDrive.Config;
using GlDrive.Services;

namespace GlDrive.UI;

public sealed class McpSettingsViewModel : INotifyPropertyChanged
{
    private readonly AppConfig _config;
    private readonly bool _readLocalState;
    private bool _enabled;
    private string _port;
    private bool _isBusy;
    private string _selectedClient = "Claude Code";
    private string _status;
    private string _feedback = "";

    public McpSettingsViewModel(AppConfig config, bool readLocalState = true)
    {
        _config = config;
        _readLocalState = readLocalState;
        _enabled = config.ControlApi.Enabled;
        _port = config.ControlApi.Port.ToString(CultureInfo.InvariantCulture);
        _status = _enabled ? "App connection has not been checked yet." : "MCP is off. Enable it and apply your settings to connect.";
        Setup = McpSetupService.GetSetup();
    }

    public McpSetupInfo Setup { get; private set; }
    public bool Enabled
    {
        get => _enabled;
        set { if (_enabled == value) return; _enabled = value; Feedback = ""; Changed(); Changed(nameof(PendingChangesText)); }
    }
    public string Port
    {
        get => _port;
        set { if (_port == value) return; _port = value; Feedback = ""; Changed(); Changed(nameof(PendingChangesText)); }
    }
    public bool IsBusy
    {
        get => _isBusy;
        private set { _isBusy = value; Changed(); Changed(nameof(IsIdle)); }
    }
    public bool IsIdle => !IsBusy;
    public string Status { get => _status; private set { _status = value; Changed(); } }
    public string Feedback { get => _feedback; set { _feedback = value; Changed(); } }
    public string PendingChangesText => Enabled != _config.ControlApi.Enabled ||
        !TryGetPort(out var port, out _) || port != _config.ControlApi.Port
        ? "Unsaved MCP changes. Apply below, or use Save at the bottom of Settings."
        : "Connection checks use the applied settings.";

    public string[] Clients { get; } = ["Claude Code", "Codex", "Claude Desktop"];
    public string SelectedClient
    {
        get => _selectedClient;
        set
        {
            if (!Clients.Contains(value) || _selectedClient == value) return;
            _selectedClient = value;
            Feedback = "";
            Changed(); Changed(nameof(ClientInstructions)); Changed(nameof(ClientSnippet)); Changed(nameof(CopyLabel));
        }
    }
    public string ClientSnippet => SelectedClient switch
    {
        "Codex" => Setup.CodexCommand,
        "Claude Desktop" => Setup.ClaudeDesktopJson,
        _ => Setup.ClaudeCodeCommand
    };
    public string CopyLabel => SelectedClient == "Claude Desktop" ? "Copy JSON" : "Copy command";
    public string ClientInstructions => SelectedClient switch
    {
        "Claude Desktop" => "Merge this fmountr entry into %APPDATA%\\Claude\\claude_desktop_config.json, keeping your other servers. Fully restart Claude Desktop afterward.",
        "Codex" => "Run this command once in PowerShell, then restart or reconnect Codex. Use /mcp to check the server. If fmountr already exists, check its path with codex mcp get fmountr.",
        _ => "Run this command once in PowerShell, then use /mcp in Claude Code to reconnect. If fmountr already exists, check its path with claude mcp get fmountr."
    };

    public bool TryGetPort(out int port, out string error)
    {
        if (int.TryParse(Port, NumberStyles.Integer, CultureInfo.InvariantCulture, out port) && port is >= 1024 and <= 65535)
        {
            error = "";
            return true;
        }
        error = "Enter a local port between 1024 and 65535 (default 8756).";
        return false;
    }

    public void ApplyTo(AppConfig config)
    {
        if (!TryGetPort(out var port, out var error)) throw new ArgumentException(error);
        config.ControlApi.Enabled = Enabled;
        config.ControlApi.Port = port;
        ControlApi.EnsureToken(config);
    }

    public async Task ApplyAsync(CancellationToken ct = default)
    {
        if (IsBusy) return;
        if (!TryGetPort(out var port, out var error)) { Status = error; return; }
        if (!_readLocalState || ConfigManager.ReadOnly) { Status = "MCP settings cannot be saved in preview mode."; return; }
        IsBusy = true;
        var saved = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            McpSettingsPersistence.Apply(_config, Enabled, port);
            saved = true;
            Changed(nameof(PendingChangesText));
            Feedback = "MCP settings saved.";
            if (System.Windows.Application.Current is App app) app.RefreshControlApi(_config);
            await ProbeCoreAsync(ct);
        }
        catch (OperationCanceledException)
        { Status = saved ? "Connection check cancelled. Saved settings remain applied." : "Apply cancelled before saving."; }
        catch (Exception ex)
        {
            Serilog.Log.Warning("MCP settings {Stage} failed ({ErrorType})", saved ? "activation" : "save", ex.GetType().Name);
            Status = saved ? "Settings were saved, but the app connection could not be started. Check the app log and retry."
                : "Could not save MCP settings. Check the app log and try again.";
        }
        finally { IsBusy = false; }
    }

    public async Task CheckAsync(CancellationToken ct = default)
    {
        if (IsBusy || !_readLocalState || ConfigManager.ReadOnly) return;
        IsBusy = true;
        try { await ProbeCoreAsync(ct); }
        catch (OperationCanceledException) { Status = "Connection check cancelled."; }
        catch (Exception) { Status = "Connection check failed. Check that GlDrive is running and try again."; }
        finally { IsBusy = false; }
    }

    private async Task ProbeCoreAsync(CancellationToken ct)
    {
        Setup = McpSetupService.GetSetup();
        Changed(nameof(Setup)); Changed(nameof(ClientSnippet)); Changed(nameof(PendingChangesText));
        Status = "Checking the applied app connection…";
        var result = await McpSetupService.ProbeAsync(_config.ControlApi, ct);
        Status = result.Message;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
