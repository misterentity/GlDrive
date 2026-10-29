using System.Text.Json;
using System.Windows;
using GlDrive.UI;

namespace GlDrive.Services.Automation;

public sealed class InstallerAutomation : IAutomationModule, IAutomationApprovalBinding
{
    private static object Schema(object properties, params string[] required) => new { type = "object", properties, required, additionalProperties = false };
    public IReadOnlyList<AutomationToolDefinition> Tools { get; } =
    [
        AutomationToolDefinition.Create("installer.state", "Inspect glFTPd SSH installer connection, progress, validation errors and local install.sh availability. Initializes its desktop window if needed.", Schema(new { })),
        AutomationToolDefinition.Create("installer.profile_get", "Read the installer's complete settings profile. Password fields are omitted unless includeSecrets=true explicitly requests them.", Schema(new { includeSecrets = new { type = "boolean" } })),
        AutomationToolDefinition.Create("installer.profile_set", "Patch installer profile fields from installer.profile_get. Sections use section_entries:[{name,path,dated:'y'|'n'}]. Secret fields are ssh_password, chanpass1, chanpass2, ircpass, admin_password. This edits the form; it does not install anything.", Schema(new { profile = new { type = "object" } }, "profile")),
        AutomationToolDefinition.Create("installer.connect", "Connect the installer over SSH/SFTP using its profile. A previously trusted host key is required, or pass its independently verified expectedHostKey SHA256 fingerprint. An unknown key is returned for verification without trusting it.", Schema(new { expectedHostKey = new { type = "string", minLength = 1, maxLength = 128 } })),
        AutomationToolDefinition.Create("installer.disconnect", "Disconnect the installer's SSH/SFTP session when idle.", Schema(new { })),
        AutomationToolDefinition.Create("installer.start", "Install glFTPd and selected scripts on the connected remote Linux server using Documents/glftpd-installer/install.sh. This changes the remote system via sudo. Requires a fresh /glftpd destination; inspect state/profile first. Returns a tracked job for long installations.", Schema(new { }), destructive: true),
        AutomationToolDefinition.Create("installer.cancel", "Request cancellation of a running installation, including its SSH command. Applied remote changes are not rolled back; child termination depends on SSH server signal support.", Schema(new { }), destructive: true),
        AutomationToolDefinition.Create("installer.logs", "Read installer log lines with pagination. Current profile secrets are redacted unless includeSecrets=true.", Schema(new { offset = new { type = "integer", minimum = 0 }, limit = new { type = "integer", minimum = 1, maximum = 2000 }, includeSecrets = new { type = "boolean" } }), true),
        AutomationToolDefinition.Create("installer.logs_clear", "Clear the desktop installer log.", Schema(new { }), destructive: true)
    ];

    public Task<object?> ExecuteAsync(string operation, JsonElement arguments, CancellationToken ct)
        => ExecuteCoreAsync(operation, arguments, ct, null, null);

    public async Task<IAutomationApprovedCall?> BindApprovalAsync(string operation, JsonElement arguments, CancellationToken ct)
    {
        if (operation != "installer.start") return null;
        var dispatcher = Application.Current?.Dispatcher ?? throw new InvalidOperationException("Desktop session is unavailable.");
        return await dispatcher.InvokeAsync(() =>
        {
            ct.ThrowIfCancellationRequested();
            var window = Application.Current.Windows.OfType<GlftpdInstallerWindow>().FirstOrDefault()
                ?? throw new InvalidOperationException("Connect the installer before requesting installation approval.");
            return (IAutomationApprovedCall)new BoundCall(this, arguments.Clone(), window, window.CaptureAutomationApproval());
        }, System.Windows.Threading.DispatcherPriority.Normal, ct);
    }

    private sealed class BoundCall(InstallerAutomation owner, JsonElement arguments, GlftpdInstallerWindow window, AutomationStateApproval approval) : IAutomationApprovedCall
    {
        public string Description => approval.Description;
        public Task<object?> ExecuteAsync(CancellationToken ct) => owner.ExecuteCoreAsync("installer.start", arguments, ct, window, approval);
    }

    private async Task<object?> ExecuteCoreAsync(string operation, JsonElement arguments, CancellationToken ct, GlftpdInstallerWindow? expectedWindow, AutomationStateApproval? approval)
    {
        var dispatcher = Application.Current?.Dispatcher ?? throw new InvalidOperationException("Desktop session is unavailable.");
        return await (await dispatcher.InvokeAsync(async () =>
        {
            var window = Application.Current.Windows.OfType<GlftpdInstallerWindow>().FirstOrDefault();
            if (expectedWindow != null && !ReferenceEquals(window, expectedWindow))
                throw new AutomationDeniedException("The installer window changed while approval was pending.");
            if (window == null)
            {
                window = new GlftpdInstallerWindow { ShowInTaskbar = false };
                window.Show(); window.Hide();
            }
            Action? validate = approval == null ? null : () => window.ValidateAutomationApproval(approval);
            return await window.AutomationExecute(operation, arguments, ct, validate);
        }, System.Windows.Threading.DispatcherPriority.Normal, ct));
    }
}
