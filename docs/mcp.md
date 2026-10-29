# GlDrive / fmountr MCP

GlDrive includes a local MCP server for Claude Code, Claude Desktop, Codex, and other clients that support stdio MCP. It controls the **running app** through its authenticated loopback API, so reads and writes use the same services as the UI.

The app supplies the live tool catalog and JSON schemas. New app tools become available without a new bridge implementation. `fmountr_connection_status` checks connectivity and refreshes discovery. The resources `gldrive://mcp/help` and `gldrive://mcp/catalog` provide connection help and the complete live catalog.

The existing token retains full capabilities. Optional named tokens, local approval for dangerous operations, private operation jobs and the bounded call ledger are described in [automation access](automation-security.md). Read-only calls and ordinary UI actions do not prompt. Secret or destructive calls can return `awaiting-approval`; poll the resulting job after local review.

Safe mode and offline recovery do not start the control API. [Crash recovery and update probation](crash-recovery.md) describe their local controls and the optional `recovery` settings. Rollback is available only through local offline UI.

## Feature coverage

| App area | Read access | Write and action access |
| --- | --- | --- |
| Settings and server configuration | `config_get`, `config_schema`, revision and defaults | `config_patch` adds, changes, tests, and removes any supported settings field; server rows can be added or removed. |
| Stored credentials | `credentials_manage` reports presence; explicit `includeValue` reveals a requested credential | Set or delete FTP, proxy, IRC, and supported service API credentials. |
| Configuration snapshots | `config_snapshots`, `config_snapshot_get` | `config_snapshot_create`, `config_snapshot_restore`, `config_snapshot_delete`; restore validates settings and preserves the current control token. |
| TLS trust | `certificates_list` includes pending certificate decisions | Read-only. Certificate changes require an explicit local UI decision; pin mutation tools are unavailable. See [certificate decisions](certificate-decisions.md). |
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

Third-party websites embedded in WebView tabs are separate applications: MCP does not provide arbitrary DOM access to those pages. Interactive authentication and operating-system dialogs may still require the user to act in the app or browser. Certificate tools only list pins and pending decisions. Trust changes require an explicit local action in Settings → General → Review Certificate Changes. Plex sign-in uses `plex.login_start` to return an authorization URL for the user, followed by `plex.login_status`; credentials stay in Windows Credential Manager. Plex sharing operations change the account's live permissions. `plex.update_share` uses the app's revoke-and-reinvite workflow, which can leave access revoked if the new invitation fails.

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
- `library.index_refresh` explicitly reads configured disk roots and the selected Plex server, retaining a local in-memory inventory and reporting missing evidence. It never triggers Plex scans or updates settings. When Plex Watchlist sync is explicitly enabled, verified ownership can complete imported wishlist entries; this operation is therefore advertised as mutating local state.
- `library.index(offset?, limit?)` pages through that inventory (default 100, maximum 500 response entries). `library.lookup(releaseName)` compares an unambiguous movie/year or single episode against owned quality. Both require a prior explicit refresh and do no network or file work themselves.

Enabling `library.enabled` forces `dryRun=true` even if the same patch requests live mode. A separate later patch can disable dry-run. This batch provides explicit imports only; existing download/extraction destinations remain governed by their current settings. No inbound library control channel or listening port is added.

## Hook outcomes

`hooks.status` is read-only and returns recent executable-hook outcomes, refusal reasons and the number of evicted pending events. No MCP tool launches or edits executable hooks. `config_patch`, including parent-object replacements and `allowSecrets: true`, rejects changes to `/hooks`; history restores preserve the current definitions and leases cannot change them. Invalid hook drafts do not prevent unrelated settings edits or lease reverts. Configure local scripts by deliberate `appsettings.json` edits and restart. See the configuration guide for the static argument-list and environment/stdin contract.

Optional post-event HTTPS webhooks use `alerts.targets` with `kind: "webhook"` and an explicit `hookEvents` array (`download.complete`, `download.failed`, `race.finished`, `wishlist.grab`, `nuke`). Targets and release-name inclusion default off. Credentials use the existing `credentials_manage` service `alerts-TARGET_ID`; payloads reuse alert redaction and bounded per-target delivery, and never include local filesystem paths.

