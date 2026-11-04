using System.Net;
using System.Net.Http;
using System.Windows;
using GlDrive.Config;
using GlDrive.Irc;
using Serilog;

namespace GlDrive.UI;

/// <summary>
/// Sends your own addline (site adduser you pass ident@ip) to a siteop in a PM and remembers it,
/// so their reply can become a new server with this login already filled in.
/// </summary>
public partial class SendAddlineDialog : Window
{
    private readonly IrcService _irc;
    private readonly AddlineOfferStore _store;
    private readonly string _nick;
    private bool _sending;

    public SendAddlineDialog(IrcService irc, AppConfig config, AddlineOfferStore store, string nick)
    {
        _irc = irc;
        _store = store;
        _nick = nick;
        InitializeComponent();
        Title = $"Send my addline to {nick}";
        HeaderText.Text = $">> SEND MY ADDLINE TO {nick.ToUpperInvariant()}";

        var (lastUser, lastMasks) = store.LastUsed();
        UserBox.Text = !string.IsNullOrWhiteSpace(lastUser) ? lastUser
            : config.Servers.Select(s => s.Connection.Username).Where(u => !string.IsNullOrWhiteSpace(u))
                .GroupBy(u => u, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? "";
        PassBox.Text = AddlineText.GeneratePassword();
        MasksBox.Text = string.Join(" ", lastMasks);

        var encrypted = irc.WillEncrypt(nick);
        EncryptionText.Text = encrypted
            ? $"\U0001F512 Your PM with {nick} is FiSH-encrypted."
            : $"⚠ No FiSH key with {nick}: the password goes out in plain text. Right-click the nick and run a FiSH key exchange first.";
        EncryptionText.SetResourceReference(ForegroundProperty, encrypted ? "ForegroundDimBrush" : "AccentBrush");
        Refresh();
    }

    private void Fields_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) => Refresh();

    private bool Refresh()
    {
        if (PreviewBox == null || SendButton == null || StatusText == null) return false;
        var masks = AddlineText.ParseMasks(MasksBox.Text);
        string? problem = !AddlineText.IsSafeToken(UserBox.Text) ? "Enter a username (no spaces)."
            : !AddlineText.IsSafeToken(PassBox.Text) ? "Enter a password (no spaces)."
            : masks.Count == 0 ? "Add at least one ident@ip mask, or use Detect my IP."
            : masks.Any(m => !AddlineText.IsSafeToken(m) || m.IndexOf('@') == m.Length - 1) ? "A mask looks wrong — use ident@ip, e.g. *@1.2.3.4."
            : null;
        PreviewBox.Text = AddlineText.Build(UserBox.Text, PassBox.Text, masks);
        if (!_sending) StatusText.Text = problem ?? "";
        SendButton.IsEnabled = problem == null && !_sending;
        return problem == null;
    }

    private void NewPassword_Click(object sender, RoutedEventArgs e) => PassBox.Text = AddlineText.GeneratePassword();

    private async void DetectIp_Click(object sender, RoutedEventArgs e)
    {
        DetectButton.IsEnabled = false;
        StatusText.Text = "Looking up your public IP…";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("GlDrive");
            var text = (await http.GetStringAsync("https://api.ipify.org")).Trim();
            if (!IPAddress.TryParse(text, out var ip)) throw new FormatException("unexpected reply");
            var mask = "*@" + ip;
            var masks = AddlineText.ParseMasks(MasksBox.Text).ToList();
            if (!masks.Contains(mask, StringComparer.OrdinalIgnoreCase)) masks.Insert(0, mask);
            MasksBox.Text = string.Join(" ", masks);
            StatusText.Text = $"Your public IP is {ip}.";
        }
        catch (Exception ex)
        {
            Log.Information("Public IP lookup failed: {Error}", ex.Message);
            StatusText.Text = "Couldn't look up your IP. Type it in instead.";
        }
        finally { DetectButton.IsEnabled = true; }
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (_sending || !Refresh()) return;
        var offer = new AddlineOffer(_irc.ServerId, _nick, UserBox.Text.Trim(), PassBox.Text.Trim(),
            AddlineText.ParseMasks(MasksBox.Text), DateTimeOffset.UtcNow);
        _sending = true;
        SendButton.IsEnabled = false;
        StatusText.Text = "Sending…";
        try
        {
            if (!await _irc.SendMessage(_nick, offer.Line, sensitive: true))
            {
                StatusText.Text = "IRC is not connected. Nothing was sent.";
                return;
            }
            _store.Record(offer);
            Log.Information("Sent addline for {User} to {Nick} on {Server}", offer.Username, _nick, _irc.ServerName);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Sending addline to {Nick} failed", _nick);
            StatusText.Text = "Send failed: " + ex.Message;
        }
        finally
        {
            _sending = false;
            if (IsLoaded) Refresh();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
