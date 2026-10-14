using System.Text.Json;

namespace GlDrive.Services.Automation;

public sealed partial class AutomationService
{
    private static IReadOnlyList<AutomationToolDefinition> BuildTools()
    {
        var tools = new List<AutomationToolDefinition>();
        void Add(string name, string description, bool readOnly, bool destructive, params string[] fields)
        {
            var properties = new Dictionary<string, object>();
            var required = new List<string>();
            foreach (var field in fields)
            {
                var parts = field.Split(':', 3);
                var key = parts[0].TrimEnd('?');
                if (!parts[0].EndsWith('?')) required.Add(key);
                var type = parts.Length > 1 ? parts[1] : "string";
                var schema = new Dictionary<string, object>();
                if (type.Contains('|'))
                {
                    schema["type"] = "string";
                    schema["enum"] = type.Split('|');
                }
                else if (type == "strings")
                {
                    schema["type"] = "array";
                    schema["items"] = new { type = "string" };
                    schema["maxItems"] = 100;
                }
                else schema["type"] = type;
                if (parts.Length > 2) schema["description"] = parts[2];
                properties[key] = schema;
            }
            tools.Add(new AutomationToolDefinition(name, description,
                JsonSerializer.SerializeToElement(new { type = "object", properties, required, additionalProperties = false }),
                readOnly, destructive));
        }
        Add("servers.list", "List configured servers, mount/IRC state, credits, and live FTP pool health. Passwords are omitted.", true, false);
        Add("servers.get", "Read a server's live mount state, FTP pool health and site statistics.", true, false, "serverId");
        Add("servers.mount", "Mount a configured server and start its download/search/IRC services.", false, false, "serverId");
        Add("servers.unmount", "Unmount a server and stop its running FTP, download, spread and IRC services.", false, true, "serverId");
        Add("servers.remount", "Stop and recreate a server's live services from the current settings.", false, true, "serverId");
        Add("servers.mount_all", "Mount enabled servers configured to mount on startup, and ensure enabled IRC services are running.", false, false);
        Add("servers.unmount_all", "Unmount all currently mounted servers and stop their services.", false, true);
        Add("servers.cache_clear", "Invalidate the mounted filesystem directory cache.", false, false, "serverId");
        Add("servers.stats_refresh", "Fetch fresh site credits and ratio.", false, false, "serverId");
        Add("servers.pool_reinitialize", "Recover an exhausted main FTP pool using the app's connection recovery policy.", false, false, "serverId");

        Add("ftp.list", "List an FTP directory using the server's normal or CPSV transport. Results are paginated.", true, false, "serverId", "path", "offset?:integer", "limit?:integer");
        Add("ftp.stat", "Read existence, type, size and modified time for an absolute FTP path.", true, false, "serverId", "path");
        Add("ftp.read", "Read an FTP file as UTF-8 text or base64. Maximum 1 MiB; use ftp.download for larger files.", true, false, "serverId", "path", "encoding?:utf8|base64", "maxBytes?:integer");
        Add("ftp.write", "Write small UTF-8 text or base64 files. The complete JSON request must fit within 64 KiB; use ftp.upload for larger files. Overwrite requires overwrite=true.", false, true, "serverId", "path", "content", "encoding?:utf8|base64", "overwrite?:boolean");
        Add("ftp.upload", "Stream a local file to an absolute FTP path. Overwrite requires overwrite=true.", false, true, "serverId", "path", "localPath", "overwrite?:boolean");
        Add("ftp.download", "Stream an FTP file to a local file. Existing files are protected unless overwrite=true. Incomplete output is removed.", false, true, "serverId", "path", "localPath", "overwrite?:boolean");
        Add("ftp.mkdir", "Create an FTP directory and its missing parents.", false, false, "serverId", "path");
        Add("ftp.rename", "Rename or move an FTP file/directory. Existing destinations are rejected.", false, true, "serverId", "path", "destinationPath");
        Add("ftp.delete", "Delete an FTP file or directory. Nonempty directories require recursive=true; the FTP root is protected.", false, true, "serverId", "path", "recursive?:boolean");
        Add("ftp.command", "Execute a single FTP control command, including SITE commands. The borrowed connection is discarded afterward to isolate protocol state. Data-transfer commands are rejected; use ftp tools for data.", false, true, "serverId", "command");

        Add("downloads.list", "Read download queues/history across mounted servers, optionally filtered by server and status.", true, false, "serverId?", "status?:Queued|Downloading|Extracting|Completed|Failed|Cancelled", "offset?:integer", "limit?:integer");
        Add("downloads.enqueue", "Queue an FTP release directory for download. Omit localPath to use the configured category destination.", false, false, "serverId", "path", "releaseName?", "category?", "localPath?", "wishlistItemId?");
        Add("downloads.cancel", "Cancel a queued, downloading or extracting item.", false, true, "serverId", "id");
        Add("downloads.retry", "Retry a failed or cancelled download, retaining partial bytes for resume.", false, false, "serverId", "id");
        Add("downloads.remove", "Remove a finished download from history. Does not delete its local files; cancel active work first.", false, true, "serverId", "id");
        Add("downloads.clear_finished", "Clear completed, failed, cancelled, or all finished download history, leaving local files in place.", false, true, "serverId", "status?:Completed|Failed|Cancelled|all");
        Add("downloads.reorder", "Move a queued download one position up or down in the processing queue.", false, false, "serverId", "id", "direction:up|down");
        Add("search.query", "Search the mounted FTP servers through the app's SITE search and index fallback. Omit serverId to search all mounted servers.", true, false, "query", "serverId?", "limit?:integer");
        Add("search.refresh", "Rebuild one server's FTP search index. May run for several minutes.", false, false, "serverId");

        Add("wishlist.list", "Read the persisted wishlist, including media metadata, quality, watching/paused/completed state and grabbed releases.", true, false, "offset?:integer", "limit?:integer");
        Add("wishlist.upsert", "Add a wishlist item or patch an existing item by item.id. item fields: id, title, type (Movie/TvShow), year, imdbId, tvMazeId, quality (Any/SD/Q720p/Q1080p/Q2160p), status (Watching/Completed/Paused), grabbedReleases, posterUrl, plot, rating, genres, dead. Unspecified existing fields are preserved.", false, false, "item:object");
        Add("wishlist.remove", "Delete a wishlist item by ID.", false, true, "id");
        Add("wishlist.match", "Evaluate a known FTP release against the wishlist and allow the normal configured auto-download behavior.", false, false, "serverId", "category", "releaseName", "path");
        Add("notifications.list", "Read stored release notifications, optionally filtered by server.", true, false, "serverId?", "offset?:integer", "limit?:integer");
        Add("notifications.clear", "Clear the persisted release notification history.", false, true);

        Add("irc.status", "Read the server's IRC state, nickname, channels, topics, members and known conversation targets.", true, false, "serverId");
        Add("irc.history", "Read chronological scrollback for a channel, private-message nickname, or '*' for system messages. Returns the latest limit messages.", true, false, "serverId", "target", "limit?:integer");
        Add("irc.start", "Start IRC for an enabled configured IRC server, independently of FTP mounting.", false, false, "serverId");
        Add("irc.stop", "Disconnect and stop a server's IRC service.", false, true, "serverId");
        Add("irc.reconnect", "Restart the server's IRC service using the saved IRC settings.", false, true, "serverId");
        Add("irc.send", "Send an IRC channel or private message, honoring configured FiSH encryption. This sends a real message.", false, false, "serverId", "target", "text");
        Add("irc.action", "Send a real IRC /me action to a channel or nickname.", false, false, "serverId", "target", "text");
        Add("irc.notice", "Send a real IRC NOTICE to a channel or nickname.", false, false, "serverId", "target", "text");
        Add("irc.join", "Join an IRC channel, optionally supplying its channel password.", false, false, "serverId", "target", "key?");
        Add("irc.part", "Leave an IRC channel with an optional part message.", false, true, "serverId", "target", "text?");
        Add("irc.topic", "Change a channel topic (subject to IRC permissions).", false, true, "serverId", "target", "text");
        Add("irc.raw", "Send one IRC protocol line. This may send messages, change modes, or affect other users; use only for explicitly requested IRC actions.", false, true, "serverId", "line");
        Add("irc.patterns", "Detect release announce patterns from a server's recent IRC traffic.", true, false, "serverId");
        Add("irc.keys_list", "List FiSH targets, modes and timestamps without exposing key material.", true, false, "serverId");
        Add("irc.key_get", "Read one stored FiSH key. This explicitly returns sensitive key material.", true, false, "serverId", "target");
        Add("irc.key_set", "Set a manually supplied FiSH key and ECB/CBC mode for a conversation.", false, true, "serverId", "target", "key", "mode?:ECB|CBC");
        Add("irc.key_remove", "Remove a conversation's FiSH key.", false, true, "serverId", "target");
        Add("irc.key_mode", "Set ECB/CBC mode for an existing FiSH key.", false, false, "serverId", "target", "mode:ECB|CBC");
        Add("irc.key_exchange", "Initiate a real DH1080 key-exchange NOTICE with a nickname.", false, false, "serverId", "target");

        Add("spread.status", "Read active races, queued requests, connected sites, pool health and historical summary.", true, false);
        Add("spread.detail", "Read the full file/site/transfer detail of an active race.", true, false, "id");
        Add("spread.start", "Start or queue a race between at least two connected sites. Omit serverIds to select connected enabled sites with sections.", false, false, "section", "releaseName", "serverIds?:strings", "sourceServerId?", "sourcePath?");
        Add("spread.stop", "Stop an active race.", false, true, "id");
        Add("spread.pause", "Pause an active race.", false, false, "id");
        Add("spread.resume", "Resume a paused race.", false, false, "id");
        Add("spread.queue_remove", "Remove a queued race by exact section and release name. An already started race must be stopped by ID.", false, true, "section", "releaseName");
        Add("spread.fxp", "Copy a file or directory between different connected FTP servers via the app's FXP engine. directory=true recursively copies a directory.", false, true, "sourceServerId", "sourcePath", "destinationServerId", "destinationPath", "directory?:boolean");
        Add("spread.history", "Read persisted race history, newest first.", true, false, "offset?:integer", "limit?:integer");
        Add("spread.blacklist_list", "Read learned per-server section exclusions and their failure evidence.", true, false);
        Add("spread.blacklist_remove", "Remove a learned section exclusion so future races may retry it.", false, true, "serverId", "section");
        Add("spread.blacklist_clear", "Clear all learned section exclusions.", false, true);
        return tools;
    }
}
