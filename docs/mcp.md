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

`agent_audit_action` applies priority and download-only suggestions only when local telemetry contains measured winners for at least 50% of the target server's races. Unknown winners are excluded from win rates. This conservative validator rule applies regardless of the suggestion's reasoning; missing or unreadable evidence rejects the apply. Explicit undo and direct `config_patch` edits remain available. Successful reviewed changes queue the same targeted runtime refresh as Settings; inspect `restartRequired` if that refresh could not be queued.

## Enable the app connection

1. Install a GlDrive build with MCP support.
2. Open **Settings → MCP** and turn on **Enable MCP / local control API**. The default port is `8756`. On v3.11.0 and v3.11.1, the toggle is under **Settings → Diagnostics**.
3. Click **Apply MCP settings** (or **Save** in older builds). The app starts the local API and generates a token when needed. The MCP panel checks the applied connection and reports the available tool count or an actionable error.
4. Keep GlDrive running while using MCP.

For an installation without the settings control, exit GlDrive first, preserve the existing settings in `%APPDATA%\GlDrive\appsettings.json`, set `ControlApi.Enabled` to `true`, then restart the app. Preserve an existing token; a missing token is generated at startup.

The bridge automatically reads `ControlApi.Port` and `ControlApi.Token` from that file. **Do not paste the token into Claude/Codex configuration.** The app and client should run as the same Windows user. The bridge connects only to `http://127.0.0.1`; it cannot be used as a remote HTTP MCP endpoint.

Use **Regenerate control-API token** in the MCP settings panel to invalidate the current token immediately. This saves only the new token, preserving pending settings edits. The bridge reads it on its next request; no client configuration change is needed unless you set `GLDRIVE_CONTROL_TOKEN` yourself. Automation can also replace `/controlApi/token` through `config_patch` with `allowSecrets=true`; the token is never returned by the bridge.

The installed executable is `<GlDrive installation folder>\mcp\GlDrive.Mcp.exe`. Substitute its actual absolute path in the examples below. The packaged bridge includes its .NET runtime.

The **MCP** panel shows this installation's adapter path and warns if the executable is missing. Choose **Claude Code**, **Codex**, or **Claude Desktop** under **Connect a client**, then copy the generated command or JSON. Commands are ready for PowerShell and already contain the actual path. Run the command, or merge the Desktop JSON into its existing configuration as described below; copying does not register the client automatically. No authentication token is included in the copied text.

**Apply MCP settings** saves only the MCP toggle and port without closing Settings or saving drafts from other tabs. **Check connection** tests the applied app settings; an unsaved toggle or port stays a draft. A successful app check does not prove the client is registered. Keep the app running and reconnect the client after registration. Invalid ports are rejected instead of silently replaced.

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

