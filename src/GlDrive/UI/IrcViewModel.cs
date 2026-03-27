using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using GlDrive.Config;
using GlDrive.Irc;
using GlDrive.Services;
using Serilog;

namespace GlDrive.UI;

public class IrcChannelVm : INotifyPropertyChanged
{
    private int _unreadCount;

    public string ServerId { get; set; } = "";
    public string ServerName { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsChannel => Name.StartsWith('#') || Name.StartsWith('&');
    public bool HasFishKey { get; set; }

    public string DisplayName => IsChannel ? $"{ServerName}: {Name}" : $"{ServerName}: {Name} (PM)";
    public string SidebarDisplay => Name == "*" ? $"[{ServerName}]" : IsChannel ? Name : $"{Name} (PM)";

    public int UnreadCount
    {
        get => _unreadCount;
        set { _unreadCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(UnreadDisplay)); }
    }

    public string UnreadDisplay => _unreadCount > 0 ? $" ({_unreadCount})" : "";

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class IrcMessageVm
{
    public string Timestamp { get; set; } = "";
    public string Nick { get; set; } = "";
    public string Text { get; set; } = "";
    public IrcMessageType Type { get; set; }
    public bool WasEncrypted { get; set; }
    public SolidColorBrush NickColor { get; set; } = Brushes.White;

    public string FormattedLine => Type switch
    {
        IrcMessageType.Action => $"* {Nick} {Text}",
        IrcMessageType.Join or IrcMessageType.Part or IrcMessageType.Quit
            or IrcMessageType.Kick or IrcMessageType.Topic or IrcMessageType.Mode
            or IrcMessageType.System => $"*** {Text}",
        IrcMessageType.Notice => $"-{Nick}- {Text}",
        _ => $"<{Nick}> {Text}"
    };

    public bool IsSystemMessage => Type is IrcMessageType.Join or IrcMessageType.Part or IrcMessageType.Quit
        or IrcMessageType.Kick or IrcMessageType.Topic or IrcMessageType.Mode or IrcMessageType.System;
}

public class IrcViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ServerManager _serverManager;
    private readonly AppConfig _config;
    private readonly Action<string, string, IrcServiceState> _ircStateHandler;
    private IrcChannelVm? _selectedChannel;
    private string _inputText = "";
    private string _topicText = "";
    private string _statusText = "Not connected";
    private bool _isConnected;
    private bool _disposed;

    // Per-channel message buffers: key = "serverId:target"
    private readonly Dictionary<string, List<IrcMessageVm>> _messageBuffers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _nickBuffers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _subscribedServices = new();
    // Track subscribed handlers for cleanup
    private readonly Dictionary<string, (Action<string, IrcMessageItem> msg, Action<string> names, Action<string, string> topic, Action<IrcServiceState> state)> _serviceHandlers = new();
    private const int MaxMessages = 500;

    // Nick colors (deterministic)
    private static readonly SolidColorBrush[] NickColors =
    [
        new(Color.FromRgb(255, 100, 100)),
        new(Color.FromRgb(100, 255, 100)),
        new(Color.FromRgb(100, 200, 255)),
        new(Color.FromRgb(255, 200, 100)),
        new(Color.FromRgb(200, 100, 255)),
        new(Color.FromRgb(255, 150, 200)),
        new(Color.FromRgb(100, 255, 200)),
        new(Color.FromRgb(200, 200, 100)),
        new(Color.FromRgb(150, 150, 255)),
        new(Color.FromRgb(255, 180, 130)),
    ];

    public ObservableCollection<IrcChannelVm> Channels { get; } = new();
    public ObservableCollection<IrcMessageVm> Messages { get; } = new();
    public ObservableCollection<string> NickList { get; } = new();

    public IrcChannelVm? SelectedChannel
    {
        get => _selectedChannel;
        set
        {
            if (_selectedChannel == value) return;
            _selectedChannel = value;
            OnPropertyChanged();
            SwitchToChannel(value);
        }
    }