Targeted SFV repair is configured through `config_patch` with `downloads.sfvRepairEnabled` (default false) and `downloads.sfvRepairMaxAttempts` (default 2, range 1–2). `downloads.list` includes `sfvIntegrityHold` and `sfvRepairs` (`localPath`, `attempts`, `pending`) for troubleshooting. `downloads.retry` preserves the repair budget; it cannot authorize overwriting an unowned file or bypass an SFV/extraction verdict. No separate remote repair command or FTP pool is added.

## Plex Watchlist tools

- `plex.watchlist_status` reads `enabled`, `intervalMinutes`, and the last sync result without provider traffic.
- `plex.watchlist_sync` runs the opted-in importer immediately. Enable `plex.watchlistSyncEnabled` first; the default is false. Provider operations are GET-only and reuse the saved Plex token. The result reports status and added/existing/completed/skipped counts. Offline or malformed provider responses preserve local entries; this tool never writes to Plex.

Imported rows exposed by `wishlist.list` retain `source`, `plexWatchlistId`, and available `imdbId`, `tmdbId`, `tvdbId`, and `tvMazeId`. `source`, `plexWatchlistId`, `watchlistSeriesEnded` and `watchlistMetadataCheckedAtUtc` are importer-maintained and read-only in `wishlist.upsert`. Normal title/quality/profile/status edits remain available and survive subsequent syncs. `wishlist.remove` retains an imported row as inactive (`retainedInactive: true`) to prevent later reimport; remove it from Plex Watchlist separately if desired. `wishlist.upsert` with `dead: false` and status Watching re-enables it. The tuning agent cannot prune watchlist-sourced rows. See the configuration guide for identity, cutoff and TV-completion rules.

`wishlist.calendar_refresh` takes `itemId` for one wanted movie and performs an explicit TMDB lookup using the existing saved key. It returns status and advisory `dates` (`theatrical`, `digital`, `physical`, `region`, `status`, `checkedAtUtc`); `wishlist.list` includes the saved `releaseDates`. The calendar field is read-only in `wishlist.upsert`. Missing keys/IDs and failed lookups return `unavailable` with saved dates retained; a successful response with no regional dates returns `unknown`. Neither holds acquisition. There is no new periodic provider work. Configure the two-letter region through `downloads.movieCalendarRegion` (default `US`).

The wishlist movie CAM/TS/TC floor defaults on through `downloads.blockLowQualityWishlistSources`, including common concatenated forms. TV episode titles and screeners are outside this floor. `wishlist.upsert` accepts nullable `allowLowQualitySources`: null follows default/profile, true explicitly permits low sources, false blocks them. Existing selected configured profiles that explicitly permit low sources retain that permission. Other profile requirements continue to apply, and low-source movie copies never complete the wishlist cutoff. Manual downloads and racing are unchanged.

## Owned-release nuke watch

`library.nuke_watch(offset?, limit?)` reads recent completed/imported ownership, exact nuke flags, reasons/sources and corrective download attempts. The default page is 100, maximum 500. It reports `enabled`, `autoQueueFixes`, `watchDays`, `dropped`, and `persistenceHealthy`; it performs no network or disk scan. `library.index` and `library.lookup` also include `nukeFlags` for matching local paths. `downloads.list` exposes `nukeCorrectionForRelease` provenance on automatic fixes.

`nukeWatch.enabled=true` flags passively from existing PreDB responses and trusted sitebot observations. `nukeWatch.autoQueueFixes=false` is independently opt-in through Settings or `config.patch`; enabling it can queue a completed exact-identity, same-tier PROPER/newer same-group REPACK after a recognized quality nuke. The original library file stays intact. Failed/cancelled exact candidates do not regenerate on repeated observations; use normal explicit Retry or wait for a distinct correction. See the configuration guide for window, identity and retention behavior.
## Saved feed views

`feed.views`, `feed.save`, `feed.remove`, `feed.mark_read` and `feed.mute` manage local presentation preferences and unread counts. They never change wishlist, racing, polling exclusions or alerts. See [saved feed views](feed-views.md).

## Desktop bulk selection and Undo

