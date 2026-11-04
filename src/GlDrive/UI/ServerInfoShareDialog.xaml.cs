using System.Windows;
using GlDrive.Config;
using GlDrive.Irc;
using GlDrive.Services;
using Serilog;

namespace GlDrive.UI;

/// <summary>
/// Sends a site's BNC / IRCd details (optionally with passwords and channel keys) to a
/// nick in a PM. The text is previewed and editable; nothing goes out until Send.
/// </summary>
public partial class ServerInfoShareDialog : Window
{
    // Paced so a multi-line paste doesn't trip the IRCd's flood protection.
    private static readonly TimeSpan LineDelay = TimeSpan.FromMilliseconds(700);

    private readonly ServerManager _serverManager;
    private readonly IrcService _irc;
    private readonly string _nick;
    private bool _sending;
    private bool _cancelled;

    public ServerInfoShareDialog(ServerManager serverManager, AppConfig config, IrcService irc, string nick)
    {
        _serverManager = serverManager;
        _irc = irc;
        _nick = nick;
        InitializeComponent();

        Title = $"Send server info to {nick}";
        HeaderText.Text = $">> SEND SERVER INFO TO {nick.ToUpperInvariant()}";
        ServerPicker.ItemsSource = config.Servers;
        ServerPicker.SelectedItem = config.Servers.FirstOrDefault(s => s.Id == irc.ServerId) ?? config.Servers.FirstOrDefault();

        var encrypted = irc.WillEncrypt(nick);
        EncryptionText.Text = encrypted
            ? $"\U0001F512 Your PM with {nick} is FiSH-encrypted."
            : $"⚠ No FiSH key with {nick}: this goes out in plain text across the IRC network. " +
              "Right-click the nick and run a FiSH key exchange first if it includes passwords.";
        EncryptionText.SetResourceReference(ForegroundProperty, encrypted ? "ForegroundDimBrush" : "AccentBrush");
        Rebuild();
    }

    private void Options_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) Rebuild();
    }

    private void Rebuild()
    {
        IrcPassBox.IsEnabled = IrcdBox.IsChecked == true;
        FishBox.IsEnabled = ChannelsBox.IsChecked == true;
        if (ServerPicker.SelectedItem is not ServerConfig server)
        {
            PreviewBox.Text = "";
            return;
        }

        var options = new ServerInfoShareOptions
        {
            Bnc = BncBox.IsChecked == true,
            FtpLogin = FtpLoginBox.IsChecked == true,
            Ircd = IrcdBox.IsChecked == true,
            IrcPassword = IrcPassBox.IsChecked == true,
            Channels = ChannelsBox.IsChecked == true,
            FishKeys = FishBox.IsChecked == true,
        };
        PreviewBox.Text = string.Join(Environment.NewLine, ServerInfoShare.BuildLines(server, options, LoadSecrets(server, options)));

        StatusText.Text = options.FishKeys && options.Channels && _serverManager.GetIrcService(server.Id) == null
            ? $"{server.Name} IRC isn't running, so its blowfish keys aren't available."
            : "";
    }

    private ServerInfoSecrets LoadSecrets(ServerConfig server, ServerInfoShareOptions options)
    {
        string? ftpPassword = null, ircPassword = null;
        IReadOnlyDictionary<string, FishKeyEntry>? fish = null;
        try
        {
            var c = server.Connection;
            if (options.FtpLogin && !string.IsNullOrWhiteSpace(c.Username))
                ftpPassword = CredentialStore.GetPassword(c.Host, c.Port, c.Username);
            var irc = server.Irc;
            if (options.Ircd && options.IrcPassword && !string.IsNullOrWhiteSpace(irc.Host))
                ircPassword = CredentialStore.GetIrcPassword(irc.Host, irc.Port, irc.Nick);
            if (options.Channels && options.FishKeys)
                fish = _serverManager.GetIrcService(server.Id)?.KeyStore.GetAllKeys();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Server info share: failed to read secrets for {Server}", server.Name);
        }
        return new ServerInfoSecrets(ftpPassword, ircPassword, fish);
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (_sending) return;
        var lines = (PreviewBox.Text ?? "")
            .Split('\n')
            .Select(l => l.TrimEnd('\r').Trim())
            .Where(l => l.Length > 0)
            .ToList();
        if (lines.Count == 0)
        {
            StatusText.Text = "Nothing to send.";
            return;
        }

        _sending = true;
        SendButton.IsEnabled = false;
        try
        {
            for (var i = 0; i < lines.Count; i++)
            {
                if (i > 0) await Task.Delay(LineDelay);
                if (_cancelled) return;
                StatusText.Text = $"Sending {i + 1}/{lines.Count}…";
                if (!await _irc.SendMessage(_nick, lines[i], sensitive: true))
                {
                    StatusText.Text = $"IRC is not connected: sent {i} of {lines.Count} lines.";
                    return;
                }
            }
            Log.Information("Sent {Count} lines of server info to {Nick} on {Server}", lines.Count, _nick, _irc.ServerName);
            if (IsVisible) Close();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Server info share to {Nick} failed", _nick);
            StatusText.Text = "Send failed: " + ex.Message;
        }
        finally
        {
            _sending = false;
            SendButton.IsEnabled = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cancelled = true;
        Close();
    }
}