    public string InputText
    {
        get => _inputText;
        set { _inputText = value; OnPropertyChanged(); }
    }

    public string TopicText
    {
        get => _topicText;
        set { _topicText = value; OnPropertyChanged(); }
    }

    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    public bool IsConnected
    {
        get => _isConnected;
        set { _isConnected = value; OnPropertyChanged(); }
    }

    public ICommand SendCommand { get; }
    public ICommand JoinChannelCommand { get; }
    public ICommand PartChannelCommand { get; }
    public ICommand ReconnectCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand PrivateMessageNickCommand { get; }
    public ICommand KeyExchangeNickCommand { get; }
    public ICommand ReleaseLinkClickedCommand { get; }

    public event Action<string>? ReleaseLinkClicked;
    public event Action? ScrollToBottom;
    public event Action? FocusInput;

    public IrcViewModel(ServerManager serverManager, AppConfig config)
    {
        _serverManager = serverManager;
        _config = config;

        SendCommand = new RelayCommand(Send);
        JoinChannelCommand = new RelayCommand(() => { });
        PartChannelCommand = new RelayCommand(async () =>
        {
            if (_selectedChannel == null) return;
            var irc = _serverManager.GetIrcService(_selectedChannel.ServerId);
            if (irc != null && _selectedChannel.IsChannel)
                await irc.PartChannel(_selectedChannel.Name);
        });
        ReconnectCommand = new RelayCommand(async () =>
        {
            foreach (var (serverId, ircService) in _serverManager.GetIrcServices())
            {
                if (ircService.State != IrcServiceState.Connected)
                {
                    try
                    {
                        await ircService.StopAsync();
                        await ircService.StartAsync();
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "IRC reconnect failed for {ServerId}", serverId);
                    }
                }
            }
        });
        DisconnectCommand = new RelayCommand(async () =>
        {
            foreach (var (_, ircService) in _serverManager.GetIrcServices())
            {
                if (ircService.State != IrcServiceState.Disconnected)
                    await ircService.StopAsync();
            }
        });
        PrivateMessageNickCommand = new RelayCommand<string>(nick =>
        {
            if (!string.IsNullOrEmpty(nick))
                InputText = $"/msg {nick.TrimStart('@', '+', '%', '~', '&')} ";
        });
        KeyExchangeNickCommand = new RelayCommand<string>(async nick =>
        {
            if (string.IsNullOrEmpty(nick) || _selectedChannel == null) return;
            var cleanNick = nick.TrimStart('@', '+', '%', '~', '&');
            var irc = _serverManager.GetIrcService(_selectedChannel.ServerId);
            if (irc != null)
                await irc.InitiateKeyExchange(cleanNick);
        });
        ReleaseLinkClickedCommand = new RelayCommand<string>(releaseName =>
        {
            if (!string.IsNullOrEmpty(releaseName))
                ReleaseLinkClicked?.Invoke(releaseName);
        });

        // Subscribe to existing IRC services
        foreach (var (serverId, ircService) in _serverManager.GetIrcServices())
            SubscribeToIrcService(ircService);

        // Subscribe when new IRC services start
        _ircStateHandler = (serverId, serverName, state) =>
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                var irc = _serverManager.GetIrcService(serverId);
                if (irc != null && state == IrcServiceState.Connected)
                    SubscribeToIrcService(irc);