`ui.select_many` sets the visible selection using exact IDs from `ui.state`, with at most 200 distinct IDs. Use screen `dashboard` with `SelectedWishlistItems`/`WishlistItems`, `SelectedNotificationItems`/`NotificationItems`, or `SelectedDownloadItems`/`DownloadItems`; use screen `spread` with `SelectedQueuedRaces`/`QueuedRaces`. Race IDs are `requestId`; other rows use `id`. An empty `ids` array clears selection. Missing, ambiguous or filtered-out rows reject the selection. Selections do not use changing row indices.

`ui.invoke` exposes the available bulk wishlist quality/status commands, feed removal and queued-race removal/site edits, and advertises `destructiveHint=true` for these persistent mutations. Set `BulkWishlistQuality` with a numeric value from `WishlistQualities`; status parameters are `Watching`, `Paused` or `Completed`. Destructive bulk list edits wait ten seconds; invoke `UndoBulkCommand` on `dashboard` to cancel. Closing the dashboard also cancels pending removal. Queue eligibility is checked again at execution. These commands never authorize deletion of local files or remote content. See [bulk actions](bulk-actions.md).

## Collection gaps

`collections.status` reads the last comparison without network or disk work. `collections.refresh(movieTmdbId)` explicitly fetches a movie's collection with at most two bounded TMDB requests, then compares known members against the current library index, downloads and wishlist. No polling or implicit index refresh occurs. Offline results are unknown; partial responses retain valid members with an explicit partial status.

`collections.add(movieTmdbId, filmTmdbId)` adds one known member with a known release year and confirmed missing ownership through the normal wishlist path using current default quality and source permissions. It rechecks current ownership, downloads and duplicate wishlist identities. Unknown ownership requires a library refresh before adding; undated films return `year-required`. Refresh with the library tools or **Refresh library index** in the Collection gaps window, which shows index age and completeness. See [collection gaps](collection-gaps.md).

## Flight recorder

`logs_tail` reads a bounded, redacted snapshot of the in-memory recorder. `logs_capture` explicitly requests a redacted local capsule and shares automatic capture's rate and storage limits. `diagnostics_files` and `diagnostics_read` accept `kind: "capsules"` or `"census"` in addition to the existing kinds. These tools never contact a site or feed additional data to the tuning agent. The local log console also offers expiring component traces and redacted ZIP export. See [flight recorder and log console](flight-recorder.md).

## Problems and health doctor

`problems.list` is read-only and returns cached redacted standing conditions, evidence, manual remedies, the check time, a `bounded` flag for the normal recent audit window, and a `partial` flag for unavailable or incomplete evidence. It makes no FTP/provider calls and cannot apply fixes or resolve certificate decisions. Use the local **Problems** window to preview and explicitly apply a supported configuration fix. See [Problems and health checks](problems.md).

## Statistics

`stats.summary(days?)` reads existing local telemetry and retained race history for 1–90 days (default 7). It returns endpoint payload totals including relay, route throughput, clean-rate evidence and coverage, queue wait, matched-announce lag, download outcomes and site-health sample coverage. Reads run in the background under shared time/byte/row/file limits, and `coverage.partial` identifies incomplete results. Historical transport and uptime can be unknown; historical unmarked duplicate skips can overstate payload. No site polling, login, config edit or allowance accounting occurs. See [statistics and traffic ledger](statistics.md).

## Release dossier and operations journal

`release.trace(release, section?, days?)` joins retained local evidence for an exact release name and supplies a config-only prediction. Release names are limited to 1,024 characters, sections to 256, and days to 1–90 (default 30). The result includes timeline events, predictions, coverage and uncertainty notes. It never starts a race, downloads, logs in, changes configuration or creates acquisition claims.

`ops.timeline(serverId?, days?, limit?)` reads the redacted operational journal (default 7 days and 200 events, maximum 500). `ops.incidents(serverId?, days?, limit?)` reads grouped incidents (default 7 days and 50 incidents, maximum 100). Both accept up to 90 days and return persistence health, dropped queue entries and partial read coverage with the result. All three tools are read-only and do no site or provider work. See [release dossier and operations journal](release-dossier.md) for retention and evidence limits.
