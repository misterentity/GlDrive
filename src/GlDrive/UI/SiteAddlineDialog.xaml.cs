using System.Windows;
using System.Windows.Controls;
using GlDrive.Config;
using GlDrive.Irc;
using GlDrive.Services;
using Serilog;

namespace GlDrive.UI;

/// <summary>
/// Sends glftpd addlines picked out of IRC (SITE ADDUSER / GADDUSER / ADDIP) to a chosen site.
/// The commands are previewed and editable; nothing is sent until Send, and each line is
/// re-validated so the box cannot be used for arbitrary SITE commands.
/// </summary>
public partial class SiteAddlineDialog : Window
{
    private sealed record SiteChoice(string Id, string Name, bool Connected)
    {
        public string Label => Connected ? Name : $"{Name} (not connected)";
    }

    private readonly ServerManager _serverManager;
    private CancellationTokenSource? _sending;

    public SiteAddlineDialog(ServerManager serverManager, AppConfig config,
        IReadOnlyList<SiteAddlineCommand> addlines, string? fromNick, string? defaultServerId)
    {
        _serverManager = serverManager;
        InitializeComponent();

        if (!string.IsNullOrWhiteSpace(fromNick))
            HeaderText.Text = $">> SEND ADDLINE FROM {fromNick.ToUpperInvariant()} TO SITE";
        var choices = config.Servers.Where(s => s.Enabled)
            .Select(s => new SiteChoice(s.Id, s.Name,
                serverManager.GetServer(s.Id) is { CurrentState: MountState.Connected, Pool: not null }))
            .OrderByDescending(c => c.Connected).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        ServerPicker.ItemsSource = choices;
        ServerPicker.SelectedItem = choices.FirstOrDefault(c => c.Id == defaultServerId && c.Connected)
                                    ?? choices.FirstOrDefault(c => c.Connected) ?? choices.FirstOrDefault();
        CommandsBox.Text = string.Join(Environment.NewLine, addlines.Select(a => a.Command));
        Validate();
    }

    private void ServerPicker_SelectionChanged(object sender, SelectionChangedEventArgs e) => Validate();
    private void CommandsBox_TextChanged(object sender, TextChangedEventArgs e) => Validate();

    private List<string> Lines() => (CommandsBox?.Text ?? "")
        .Split('\n').Select(l => l.TrimEnd('\r').Trim()).Where(l => l.Length > 0).ToList();

    private bool Validate()
    {
        if (StatusText == null || SendButton == null) return false;
        if (_sending != null) return false;
        string? problem = null;
        var lines = Lines();
        if (ServerPicker.SelectedItem is not SiteChoice site) problem = "Choose a site.";
        else if (!site.Connected) problem = $"{site.Name} isn't connected. Mount it first.";
        else if (lines.Count == 0) problem = "Nothing to send.";
        else
            for (var i = 0; i < lines.Count && problem == null; i++)
                if (!SiteAddline.TryParseLine(lines[i], out _, out var error))
                    problem = lines.Count > 1 ? $"Line {i + 1}: {error}" : error;
        StatusText.Text = problem ?? $"{lines.Count} command{(lines.Count == 1 ? "" : "s")} ready for {((SiteChoice)ServerPicker.SelectedItem!).Name}.";
        SendButton.IsEnabled = problem == null;
        return problem == null;
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (_sending != null || !Validate() || ServerPicker.SelectedItem is not SiteChoice site) return;
        var commands = Lines().Select(l => SiteAddline.TryParseLine(l, out var c, out _) ? c : null).OfType<SiteAddlineCommand>().ToList();

        _sending = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        SendButton.IsEnabled = false;
        CommandsBox.IsReadOnly = true;
        ServerPicker.IsEnabled = false;
        var ok = 0;
        try
        {
            for (var i = 0; i < commands.Count; i++)
            {
                var command = commands[i];
                StatusText.Text = $"Sending {i + 1}/{commands.Count} to {site.Name}…";
                AppendReply($"> {command.Redacted}");
                try
                {
                    var reply = await _serverManager.SendSiteAddlineAsync(site.Id, command, _sending.Token);
                    AppendReply($"{reply.Code} {reply.Message}");
                    if (reply.Success) ok++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Addline {Command} to {Server} failed", command.Redacted, site.Name);
                    AppendReply("Failed: " + ex.Message);
                }
            }
            StatusText.Text = ok == commands.Count
                ? $"{site.Name} accepted all {commands.Count}."
                : $"{site.Name} accepted {ok} of {commands.Count}. See the replies above.";
        }
        catch (OperationCanceledException)
        {
            // The command may already have reached the site; never resend it automatically.
            if (IsLoaded) StatusText.Text = "Stopped waiting for the site. It may still have applied the last command — check before resending.";
        }
        finally
        {
            _sending.Dispose();
            _sending = null;
            CommandsBox.IsReadOnly = false;
            ServerPicker.IsEnabled = true;
            SendButton.IsEnabled = true;
        }
    }

    private void AppendReply(string text)
    {
        RepliesBox.AppendText((RepliesBox.Text.Length > 0 ? Environment.NewLine : "") + text);
        RepliesBox.ScrollToEnd();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        _sending?.Cancel();
        Close();
    }
}