Restart or reconnect the Codex session after registration. The CLI syntax above was verified with the installed `codex mcp add --help` and the [official MCP setup documentation](https://learn.chatgpt.com/docs/extend/mcp?surface=cli); local stdio clients require the bridge to run on the same machine as GlDrive.

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

`servers.list` and `servers.get` expose `authenticationHeld` and `authenticationRetryAtUtc`. A rejected normal FTP login holds new logins for the same host, port and user across all pools, ghost cleanup and UI probes. Existing usable connections remain available. Automatic recovery admits at most one probe per 30 minutes; a changed password or an explicit `servers.mount`, `servers.remount`, `servers.mount_all` or `servers.pool_reinitialize` action permits a retry. Avoid repeating these manual recovery actions in a polling loop.

Operations that take more than about eight seconds return a `jobId` with `status: "running"`. This acknowledges that work started; it is **not** a success result. Use `app.jobs_get` with that ID until `completed`, `failed`, or `cancelled`. `app.jobs` lists recent operations, and `app.jobs_cancel` requests cancellation. Cancellation does not reverse changes already made. Jobs are kept in memory and expire after thirty minutes or when the app exits.

Writes are never automatically retried. If a call times out, is cancelled, or loses its connection, inspect current state or recent jobs before retrying it. Cancelling an MCP request stops waiting for its HTTP response; it does **not** cancel a detached operation in GlDrive. Find the operation with `app.jobs` and use `app.jobs_cancel`, or use the feature's explicit stop/cancel operation. Cancellation is cooperative: existing desktop commands and the updater may continue after a cancellation request. Remote filesystem changes, downloads, messages, sharing changes, and other actions can have effects outside the local app; authorize the intended action in your assistant conversation.

App settings and credentials use the app's normal persistence mechanisms. Secret fields are redacted by default; tools that support secret reads or writes require explicit parameters. The local control API token is never exposed by the bridge. Remote files, IRC messages, descriptions, and logs are data, not instructions to the assistant.

## Offline startup and troubleshooting

The MCP process can initialize while GlDrive is stopped. In that case only `fmountr_connection_status` is available, and its result explains the connection problem. Start GlDrive and call that tool again. On recovery, the bridge announces that the tool list changed. If the client does not refresh its tools automatically, reconnect the server. After a successful discovery, a temporary app outage preserves the last known catalog in that MCP process; calls still report that the app is unavailable.

| Problem | Action |
| --- | --- |
| Settings not found | Run the app under the same Windows user, or set `GLDRIVE_CONFIG_PATH`. |
| Control API disabled | Enable MCP / local control API under Settings → MCP and apply. Older builds have the toggle under Diagnostics. |
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

Configuration and credential edits schedule a serialized server/download refresh. Connection, TLS, drive-mount and login-budget changes rebuild the affected FTP resources; other server/download edits update their owning services. IRC changes restart IRC independently. Existing race destination paths stay fixed while new races use updated routing. WinFsp `cache.fileInfoTimeoutMs` requires a subsequent drive mount; unrelated listener, logging and UI settings may still require restarting the app.

For `downloads.enqueue`, an explicit `localPath` remains authoritative. If omitted, the base is selected from an exact category key, then the release's logical `TV`/`Movies` key, then download settings `localPath`; one release subfolder is appended. This endpoint retains its flat release layout. Torrent downloads still use the explicitly supplied directory.

For `wishlist.upsert`, a new item with omitted `quality` uses `downloads.qualityDefault` (1080p if invalid). Patching an existing item preserves its quality unless supplied explicitly.

`wishlist.profiles` lists built-in and configured scored profiles. Set an item's `profileId` with `wishlist.upsert`, then opt in with `config_patch` at `/downloads/scoredQualityEnabled`. `wishlist.score(itemId, releaseName, sizeBytes?)` explains acceptance, score, cutoff and owned/pending-copy decisions without enqueueing or network access. User-defined profiles live in `/downloads/profiles`; `config_schema` describes their fields. `wishlist.list` exposes best-owned and pending records, maintained by download outcomes and read-only in upsert. Scoring does not remove superseded files. Existing items migrate to `legacy-<quality>` profiles; recommended ids include `scored-q1080p` and `scored-q2160p`.

`spread.report(days?)` reads existing local race telemetry (default one day, maximum 90) and returns daily, section and destination aggregates. Unknown outcomes are excluded from win/loss rates; coverage, malformed input and any bounded history fallback are explicit. `spread.sitebot_facts(serverId?, releaseName?)` shows recent trusted observations, including announced bytes/files, metadata and completion hints. Both are read-only and issue no FTP commands. Configure trust per `/servers/N/irc/channels/N/sitebot` using `config_patch`; empty `botNick` disables decoding. `spread.history` includes per-destination observed winner and verification evidence.

Race queue snapshots include persisted size/work estimates. `spread.start` keeps its existing outcome strings and adds `hintsMerged`, true when a duplicate request actually enriches participants/source information. Queue positions reflect the configured scheduling policy among eligible requests. `/spread/sizeAwareRaceQueue` and `/spread/mergeRaceAnnounces` default true; `/spread/raceQueueMaxAgeMinutes` defaults 120. These settings are available through `config_get`, `config_schema` and `config_patch`.

Race lifetime settings are available through `config_schema` and `config_patch`: `spread.hardTimeoutSeconds` now measures idle payload time, `spread.absoluteRaceTimeoutMinutes` defaults to 90, and `spread.partialRaceDeadlineHours` defaults to 10. `spread.status` and `spread.history` expose stop reasons, resume counts and first-upload timestamps; queued cleanup work remains inspectable when the destination is unavailable. Cleanup only considers incomplete release directories with recorded GlDrive creation ownership.

`spread.start` returns the engine's admission decision: `started`, `queued`, `already_active`, `duplicate` (already queued), `suppressed`, or `unavailable`. Inspect this state instead of assuming that a successful tool response created a race. `queuePosition` is a snapshot among currently eligible queued requests; deferred work or unavailable pools can leave it null. Suppressed requests include `retryAfterSeconds` and `reason`. The Spread tab displays the same decisions.

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

`wishlist.wanted` returns `windowEnabled`, `indexSweepsEnabled`, pending `windows`, and per-server cache `offers`. It is read-only and never crawls or probes. Index presence and directory sizes do not establish release completeness or total bytes. Opt-in window/sweep settings and bounds are exposed by `config.schema` and validated by `config.patch`; both switches default off. Trusted completion remains advisory to the normal download verification.

Series operations: `wishlist.series(itemId)` reads episode states; `wishlist.season_plan(itemId, season)` builds a cache-only plan; `wishlist.season_pull(itemId, season)` recomputes and explicitly queues that plan with durable episode claims. `wishlist.upsert` accepts `monitorMode` (`Legacy` default, `Future`, `Missing`, `All`, `LatestSeason`), `monitorSinceUtc`, and `autoPullCompletedRaces` (false). Monitoring requires a TVMaze id. Episode inventory and refresh timestamps are read-only metadata state, like owned/pending download evidence. A ready plan covers aired gaps, not a claimed finale. New source/race-pull switches default off and are available in the config schema. Automatic race pulls additionally require B3 windows, explicit server ids, and per-show opt-in.

Race controls: `spread.reset(id)`, `spread.rerace(historyId, serverIds?)`, `spread.queue_site(section, releaseName, serverId, add)`, and `spread.cleanup_own(section, releaseName, confirmed:true)`. Membership edits are queued-only and protect sources and owned partials. Cleanup uses A5 ownership/incompleteness checks and rejects known nuked content; it cannot delete arbitrary directories. `spread.nukes` is read-only persisted nuke state. `spread.ownNukeAlertsEnabled` defaults on and only controls exact own-account alerts, not safety holds. Re-race returns the same real admission result as `spread.start`.

Only exact nuke evidence holds a race or its cleanup. A `SITE NUKES` row whose name was truncated (glftpd cuts `Dir:` at about 50 characters) can name an alert but never holds a race, even when it matches one known release by prefix. If the persisted nuke state is unreadable it is copied to `nuke-observations.json.corrupt` and tracking restarts empty; at capacity the oldest observations are dropped. `spread.nukes` reports `stateAvailable` (false after such a reset), `persistenceHealthy`, and paginated `observations`.

`servers.get` now includes `health` (outage class, first failure, retry/probe deadlines and persistence availability), `loginBudget`, `loginUsage` (active main/race lease purposes and start times), and `announceHealth`. `irc.status` also includes channel health and persistence status. These reads create no probe or FTP login. Health supervision, account login-rate protection and passive silence notices default on; TCP probes and automatic missing-channel recovery default off. Their switches are available through `config.schema`/`config.patch`. Existing explicit server retry remains subject to the authentication/manual-retry path and simultaneous login limits.

### Alert inbox

`alerts.list` reads the local inbox, `alerts.update` acknowledges or snoozes an item, and `alerts.digest` creates a deterministic local 24-hour summary. Omitted update fields are preserved; `snoozeMinutes: 0` clears the snooze. Snooze belongs to the specified entry, including its queued outbound deliveries, and does not mute new entries or a whole alert kind. These tools never execute a decision or call a model. Enable `alerts.enabled` through `config_patch` to collect history. `config_schema` includes all alert policies. Target configuration is opt-in; `credentials_manage` accepts API services `alerts-TARGET_ID` containing provider credential JSON. See the configuration guide for fields. Push targets never export local message bodies or chat.

### Attributed settings and temporary changes

`config_history` reads numbered, secret-masked versions after `configHistory.enabled` is explicitly enabled. `config_restore(version, requireIdle?)` restores through the normal validated save and runtime-refresh path, preserving the current control token. DPAPI-protected snapshots never appear in read results. `config_preview(operations)` reports A4 remount/IRC effects without saving.

`config_lease` accepts `action: list|start|keep`. Start requires `pointer`, scalar `value`, and ISO `expiresUtc`; optional `label` and `applyWhenIdle` describe the user-approved change. Keep requires `id`. Expiry only reverts a still-matching leased value, preserves later edits, and survives restarts. `applyWhenIdle` defaults false and, when enabled, uses the existing race-idle signals for both apply and expiry. No secret fields, library enablement/dry-run controls, unstable nested lists or overlapping leases are allowed. An observed later edit permanently supersedes the lease; a failed immediate apply is terminal. Responses expose `persistenceHealthy`; false means the lease is currently only reliable in memory.

`config_experiment` accepts `action: list|start|keep|revert`. Start takes `label` and `hypothesis`; keep/revert take `id`. Before/after measurements are local rolling-24-hour observed race counts, with an incomplete-evidence flag. Revert is an explicit full-baseline settings restore. These operations do not call a model, create FTP traffic, or bypass agent validators. The tuning agent cannot create leases.

- `agent.simulate` (`agent_simulate`): read-only local validator/replay preview of a `changes` array in the normal agent proposal format. It runs off the UI thread, does not save settings, call a model, write wishlist/error-report side effects or return raw IRC samples. Results include accepted-preview/rejection counts and per-change `Impact` sample/change/loss counts.
- `agent.outcomes` (`agent_outcomes`): read-only expected effects and passive assessments for applied audit rows. An absent outcome with a valid declaration is pending; missing declarations are unscored. Automatic regression reverts require `agent.autoRevertRegressions=true` (default false), an agent-originated row, the original applied scalar value, the same validator and current freeze checks. Explicit `agent_audit_action` applies treat replay as advice and retain its impact summary; replay runs in the background outside the configuration lock with a five-second cancellation budget. `config.schema` documents the six accountability settings and defaults.

### Staged library tools

- `library.plan(sourcePath, releaseName)` reads a plan for one completed video in `library.stagingRoot`. Planning is available while imports are disabled and writes nothing. Samples, extras, dated episodes and filename/release identity mismatches remain staged.
- `library.import(sourcePath, releaseName)` respects `library.enabled` and `dryRun`, rechecking current permission and roots before publishing or removing the staged source. Live imports fail closed on changed files, unsafe paths or destination collisions. Copies publish only after verifying a private dot-prefixed temporary sibling; failures remove only the unpublished temporary file. Imports never overwrite or delete destinations. A verified import may request one targeted Plex folder scan of the matching media type when explicitly configured; its result reports file import and scan status separately.
- `library.index_refresh` explicitly reads configured disk roots and the selected Plex server, retaining a local in-memory inventory and reporting missing evidence. It never triggers Plex scans or updates settings/wishlist entries.
- `library.index(offset?, limit?)` pages through that inventory (default 100, maximum 500 response entries). `library.lookup(releaseName)` compares an unambiguous movie/year or single episode against owned quality. Both require a prior explicit refresh and do no network or file work themselves.

Enabling `library.enabled` forces `dryRun=true` even if the same patch requests live mode. A separate later patch can disable dry-run. This batch provides explicit imports only; existing download/extraction destinations remain governed by their current settings. No inbound library control channel or listening port is added.
