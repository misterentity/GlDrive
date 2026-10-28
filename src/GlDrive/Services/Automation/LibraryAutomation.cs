using System.IO;
using System.Text.Json;
using GlDrive.Config;
using GlDrive.Library;
using GlDrive.Plex;
using GlDrive.Util;

namespace GlDrive.Services.Automation;

/// <summary>Explicit, local staged imports and read-only inventory. No download or wishlist mutations.</summary>
public sealed class LibraryAutomation : IAutomationModule
{
    private readonly AppConfig _config;
    private readonly Func<PlexConfig, PlexClient?> _plex;
    private readonly SafeLibraryImporter _importer;
    private readonly SemaphoreSlim _imports = new(1, 1);
    private readonly LibraryIndex _libraryIndex = new();
    private sealed record Inventory(LibraryIndexSnapshot Snapshot, string PlexStatus);
    private Inventory? _inventory;

    public LibraryAutomation(AppConfig config, Func<PlexConfig, PlexClient?>? plex = null, SafeLibraryImporter? importer = null)
    {
        _config = config;
        _plex = plex ?? CreatePlex;
        _importer = importer ?? new SafeLibraryImporter();
    }

    public IReadOnlyList<AutomationToolDefinition> Tools { get; } =
    [
        AutomationToolDefinition.Create("library.plan", "Read-only import plan for one completed video already in the configured staging root. Requires an unambiguous movie with year or single SxxExx episode; never creates folders or changes files.",
            FileArguments, readOnly: true),
        AutomationToolDefinition.Create("library.import", "Import one explicitly selected staged video. Disabled by default; first enable forces dry-run. Live imports never replace destinations and remove the staged source only through verified rename/copy handling. Does not download or change wishlist ownership.",
            FileArguments, destructive: true),
        AutomationToolDefinition.Create("library.index_refresh", "Explicitly refresh a read-only inventory of configured movie/TV folders and the selected Plex server. No periodic polling, library scans or wishlist edits. Unavailable Plex/disk evidence is marked incomplete.",
            new { type = "object", properties = new { }, additionalProperties = false }, readOnly: true),
        AutomationToolDefinition.Create("library.index", "Read a page of the last local library inventory. Call library.index_refresh first; no file or network work.",
            new { type = "object", properties = new { offset = new { type = "integer", minimum = 0 }, limit = new { type = "integer", minimum = 1, maximum = 500 } }, additionalProperties = false }, readOnly: true),
        AutomationToolDefinition.Create("library.lookup", "Compare a movie/year or single episode release against the last read-only inventory. Reports owned quality without suppressing downloads or modifying wishlist items.",
            new { type = "object", properties = new { releaseName = new { type = "string", minLength = 1, maxLength = 512 } }, required = new[] { "releaseName" }, additionalProperties = false }, readOnly: true)
    ];

    private static object FileArguments => new
    {
        type = "object",
        properties = new { sourcePath = new { type = "string", minLength = 1, maxLength = 32767 }, releaseName = new { type = "string", minLength = 1, maxLength = 512 } },
        required = new[] { "sourcePath", "releaseName" }, additionalProperties = false
    };

