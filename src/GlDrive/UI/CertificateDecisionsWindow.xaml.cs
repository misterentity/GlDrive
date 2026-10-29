using System.Windows;
using System.Windows.Controls;
using GlDrive.Config;
using GlDrive.Tls;

namespace GlDrive.UI;

public partial class CertificateDecisionsWindow : Window
{
    private readonly CertificateManager _certificates;
    public CertificateDecisionsWindow(CertificateManager certificates)
    {
        InitializeComponent();
        _certificates = certificates;
        RefreshDecisions();
    }
    public void RefreshDecisions()
    {
        var pending = _certificates.GetPendingDecisions();
        Decisions.ItemsSource = pending;
        StatusText.Text = pending.Count == 0 ? "No pending certificate changes." : $"{pending.Count} endpoint(s) waiting for a local decision.";
    }
    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshDecisions();
    private async void Accept_Click(object sender, RoutedEventArgs e) => await Resolve(sender, id => _certificates.ResolveFromLocalUi(id, true));
    private async void Reject_Click(object sender, RoutedEventArgs e) => await Resolve(sender, id => _certificates.ResolveFromLocalUi(id, false));
    private async void RetrySavedPin_Click(object sender, RoutedEventArgs e) => await Resolve(sender, _certificates.RetrySavedPinFromLocalUi);
    private async Task Resolve(object sender, Func<string, bool> resolve)
    {
        if (sender is not Button { Tag: string id } button) return;
        button.IsEnabled = false;
        using var attribution = ConfigWriteScope.Begin("ui:certificate-decision");
        var resolved = await Task.Run(() => resolve(id));
        RefreshDecisions();
        if (!resolved) StatusText.Text = "This decision changed. Review the current fingerprint before accepting.";
    }
}