                UpdateStatus();
            });
        };
        _serverManager.IrcStateChanged += _ircStateHandler;

        UpdateStatus();
    }

    private void SubscribeToIrcService(IrcService irc)
    {
        if (!_subscribedServices.Add(irc.ServerId)) return;

        Action<string, IrcMessageItem> msgHandler = (target, item) =>
            Application.Current?.Dispatcher.InvokeAsync(() => OnIrcMessage(irc.ServerId, irc.ServerName, target, item));
        Action<string> namesHandler = channel =>
            Application.Current?.Dispatcher.InvokeAsync(() => OnNamesUpdated(irc.ServerId, channel, irc));
        Action<string, string> topicHandler = (channel, topic) =>
            Application.Current?.Dispatcher.InvokeAsync(() => OnTopicChanged(irc.ServerId, channel, topic));
        Action<IrcServiceState> stateHandler = state =>
            Application.Current?.Dispatcher.InvokeAsync(UpdateStatus);

        irc.MessageReceived += msgHandler;
        irc.NamesUpdated += namesHandler;
        irc.TopicChanged += topicHandler;
        irc.StateChanged += stateHandler;
        _serviceHandlers[irc.ServerId] = (msgHandler, namesHandler, topicHandler, stateHandler);

        // Hydrate sidebar with existing channels if IRC already connected
        // (Dashboard opened after IRC was up)
        if (irc.State == IrcServiceState.Connected)
        {
            EnsureChannelVm(irc.ServerId, irc.ServerName, "*");
            foreach (var (name, ch) in irc.Channels)
            {
                var vm = EnsureChannelVm(irc.ServerId, irc.ServerName, name);
                vm.HasFishKey = ch.HasFishKey;
            }
        }
    }

    private void OnIrcMessage(string serverId, string serverName, string target, IrcMessageItem item)
    {
        var bufferKey = $"{serverId}:{target}";

        if (!_messageBuffers.ContainsKey(bufferKey))
            _messageBuffers[bufferKey] = [];

        var vm = new IrcMessageVm
        {
            Timestamp = item.Timestamp.ToString("HH:mm:ss"),
            Nick = item.Nick,
            Text = item.Text,
            Type = item.Type,
            WasEncrypted = item.WasEncrypted,
            NickColor = GetNickColor(item.Nick)
        };

        var buffer = _messageBuffers[bufferKey];
        buffer.Add(vm);
        if (buffer.Count > MaxMessages)
            buffer.RemoveAt(0);

        // Ensure channel exists in sidebar
        var channelVm = EnsureChannelVm(serverId, serverName, target);

        // Update FiSH key status
        var ircService = _serverManager.GetIrcService(serverId);
        if (ircService != null)
            channelVm.HasFishKey = ircService.KeyStore.GetKey(target) != null;

        // If this is the selected channel, add to visible messages
        if (_selectedChannel != null && _selectedChannel.ServerId == serverId &&
            _selectedChannel.Name.Equals(target, StringComparison.OrdinalIgnoreCase))
        {
            Messages.Add(vm);
            if (Messages.Count > MaxMessages)
                Messages.RemoveAt(0);
            ScrollToBottom?.Invoke();
        }
        else
        {
            channelVm.UnreadCount++;
        }
    }

    private void OnNamesUpdated(string serverId, string channel, IrcService irc)
    {
        if (_selectedChannel == null || _selectedChannel.ServerId != serverId ||
            !_selectedChannel.Name.Equals(channel, StringComparison.OrdinalIgnoreCase))
            return;

        if (irc.Channels.TryGetValue(channel, out var ch))
        {
            NickList.Clear();
            foreach (var nick in SortNicks(ch.Nicks))
                NickList.Add(nick);
        }
    }

    private static int NickSortOrder(string n) => n.Length > 0 ? n[0] switch
    {
        '~' => 0, '&' => 1, '@' => 2, '%' => 3, '+' => 4, _ => 5
    } : 5;

    private static IEnumerable<string> SortNicks(List<string> nicks) =>
        nicks.OrderBy(NickSortOrder)
             .ThenBy(n => n.TrimStart('@', '+', '%', '~', '&'), StringComparer.OrdinalIgnoreCase);

    private void OnTopicChanged(string serverId, string channel, string topic)
    {
        if (_selectedChannel?.ServerId == serverId &&
            _selectedChannel.Name.Equals(channel, StringComparison.OrdinalIgnoreCase))
        {
            TopicText = topic;
        }
    }

    private IrcChannelVm EnsureChannelVm(string serverId, string serverName, string target)
    {
        var existing = Channels.FirstOrDefault(c =>
            c.ServerId == serverId && c.Name.Equals(target, StringComparison.OrdinalIgnoreCase));

        if (existing != null) return existing;

        var vm = new IrcChannelVm
        {
            ServerId = serverId,
            ServerName = serverName,
            Name = target
        };
        Channels.Add(vm);
        return vm;
    }

    private void SwitchToChannel(IrcChannelVm? channel)
    {
        Messages.Clear();
        NickList.Clear();
        TopicText = "";

        if (channel == null) return;

        channel.UnreadCount = 0;
        var bufferKey = $"{channel.ServerId}:{channel.Name}";

        if (_messageBuffers.TryGetValue(bufferKey, out var buffer))
        {
            foreach (var msg in buffer)
                Messages.Add(msg);
        }

        var irc = _serverManager.GetIrcService(channel.ServerId);
        if (irc != null && irc.Channels.TryGetValue(channel.Name, out var ch))
        {
            TopicText = ch.Topic;
            foreach (var nick in SortNicks(ch.Nicks))
                NickList.Add(nick);
        }

        ScrollToBottom?.Invoke();
    }

    private async void Send()
    {
        if (string.IsNullOrWhiteSpace(_inputText) || _selectedChannel == null) return;

        var text = _inputText;
        InputText = "";
        FocusInput?.Invoke();

        var irc = _serverManager.GetIrcService(_selectedChannel.ServerId);
        if (irc == null) return;

        // Handle slash commands
        if (text.StartsWith('/'))
        {
            await HandleSlashCommand(irc, text);
            return;
        }

        await irc.SendMessage(_selectedChannel.Name, text);
    }

    private async Task HandleSlashCommand(IrcService irc, string input)
    {
        var parts = input.Split(' ', 2);
        var cmd = parts[0].ToLowerInvariant();
        var args = parts.Length > 1 ? parts[1] : "";

        switch (cmd)
        {
            case "/join":
                var joinParts = args.Split(' ', 2);
                await irc.JoinChannel(joinParts[0], joinParts.Length > 1 ? joinParts[1] : null);
                break;

            case "/part":
                var partChannel = string.IsNullOrEmpty(args) ? _selectedChannel?.Name : args;
                if (partChannel != null)
                    await irc.PartChannel(partChannel);
                break;

            case "/msg":
                var msgParts = args.Split(' ', 2);
                if (msgParts.Length >= 2)
                    await irc.SendMessage(msgParts[0], msgParts[1]);
                break;

            case "/me":
                if (_selectedChannel != null)
                    await irc.SendAction(_selectedChannel.Name, args);
                break;

            case "/topic":
                if (_selectedChannel != null)
                    await irc.SetTopic(_selectedChannel.Name, args);
                break;

            case "/key":
                // /key [target] <key>
                var keyParts = args.Split(' ', 2);
                if (keyParts.Length >= 2)
                    irc.SetFishKey(keyParts[0], keyParts[1], _config.Servers
                        .FirstOrDefault(s => s.Id == irc.ServerId)?.Irc.FishMode ?? FishMode.ECB);
                else if (_selectedChannel != null && keyParts.Length == 1)
                    irc.SetFishKey(_selectedChannel.Name, keyParts[0], _config.Servers
                        .FirstOrDefault(s => s.Id == irc.ServerId)?.Irc.FishMode ?? FishMode.ECB);
                break;

            case "/keyx":
                // /keyx <nick> — initiate DH1080
                if (!string.IsNullOrEmpty(args))
                    await irc.InitiateKeyExchange(args.Trim());
                break;

            case "/notice":
                var noticeParts = args.Split(' ', 2);
                if (noticeParts.Length >= 2)
                    await irc.SendNotice(noticeParts[0], noticeParts[1]);
                break;

            case "/quit":
                await irc.StopAsync();
                break;

            case "/help":
                AddLocalSystem("Available commands:");
                AddLocalSystem("  /join #channel [key]  — Join a channel");
                AddLocalSystem("  /part [#channel]      — Leave current or named channel");
                AddLocalSystem("  /msg nick text        — Send a private message");
                AddLocalSystem("  /me action            — Send an action");
                AddLocalSystem("  /topic text           — Set channel topic");
                AddLocalSystem("  /notice target text   — Send a notice");
                AddLocalSystem("  /key [target] key     — Set FiSH key");
                AddLocalSystem("  /keyx nick            — Initiate DH1080 key exchange");
                AddLocalSystem("  /quit                 — Disconnect from IRC");
                AddLocalSystem("  /help                 — Show this help");
                AddLocalSystem("  /command ...          — Any other /command sent as raw IRC");
                break;

            default:
                // Send unknown commands as raw IRC
                await irc.SendRaw(input[1..]); // strip leading /
                break;
        }
    }

    private void UpdateStatus()
    {
        var services = _serverManager.GetIrcServices();
        if (services.Count == 0)
        {
            StatusText = "IRC not configured";
            IsConnected = false;
            return;
        }

        var connected = services.Values.Count(s => s.State == IrcServiceState.Connected);
        var total = services.Count;

        IsConnected = connected > 0;
        StatusText = connected switch
        {
            0 => total == 1 ? "IRC disconnected" : "IRC: 0 connected",
            _ when connected == total && total == 1 => "IRC connected",
            _ => $"IRC: {connected}/{total} connected"
        };
    }

    // Tab-completion state
    private string? _tabPrefix;
    private List<string>? _tabMatches;
    private int _tabIndex;

    public bool HandleTabComplete()
    {
        if (NickList.Count == 0) return false;

        if (_tabMatches == null || _tabPrefix == null)
        {
            // Start new completion: find the partial word at end of input
            var text = _inputText ?? "";
            var lastSpace = text.LastIndexOf(' ');
            _tabPrefix = lastSpace >= 0 ? text[(lastSpace + 1)..] : text;
            if (string.IsNullOrEmpty(_tabPrefix)) return false;

            _tabMatches = NickList
                .Select(n => n.TrimStart('@', '+', '%', '~', '&'))
                .Where(n => n.StartsWith(_tabPrefix, StringComparison.OrdinalIgnoreCase))
                .ToList();
            _tabIndex = 0;

            if (_tabMatches.Count == 0)
            {
                ResetTabComplete();
                return false;
            }
        }
        else
        {
            _tabIndex = (_tabIndex + 1) % _tabMatches.Count;
        }

        // Replace the partial word with the match
        var input = _inputText ?? "";
        var lastSp = input.LastIndexOf(' ');
        var before = lastSp >= 0 ? input[..(lastSp + 1)] : "";
        var suffix = string.IsNullOrEmpty(before) ? ": " : " ";
        InputText = before + _tabMatches[_tabIndex] + suffix;
        return true;
    }

    public void ResetTabComplete()
    {
        _tabPrefix = null;
        _tabMatches = null;
        _tabIndex = 0;
    }

    public void AddLocalSystem(string text)
    {
        var vm = new IrcMessageVm
        {
            Timestamp = DateTime.Now.ToString("HH:mm:ss"),
            Text = text,
            Type = IrcMessageType.System,
            NickColor = Brushes.Gray
        };
        Messages.Add(vm);
        ScrollToBottom?.Invoke();
    }

    private static SolidColorBrush GetNickColor(string nick)
    {
        if (string.IsNullOrEmpty(nick)) return Brushes.Gray;
        var hash = nick.Aggregate(0, (h, c) => h * 31 + c);
        return NickColors[Math.Abs(hash) % NickColors.Length];
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _serverManager.IrcStateChanged -= _ircStateHandler;
        foreach (var (serverId, handlers) in _serviceHandlers)
        {
            var irc = _serverManager.GetIrcService(serverId);
            if (irc != null)
            {
                irc.MessageReceived -= handlers.msg;
                irc.NamesUpdated -= handlers.names;
                irc.TopicChanged -= handlers.topic;
                irc.StateChanged -= handlers.state;
            }
        }
        _serviceHandlers.Clear();
        _subscribedServices.Clear();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