    public async Task<object?> ExecuteAsync(string operation, JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (operation == "library.index")
        {
            var inventory = Volatile.Read(ref _inventory) ?? throw new InvalidOperationException("Refresh the library index first.");
            var snapshot = inventory.Snapshot;
            var offset = Math.Max(0, ConfigurationAutomation.Integer(arguments, "offset", 0));
            var limit = Math.Clamp(ConfigurationAutomation.Integer(arguments, "limit", 100), 1, 500);
            return new { entries = snapshot.Entries.Skip(offset).Take(limit).ToArray(), total = snapshot.Entries.Count, offset, plex = inventory.PlexStatus, snapshot.Complete, snapshot.RefreshedAtUtc };
        }
        if (operation == "library.lookup")
        {
            var inventory = Volatile.Read(ref _inventory) ?? throw new InvalidOperationException("Refresh the library index first.");
            var snapshot = inventory.Snapshot;
            var wanted = LibraryPlanner.TryIdentity(AutomationService.Required(arguments, "releaseName", 512))
                ?? throw new ArgumentException("A movie with year or single episode identity is required.");
            return new { comparison = LibraryIndex.Lookup(snapshot, wanted), plex = inventory.PlexStatus, snapshot.Complete, snapshot.RefreshedAtUtc };
        }

        var config = Snapshot();
        if (operation == "library.index_refresh") return await Refresh(config, cancellationToken);
        if (operation is not ("library.plan" or "library.import")) throw new ArgumentException("Unknown library operation");
        var source = AutomationService.Required(arguments, "sourcePath", 32767);
        var release = AutomationService.Required(arguments, "releaseName", 512);
        if (operation == "library.plan") return LibraryPlanner.Plan(config.Library, source, release);

        await _imports.WaitAsync(cancellationToken);
        try
        {
            // A queued request sees the latest enablement/roots, not stale settings from before waiting.
            config = Snapshot();
            var plan = LibraryPlanner.Plan(config.Library, source, release);
            var result = await _importer.ImportAsync(plan, config.Library, cancellationToken, () => Snapshot().Library);
            var scan = "disabled";
            if (result.Verified && config.Library.ScanPlexAfterImport)
            {
                try { scan = await Scan(config.Plex, Path.GetDirectoryName(result.DestinationPath)!, plan.Identity.Kind, cancellationToken); }
                catch (OperationCanceledException) { scan = "cancelled-after-import"; }
                catch (Exception ex)
                {
                    Serilog.Log.Debug(ex, "Targeted Plex scan failed after a verified local import");
                    scan = "failed-after-import";
                }
            }
            return new { import = result, plexScan = scan };
        }
        finally { _imports.Release(); }
    }

    private AppConfig Snapshot()
    {
        AppConfig result = null!;
        SecureFile.WithPathLock(ConfigManager.ConfigPath, () =>
            result = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(_config))!);
        return result;
    }

    private async Task<object> Refresh(AppConfig config, CancellationToken ct)
    {
        var items = new List<PlexLibraryItem>();
        var plexStatus = "unconfigured";
        try
        {
            using var client = _plex(config.Plex);
            if (client != null)
            {
                plexStatus = "complete";
                var sections = await client.GetLibrarySectionsAsync(config.Plex.ServerUri, ct);
                foreach (var section in sections.Where(s => s.Type is "movie" or "show"))
                    items.AddRange(await client.GetLibraryItemsAsync(config.Plex.ServerUri, section, ct));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "Read-only Plex library inventory is incomplete");
            plexStatus = "incomplete";
        }
        var diskSnapshot = await Task.Run(() => _libraryIndex.Refresh(
            new[] { config.Library.MovieRoot, config.Library.TvRoot }.Where(p => !string.IsNullOrWhiteSpace(p)), items, ct), ct);
        var snapshot = diskSnapshot with { Complete = diskSnapshot.Complete && plexStatus == "complete" };
        Volatile.Write(ref _inventory, new(snapshot, plexStatus));
        return new { total = snapshot.Entries.Count, plex = plexStatus, snapshot.Complete, snapshot.ScanIssues, snapshot.ScannedRoots, snapshot.RefreshedAtUtc };
    }

    private async Task<string> Scan(PlexConfig config, string importedFolder, string mediaKind, CancellationToken ct)
    {
        using var client = _plex(config);
        if (client == null) return "unconfigured";
        var sections = await client.GetLibrarySectionsAsync(config.ServerUri, ct);
        var target = PlexClient.FindImportedFolderTarget(sections, importedFolder, mediaKind);
        if (target == null) return "unmapped-or-ambiguous";
        await client.ScanImportedFolderAsync(config.ServerUri, target.Value.SectionId, target.Value.Root, importedFolder, mediaKind, ct);
        return "requested";
    }

    private static PlexClient? CreatePlex(PlexConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.ServerUri)) return null;
        var token = CredentialStore.GetApiKey("plex");
        if (string.IsNullOrEmpty(token)) return null;
        return new PlexClient(string.IsNullOrWhiteSpace(config.ClientIdentifier) ? "gldrive-library-readonly" : config.ClientIdentifier,
            "GlDrive", UpdateChecker.CurrentVersion.ToString(), token);
    }
}
