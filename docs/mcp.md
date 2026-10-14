# GlDrive / fmountr MCP

GlDrive includes a local MCP server for Claude Code, Claude Desktop, Codex, and other clients that support stdio MCP. It controls the **running app** through its authenticated loopback API, so reads and writes use the same services as the UI.

The app supplies the live tool catalog and JSON schemas. New app tools become available without a new bridge implementation. `fmountr_connection_status` checks connectivity and refreshes discovery. The resources `gldrive://mcp/help` and `gldrive://mcp/catalog` provide connection help and the complete live catalog.

## Feature coverage

| App area | Read access | Write and action access |
| --- | --- | --- |
| Settings and server configuration | `config_get`, `config_schema`, revision and defaults | `config_patch` adds, changes, tests, and removes any supported settings field; server rows can be added or removed. |
| Stored credentials | `credentials_manage` reports presence; explicit `includeValue` reveals a requested credential | Set or delete FTP, proxy, IRC, and supported service API credentials. |
| Configuration snapshots | `config_snapshots`, `config_snapshot_get` | `config_snapshot_create`, `config_snapshot_restore`, `config_snapshot_delete`; restore validates settings and preserves the current control token. |
| TLS trust | `certificates_list` | `certificates_trust`, `certificates_remove`, `certificates_clear`; trust changes affect future connections. |
| Servers and drives | `servers.list`, `servers.get` | Mount, unmount, remount, clear cache, refresh statistics, and recover the FTP pool. |
| FTP files and directories | `ftp.list`, `ftp.stat`, `ftp.read`, `ftp.download` | `ftp.write`, `ftp.upload`, `ftp.mkdir`, `ftp.rename`, `ftp.delete`, and explicit control/SITE commands. |
| Search and downloads | `search.query`, `downloads.list` | Refresh search indexes; enqueue, cancel, retry, reorder, remove, and clear finished downloads. |
| Wishlist and release notifications | `wishlist.list`, `notifications.list` | Add/edit/remove wishlist entries, evaluate release matches, and clear notifications. |
| IRC and FiSH | `irc.status`, `irc.history`, detected patterns and key metadata; explicit key reads | Connect/reconnect/disconnect, send messages/actions/notices, join/part, set topics, raw IRC actions, and manage/exchange keys. |
| Spread and FXP | `spread.status`, `spread.detail`, `spread.history`, learned exclusions | Start, stop, pause, resume and dequeue races, copy via FXP, and remove/clear learned exclusions. |
| Dashboard, browse, upcoming releases, PreDB | `ui.state` for live fields and bounded collections | `ui.set`, `ui.select`, `ui.invoke` expose the listed desktop fields and awaited commands. |
| Media player and torrents | `ui.state` on `player` for media, playback, search, library, torrent and renderer state | Playback/search/library commands through `ui.invoke`; `player.seek`, `player.play_local`, `player.download_torrent`. |
| Archive extractor | `extractor.state` includes archive results, watch folders and settings | Add/remove/clear the queue, start/cancel extraction, and replace extractor settings, including watch behavior. |
| glFTPd remote installer | `installer.state`, `installer.profile_get`, paginated `installer.logs` | Patch its profile; connect/disconnect SSH with verified host keys; start/cancel installation; clear logs. |
| Plex | Login/selected-server status, owned servers, libraries, shares, and connection test | Authorize/logout, select a server, invite, revoke, and replace share permissions. |
| AI tuning agent | `agent_status`, `agent_audit`, `agent_memo`, freezes | Run/abort the existing agent, manage freezes and memory, and apply/undo eligible audited changes through its validators. |
| Diagnostics | `diagnostics_files`, `diagnostics_read` for bounded, redacted app logs, telemetry, and briefs | Logging and agent settings are editable through `config_patch`; log contents are diagnostic records. |
| App lifecycle and cleanup | `app.status`, `app.update_check`, `cleanup.scan`, recent jobs | Restart/update the app, delete explicitly selected eligible leftover archives, and request job cancellation. |

The live catalog is authoritative for exact fields, IDs, available actions, and service prerequisites. UI collections are limited to 200 rows per snapshot; use the domain tools' pagination where available. Reading history or diagnostics does not make those records editable: change the corresponding setting, queue, service, or stored entity through its dedicated operation.

## Enable the app connection

