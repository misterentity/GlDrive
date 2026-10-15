using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using GlDrive.UI;

namespace GlDrive.Services.Automation;

/// <summary>Exposes the same visible view models and awaited commands used by the desktop.</summary>
public sealed class UiAutomation(Func<DashboardWindow> getDashboard) : IAutomationModule
{
    private readonly HashSet<(object Model, string Command)> _runningCommands = [];
    private static readonly string[] Screens = ["dashboard", "player", "spread", "browse", "plex"];
    // Dialog/file-picker commands have dedicated service tools. These commands can run unattended.
    private static readonly HashSet<string> Commands = new(StringComparer.Ordinal)
    {
        "SearchCommand", "CancelSearchCommand", "LoadUpcomingCommand", "SearchShowScheduleCommand",
        "ClearShowSearchCommand", "SearchPreDbCommand", "CancelPreDbSearchCommand", "LoadLatestPreDbCommand",
        "SearchPreDbOnServersCommand", "SearchNotificationOnServersCommand", "RefreshMetadataCommand",
        "SearchAndPlayCommand", "PlayResultCommand", "PlayPauseCommand", "StopCommand", "RefreshTrendingCommand", "ToggleLibraryCommand",
        "PlayLibraryItemCommand", "PlayEpisodeCommand", "SeekForwardCommand", "SeekBackwardCommand",
        "ClearSearchCommand", "SearchTorrentCommand", "PlayTorrentCommand", "PauseDownloadCommand", "ResumeDownloadCommand",
        "CastToDeviceCommand", "StopCastingCommand", "LeftGoCommand", "RightGoCommand",
        "LeftDoubleClickCommand", "RightDoubleClickCommand", "PauseJobCommand", "ResumeJobCommand",
        "DismissJobCommand", "ClearFinishedCommand", "RefreshServersCommand", "RefreshCommand", "TestConnectionCommand"
    };
    private static readonly HashSet<string> Editable = new(StringComparer.Ordinal)
    {
        "SearchText", "SearchQuery", "SearchFilterQuality", "SearchFilterSource", "SearchFilterBigSize",
        "SelectedSearchServer", "SelectedSearchCategory", "ShowSearchText", "PreDbSearchText", "PreDbCategory",
        "Volume", "AudioTrackIndex", "SubtitleTrackIndex", "ShowLibrary", "LeftServer", "LeftPath", "RightServer", "RightPath",
        "SelectedSection", "SpreadReleaseName", "InviteTarget", "AllowDownloads", "SelectedNotificationCategory", "SelectedNotificationServer"
    };
    private static object Schema(object properties, string[] required) => new { type = "object", properties, required, additionalProperties = false };
    private static readonly object Screen = new { type = "string", @enum = Screens };
    private static readonly object Text = new { type = "string", minLength = 1, maxLength = 256 };
    public IReadOnlyList<AutomationToolDefinition> Tools { get; } =
    [
        AutomationToolDefinition.Create("ui.state", "Inspect desktop screen state, available editable fields, collections and callable commands. Opens the dashboard if needed. Collections are bounded to 200 rows. Embedded third-party websites are outside this API.", Schema(new { screen = Screen }, ["screen"])),
        AutomationToolDefinition.Create("ui.set", "Set a field listed in ui.state editableFields, on the UI dispatcher.", Schema(new { screen = Screen, property = Text, value = new { } }, ["screen", "property", "value"])),
        AutomationToolDefinition.Create("ui.select", "Select an existing collection item by index into a Selected... property. Inspect ui.state first.", Schema(new { screen = Screen, property = Text, collection = Text, index = new { type = "integer", minimum = 0 } }, ["screen", "property", "collection", "index"])),
        AutomationToolDefinition.Create("ui.invoke", "Await an available command from ui.state. Optional parameter may be scalar, or select a collection/index for commands taking a row. Inspect state afterward for UI status and errors.", Schema(new { screen = Screen, command = Text, parameter = new { }, collection = Text, index = new { type = "integer", minimum = 0 } }, ["screen", "command"])),
        AutomationToolDefinition.Create("player.seek", "Seek the desktop player to a position from 0 to 1.", Schema(new { position = new { type = "number", minimum = 0, maximum = 1 } }, ["position"])),
        AutomationToolDefinition.Create("player.play_local", "Play a local media file in the desktop player.", Schema(new { path = new { type = "string", minLength = 1, maxLength = 4096 } }, ["path"])),
        AutomationToolDefinition.Create("player.download_torrent", "Queue a magnet in the main Downloads list and return its durable id and queueId. The item uses a dedicated subfolder of directory. Follow and control it with downloads tools; skipBlockedFiles explicitly approves downloading only permitted files.", Schema(new { magnet = new { type = "string", maxLength = 16384 }, directory = new { type = "string", maxLength = 4096 }, skipBlockedFiles = new { type = "boolean" } }, ["magnet", "directory"]))
    ];

