using System.Windows;
using GlDrive.Config;
using GlDrive.Services.Automation;

namespace GlDrive.UI;

public partial class AutomationSecurityWindow : Window
{
    private readonly AppConfig _config;
    private readonly AutomationApprovalBroker? _broker;
    private readonly AutomationClientManagement _clients;
    private string? _issuedToken;
    private bool _saving;
    private sealed record ClientRow(string Id, string Name, string Scopes, bool AllowDestructive, DateTimeOffset? ExpiresUtc);

    public AutomationSecurityWindow(AppConfig config, AutomationApprovalBroker? broker = null, bool showApprovals = false)
    {
        InitializeComponent();
        _config = config; _broker = broker; _clients = new(config);
        if (!showApprovals) Tabs.SelectedIndex = 1;
        RequireApproval.IsChecked = config.ControlApi.Security.RequireDangerousApproval;
        RequireConfigurationApproval.IsChecked = config.ControlApi.Security.RequireConfigurationApproval;
        RequireLegacyApproval.IsChecked = config.ControlApi.Security.RequireLegacyApproval;
        RefreshClients(); RefreshRequests();
        if (broker != null) broker.Changed += BrokerChanged;
        Closed += (_, _) =>
        {
            if (broker != null) broker.Changed -= BrokerChanged;
            _issuedToken = null; IssuedToken.Clear();
        };
        Closing += (_, e) => { if (_saving) e.Cancel = true; };
    }

    private void BrokerChanged()
    {
        if (Dispatcher.HasShutdownStarted) return;
        using (ExecutionContext.IsFlowSuppressed() ? default : ExecutionContext.SuppressFlow())
            _ = Dispatcher.BeginInvoke(RefreshRequests);
    }
    internal void ShowApprovals() { Tabs.SelectedItem = ApprovalsTab; RefreshRequests(); Activate(); }
    private void RefreshRequests()
    {
        var id = (RequestsGrid.SelectedItem as AutomationApprovalRequest)?.Id;
        var rows = _broker?.Snapshot() ?? [];
        RequestsGrid.ItemsSource = rows;
        RequestsGrid.SelectedItem = rows.FirstOrDefault(r => r.Id == id);
    }
    private void RefreshClients()
    {
        var id = (ClientsGrid.SelectedItem as ClientRow)?.Id;
        var rows = _config.ControlApi.Security.Clients.Select(c => new ClientRow(c.Id, c.Name,
            string.Join(", ", c.Scopes), c.AllowDestructive, c.ExpiresUtc)).ToArray();
        ClientsGrid.ItemsSource = rows;
        ClientsGrid.SelectedItem = rows.FirstOrDefault(r => r.Id == id);
    }

    private void Approve_Click(object sender, RoutedEventArgs e) => Decide(true);
    private void Deny_Click(object sender, RoutedEventArgs e) => Decide(false);
    private void Decide(bool approve)
    {
        if (RequestsGrid.SelectedItem is not AutomationApprovalRequest request || _broker == null) return;
        StatusText.Text = _broker.Decide(request.Id, approve)
            ? approve ? "Approved this call once." : "Call denied."
            : "This request already ended or expired.";
        RefreshRequests();
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(ExpiryDays.Text, out var days) || days is < 1 or > 3650)
        { StatusText.Text = "Enter an expiry from 1 to 3650 days."; return; }
        var client = new AutomationClientConfig
        {
            Name = ClientNameBox.Text.Trim(),
            Scopes = ScopePreset.SelectedIndex switch
            {
                1 => ["Observe", "Operate"],
                2 => ["Observe", "Operate", "Configure", "Secrets", "RemoteAdmin", "LocalFiles"],
                _ => ["Observe"]
            },
            ToolFamilies = FamiliesBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            AllowedLocalRoots = RootsBox.Text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            AllowDestructive = AllowDestructive.IsChecked == true,
            ExpiresUtc = DateTimeOffset.UtcNow.AddDays(days)
        };
        await Save(() => _clients.Issue(client));
    }
    private async void Rotate_Click(object sender, RoutedEventArgs e)
    {
        if (ClientsGrid.SelectedItem is not ClientRow row) return;
        await Save(() => _clients.Rotate(row.Id));
    }
    private async void Revoke_Click(object sender, RoutedEventArgs e)
    {
        if (ClientsGrid.SelectedItem is not ClientRow row) return;
        await Save(() => { _clients.Revoke(row.Id); return null; });
    }
    private async void SaveApproval_Click(object sender, RoutedEventArgs e)
    {
        var enabled = RequireApproval.IsChecked == true;
        var configuration = RequireConfigurationApproval.IsChecked == true;
        var legacy = RequireLegacyApproval.IsChecked == true;
        await Save(() =>
        {
            _clients.Edit(s => { s.RequireDangerousApproval = enabled; s.RequireConfigurationApproval = configuration; s.RequireLegacyApproval = legacy; });
            return null;
        });
    }
    private async Task Save(Func<string?> save)
    {
        if (_saving) return;
        _saving = true;
        try
        {
            var token = await Task.Run(save);
            _issuedToken = token; IssuedToken.Password = token ?? "";
            StatusText.Text = token == null ? "Access settings saved." : "Scoped token saved. Copy the new token now; it will not be shown again.";
            RefreshClients();
        }
        catch { StatusText.Text = "Could not save access settings. Existing access remains unchanged. Check the fields and local settings file."; }
        finally { _saving = false; }
    }
    private void CopyToken_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_issuedToken)) return;
        try { Clipboard.SetDataObject(_issuedToken, true); StatusText.Text = "New token copied. Set it as GLDRIVE_CONTROL_TOKEN in that client's bridge environment."; }
        catch { StatusText.Text = "Clipboard unavailable. Rotate again when the clipboard is available."; }
    }
    private void RefreshLedger_Click(object sender, RoutedEventArgs e)
    {
        var clients = _config.ControlApi.Security.Clients.ToDictionary(client => AutomationCallLedger.ClientIdentity(client.Id), client => client.Name);
        clients[AutomationCallLedger.ClientIdentity("legacy")] = "Legacy owner";
        LedgerGrid.ItemsSource = App.AutomationLedger?.Snapshot(200).Select(row => new
        {
            row.Time, Client = clients.GetValueOrDefault(row.ClientId, row.ClientId), row.Tool,
            row.Outcome, row.DurationMilliseconds, row.CallId, row.JobId, Arguments = row.Arguments.GetRawText()
        }).ToArray();
    }
}