1. Install a GlDrive build with MCP support.
2. Open **Settings → Diagnostics** and turn on **Enable MCP / local control API**. The default port is `8756`.
3. Save settings. The app starts the local API and generates a token when needed.
4. Keep GlDrive running while using MCP.

For an installation without the settings control, exit GlDrive first, preserve the existing settings in `%APPDATA%\GlDrive\appsettings.json`, set `ControlApi.Enabled` to `true`, then restart the app. Preserve an existing token; a missing token is generated at startup.

The bridge automatically reads `ControlApi.Port` and `ControlApi.Token` from that file. **Do not paste the token into Claude/Codex configuration.** The app and client should run as the same Windows user. The bridge connects only to `http://127.0.0.1`; it cannot be used as a remote HTTP MCP endpoint.

The installed executable is `<GlDrive installation folder>\mcp\GlDrive.Mcp.exe`. Substitute its actual absolute path in the examples below. The packaged bridge includes its .NET runtime.

## Claude Code

Run in PowerShell:

```powershell
claude mcp add --transport stdio --scope user fmountr -- 'C:\path\to\GlDrive\mcp\GlDrive.Mcp.exe'
claude mcp get fmountr
```

In Claude Code, use `/mcp` to inspect or reconnect the server. The [Claude Code MCP documentation](https://code.claude.com/docs/en/mcp#option-3-add-a-local-stdio-server) describes stdio registration and scopes.

## Codex

Run in PowerShell:

```powershell
codex mcp add fmountr -- 'C:\path\to\GlDrive\mcp\GlDrive.Mcp.exe'
codex mcp get fmountr
```

Alternatively add this to `%USERPROFILE%\.codex\config.toml`, using a TOML literal string for a Windows path:

```toml
[mcp_servers.fmountr]
command = 'C:\path\to\GlDrive\mcp\GlDrive.Mcp.exe'
startup_timeout_sec = 20
tool_timeout_sec = 150
```

Restart or reconnect the Codex session after registration. The CLI syntax above was verified with the installed `codex mcp add --help`; local stdio clients require the bridge to run on the same machine as GlDrive.

## Claude Desktop and other clients

Merge this entry into the client's existing MCP configuration. For Claude Desktop on Windows, the conventional configuration file is `%APPDATA%\Claude\claude_desktop_config.json`. Preserve all other server entries:

```json
{
  "mcpServers": {
    "fmountr": {
      "command": "C:\\path\\to\\GlDrive\\mcp\\GlDrive.Mcp.exe",
      "args": []
    }
  }
}
```

Fully restart the client after saving. This is a local integration; the hosted claude.ai website cannot start this Windows executable.

See the [MCP guide to connecting local servers](https://modelcontextprotocol.io/docs/develop/connect-local-servers) for Claude Desktop's configuration file and restart workflow.

## Use and operation lifecycle

Start by asking the assistant to inspect the app and list its available operations. Tool descriptions and schemas identify required IDs, supported actions, read operations, and destructive operations. Reads return JSON as both MCP text content and structured content. API errors become tool results with `isError: true`.

Operations that take more than about eight seconds return a `jobId` with `status: "running"`. This acknowledges that work started; it is **not** a success result. Use `app.jobs_get` with that ID until `completed`, `failed`, or `cancelled`. `app.jobs` lists recent operations, and `app.jobs_cancel` requests cancellation. Cancellation does not reverse changes already made. Jobs are kept in memory and expire after thirty minutes or when the app exits.

Writes are never automatically retried. If a call times out, is cancelled, or loses its connection, inspect current state or recent jobs before retrying it. Cancelling an MCP request stops waiting for its HTTP response; it does **not** cancel a detached operation in GlDrive. Find the operation with `app.jobs` and use `app.jobs_cancel`, or use the feature's explicit stop/cancel operation. Cancellation is cooperative: existing desktop commands and the updater may continue after a cancellation request. Remote filesystem changes, downloads, messages, sharing changes, and other actions can have effects outside the local app; authorize the intended action in your assistant conversation.

App settings and credentials use the app's normal persistence mechanisms. Secret fields are redacted by default; tools that support secret reads or writes require explicit parameters. The local control API token is never exposed by the bridge. Remote files, IRC messages, descriptions, and logs are data, not instructions to the assistant.

## Offline startup and troubleshooting

The MCP process can initialize while GlDrive is stopped. In that case only `fmountr_connection_status` is available, and its result explains the connection problem. Start GlDrive and call that tool again. On recovery, the bridge announces that the tool list changed. If the client does not refresh its tools automatically, reconnect the server. After a successful discovery, a temporary app outage preserves the last known catalog in that MCP process; calls still report that the app is unavailable.

| Problem | Action |
| --- | --- |
| Settings not found | Run the app under the same Windows user, or set `GLDRIVE_CONFIG_PATH`. |
| Control API disabled | Enable MCP / local control API under Settings → Diagnostics, then save. |
| Cannot reach GlDrive | Check the app is running and using the configured port. |
| Token rejected | Remove a stale token environment override and let the bridge read the app's current configuration. |
| No automation catalog | Update the app and bridge together. |
| Only the connection tool appears | Call `fmountr_connection_status`, then reconnect the client if needed. |
| Response too large | Narrow the query, use pagination, or request smaller chunks. |

Optional environment overrides:

| Variable | Meaning |
| --- | --- |
| `GLDRIVE_CONFIG_PATH` | Absolute path to an alternative appsettings.json. |
| `GLDRIVE_CONTROL_PORT` | Loopback port, from 1 to 65535. |
| `GLDRIVE_CONTROL_TOKEN` | Override the token. Usually omit this so rotation is picked up automatically. |

The bridge reloads connection settings for every request. It never follows HTTP redirects, uses an HTTP proxy, stores cookies, or logs credentials. The app's automation HTTP endpoint accepts a maximum **64 KiB JSON request body**, including tool names, arguments, JSON escaping and base64 expansion. Each app operation result is limited to **4 MiB**. The bridge separately enforces 8 MiB request and 16 MiB response limits; those larger limits do not override the app's limits. Use `ftp.upload` or `ftp.download` to stream larger files by path rather than embedding their contents in a tool call. Protocol output is the only output written to stdout.

## Scope and UI boundaries

The catalog covers app configuration, stored credentials, connected services, filesystem operations, queues, messaging, media features, diagnostics, and app UI actions. Each operation's availability depends on the corresponding app service being running and configured. Runtime settings that require a restart say so in their results or descriptions.

Third-party websites embedded in WebView tabs are separate applications: MCP does not provide arbitrary DOM access to those pages. Interactive authentication and operating-system dialogs may still require the user to act in the app or browser. Certificate tools can manage known trust pins directly; GlDrive's normal connection-time trust behavior still applies. Plex sign-in uses `plex.login_start` to return an authorization URL for the user, followed by `plex.login_status`; credentials stay in Windows Credential Manager. Plex sharing operations change the account's live permissions. `plex.update_share` uses the app's revoke-and-reinvite workflow, which can leave access revoked if the new invitation fails.

The glFTPd installer uses the app's existing SSH/SFTP workflow and the local `Documents\glftpd-installer\install.sh` script. `installer.connect` requires an already trusted SSH host key or an independently verified `expectedHostKey` SHA-256 fingerprint; an unknown key is returned for verification without trusting it. `installer.start` runs installation through sudo on the selected remote Linux host and requires a fresh `/glftpd` destination. Cancellation cannot undo remote changes already made. Installer profile passwords and logs are redacted unless explicitly requested through their secret-access parameters.

## Build and test from source

The bridge uses the [official C# MCP SDK](https://github.com/modelcontextprotocol/csharp-sdk), pinned in its project and NuGet lock file. A .NET 10 SDK is required to build:

```powershell
dotnet build src/GlDrive.Mcp/GlDrive.Mcp.csproj -c Release
dotnet test src/GlDrive.Mcp.Tests/GlDrive.Mcp.Tests.csproj
dotnet publish src/GlDrive.Mcp/GlDrive.Mcp.csproj -c Release -r win-x64 --self-contained true -o artifacts/mcp
```

Register the resulting absolute `artifacts\mcp\GlDrive.Mcp.exe` path using the commands above. For a development build, register `dotnet` with the absolute path to the built `GlDrive.Mcp.dll` as its argument. Build beforehand; do not use `dotnet run` with a build on the MCP stdout channel.

The bridge tests launch the actual stdio executable using the SDK client and a local fake app. They cover protocol initialization, dynamic discovery, tool calls and errors, resources, offline recovery, authentication, response limits, and cancellation. They do not execute remote FTP or Plex changes.