    public async Task<object?> ExecuteAsync(string operation, JsonElement args, CancellationToken ct)
    {
        var dispatcher = Application.Current?.Dispatcher ?? throw new InvalidOperationException("Desktop session is unavailable");
        return await (await dispatcher.InvokeAsync(async () =>
        {
            ct.ThrowIfCancellationRequested();
            var screen = operation.StartsWith("player.", StringComparison.Ordinal) ? "player" : args.GetProperty("screen").GetString()!;
            var model = getDashboard().GetAutomationModel(screen);
            if (operation == "ui.state") return State(model);
            if (operation == "player.seek") { ((PlayerViewModel)model).SeekTo(args.GetProperty("position").GetDouble() * 100); return State(model); }
            if (operation == "player.play_local")
            { await ((PlayerViewModel)model).AutomationPlayLocal(args.GetProperty("path").GetString()!); return State(model); }
            if (operation == "player.download_torrent")
                return await ((PlayerViewModel)model).AutomationDownloadTorrent(args.GetProperty("magnet").GetString()!, args.GetProperty("directory").GetString()!, args.TryGetProperty("skipBlockedFiles", out var skip) && skip.GetBoolean());
            if (operation is "ui.set" or "ui.select")
            {
                var name = args.GetProperty("property").GetString()!;
                var property = model.GetType().GetProperty(name) ?? throw new ArgumentException("Unknown property");
                if (property.SetMethod?.IsPublic != true) throw new ArgumentException("Property is read-only");
                object? value;
                if (operation == "ui.set")
                {
                    if (!Editable.Contains(name) || !Scalar(property.PropertyType)) throw new ArgumentException("Property is not editable through ui.set");
                    value = args.GetProperty("value").Deserialize(property.PropertyType);
                    if (name == "Volume" && value is double volume && (volume < 0 || volume > 100)) throw new ArgumentException("Volume must be 0..100");
                }
                else
                {
                    if (!name.StartsWith("Selected", StringComparison.Ordinal)) throw new ArgumentException("Only selection properties may receive a row");
                    value = Row(model, args);
                    if (!property.PropertyType.IsInstanceOfType(value)) throw new ArgumentException("Collection row does not match selection type");
                }
                property.SetValue(model, value);
                return State(model);
            }
            if (operation == "ui.invoke")
            {
                var name = args.GetProperty("command").GetString()!;
                if (!Commands.Contains(name)) throw new ArgumentException("Command is not available through automation");
                var command = model.GetType().GetProperty(name)?.GetValue(model) as IAsyncRelayCommand
                    ?? throw new ArgumentException("Unknown command on this screen");
                object? parameter = args.TryGetProperty("collection", out _) ? Row(model, args) : null;
                if (args.TryGetProperty("parameter", out var p))
                {
                    var type = command.GetType();
                    if (!type.IsGenericType || !Scalar(type.GetGenericArguments()[0])) throw new ArgumentException("Use collection/index for this command parameter");
                    parameter = p.Deserialize(type.GetGenericArguments()[0]);
                }
                var commandType = command.GetType();
                if (commandType.IsGenericType && (parameter == null || !commandType.GetGenericArguments()[0].IsInstanceOfType(parameter)))
                    throw new ArgumentException("Command requires a parameter or collection row matching " + commandType.GetGenericArguments()[0].Name);
                if (!commandType.IsGenericType && parameter != null) throw new ArgumentException("Command takes no parameter");
                if (!command.CanExecute(parameter)) throw new InvalidOperationException("Command is disabled");
                if (!_runningCommands.Add((model, name))) throw new InvalidOperationException("This command is already running");
                try { await command.ExecuteAsync(parameter); }
                finally { _runningCommands.Remove((model, name)); }
                return State(model);
            }
            throw new KeyNotFoundException("Unknown UI operation");
        }, System.Windows.Threading.DispatcherPriority.Normal, ct));
    }

    private static object Row(object model, JsonElement args)
    {
        var name = args.GetProperty("collection").GetString()!;
        var sequence = model.GetType().GetProperty(name)?.GetValue(model) as IEnumerable ?? throw new ArgumentException("Unknown collection");
        var index = args.GetProperty("index").GetInt32();
        return sequence.Cast<object>().ElementAtOrDefault(index) ?? throw new ArgumentException("Index is out of range");
    }

    private static object State(object model) => new
    {
        state = Project(model, 0),
        editableFields = model.GetType().GetProperties().Where(p => Editable.Contains(p.Name) && p.SetMethod?.IsPublic == true && Scalar(p.PropertyType)).Select(p => new { name = p.Name, type = p.PropertyType.Name }).ToArray(),
        commands = model.GetType().GetProperties().Where(p => Commands.Contains(p.Name) && typeof(IAsyncRelayCommand).IsAssignableFrom(p.GetValue(model)?.GetType())).Select(p => p.Name).ToArray()
    };

    internal static bool Scalar(Type type) => type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) || type == typeof(DateTime) || type == typeof(TimeSpan) || (Nullable.GetUnderlyingType(type) is { } inner && Scalar(inner));
    internal static object? Project(object? value, int depth)
    {
        if (value == null) return null;
        if (Scalar(value.GetType())) return value;
        if (depth > 3) return null;
        if (value is IEnumerable sequence) return sequence.Cast<object?>().Take(200).Select(v => Project(v, depth + 1)).ToArray();
        if (value.GetType().Namespace is not ("GlDrive.UI" or "GlDrive.Plex" or "GlDrive.Player" or "GlDrive.Downloads" or "GlDrive.Spread")) return null;
        var result = new Dictionary<string, object?>();
        foreach (var property in value.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.GetIndexParameters().Length > 0 || property.GetMethod?.IsPublic != true || typeof(System.Windows.Input.ICommand).IsAssignableFrom(property.PropertyType)) continue;
            if (property.Name.Contains("Password", StringComparison.OrdinalIgnoreCase) || property.Name.Contains("Token", StringComparison.OrdinalIgnoreCase) || property.Name.Contains("ApiKey", StringComparison.OrdinalIgnoreCase) || property.Name is "Irc" or "Plex" or "Player") continue;
            try { result[property.Name] = Project(property.GetValue(value), depth + 1); }
            catch (TargetInvocationException) { result[property.Name] = null; }
        }
        return result;
    }
}
