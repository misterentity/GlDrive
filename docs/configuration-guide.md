---
vault-doctor: skip
---

# Configuration Guide

All persistent state lives under **`%AppData%\GlDrive\`**. This guide walks through every file the app reads or writes and every config key it respects.

## The big picture

| File | Format | Owner | Editable? |
|---|---|---|---|
| `appsettings.json` | JSON (camelCase) | `ConfigManager` | Yes, but prefer the Settings dialog — the dialog validates and clamps values |
| `trusted_certs.json` | JSON | `CertificateManager` | Indirect via Settings → Diagnostics → Trust manager |
| `downloads-{serverId}.json` | JSON | `DownloadStore` | No (runtime queue state) |
| `race-history.json` | JSON | `RaceHistoryStore` | No |
| `race-queue.json` | JSON | `RaceQueueStore` | No (resume counts, partial ownership and cleanup deadlines) |
| `ftp-auth-holds.json` | JSON with DPAPI-protected credential markers | Authentication circuit | No (account holds and recovery-probe times) |
| `wishlist.json` | JSON | `WishlistStore` | Yes, and importable/exportable from the Dashboard |
| `notifications.json` | JSON | `NotificationStore` | No |
| `fish-keys-{serverId}.json` | DPAPI-encrypted JSON | `FishKeyStore` | No — use IRC `/key` and `/keyx` commands |
| `extractor-settings.json` | JSON | `ExtractorWindow` | Yes |
| `logs/gldrive-{date}.log` | Plain text | Serilog | No |
| `.running` / `.updating` | Text marker | `Program.cs` / `App.xaml.cs` | No |
| (credentials) | Windows Credential Manager | `CredentialStore` | Via Control Panel → User Accounts → Credential Manager |

Never check any of these into version control. They contain per-machine state, fingerprints, release history, and references to credentials.

---

## appsettings.json

`ConfigManager.Load()` deserializes this file with `System.Text.Json` using `JsonNamingPolicy.CamelCase` and indented output. A fresh install writes it from the first-run wizard. Legacy single-server configs (detected by the root-level `connection` key) are auto-migrated into the multi-server `servers: [ ... ]` array on load and immediately re-saved. API keys found in plaintext (`omdbApiKey`, `tmdbApiKey`, …) are moved to Credential Manager and scrubbed from the JSON on next save.

Top-level structure:

```jsonc
{
  "servers": [ /* ServerConfig[] */ ],
  "logging":   { /* LoggingConfig */ },
  "downloads": { /* DownloadConfig */ },
  "spread":    { /* SpreadConfig */ }
}
```

### `servers` — `List<ServerConfig>`

Each element describes a single glftpd site. Server `id` is an 8-character GUID prefix created by the wizard / Settings dialog; it's the key for per-server data files (`downloads-{id}.json`, `fish-keys-{id}.json`). **Do not rename it by hand** — downloads and FiSH keys live under the old id.

Rejected normal FTP credentials place a shared hold on the configured host, port and username. All pools, ghost cleanup and UI connection probes respect it; library-owned reconnects are disabled. Existing usable leases continue to work. The app issues one authentication notice per episode, with at most one automatic recovery probe every 30 minutes. A changed password or explicit Connect/Retry authentication action permits an earlier probe. Failed recovery probes keep the same hold and do not repeat the notice. Login-cap/BNC replies retain their separate classification; unsupported `!username` ghost login is not proof that the normal account password is wrong. Other connection faults receive a short shared backoff.

Authentication holds survive restart in `ftp-auth-holds.json`. Account keys are hashed and credential-change markers are protected by Windows DPAPI; the file contains no password or readable password digest. If a protected marker cannot be read, the hold remains until manual retry or a scheduled probe. No new `appsettings.json` fields are required.

```jsonc
{
  "id": "a1b2c3d4",
  "name": "My Site",
  "enabled": true,
  "speedLimitKbps": 0,              // 0 = global speed limit applies

  "connection": {
    "host": "ftp.example.com",
    "port": 21,                     // wizard default: 1337
    "username": "user",             // password → Credential Manager
    "rootPath": "/",
    "passivePorts": [],             // optional PASV port range restriction
    "proxy": null                   // ProxyConfig (see below), null = disabled
  },

  "mount": {
    "driveLetter": "G",
    "volumeLabel": "glFTPd",
    "autoMountOnStart": true,
    "mountDrive": true              // false = connect without a drive letter
  },

  "tls": {
    "preferTls12": true,            // required for glftpd TLS 1.3 session ticket bug
    "certificateFingerprintFile": "trusted_certs.json"
  },

  "cache": {
    "directoryListingTtlSeconds": 30,
    "maxCachedDirectories": 500,
    "directoryListTimeoutSeconds": 30,
    "fileInfoTimeoutMs": 1000,
    "readBufferSpillThresholdMb": 50
  },

  "pool": {
    "poolSize": 3,                  // capped to 2 when spread is active on this server
    "keepaliveIntervalSeconds": 30,
    "reconnectInitialDelaySeconds": 5,
    "reconnectMaxDelaySeconds": 120
  },

  "notifications": {
    "enabled": true,
    "pollIntervalSeconds": 60,
    "watchPath": "/recent",
    "excludedCategories": []
  },

  "search": {
    "searchPaths": [ "/" ],
    "maxDepth": 2,
    "method": "Auto",               // Auto | SiteSearch | CachedIndex | LiveCrawl
    "indexCacheMinutes": 60
  },

  "irc": { /* IrcConfig, see below */ },

  "spreadConfig": { /* SiteSpreadConfig, see below */ }
}
```

#### `connection.proxy` — `ProxyConfig`

```jsonc
"proxy": {
  "enabled": true,
  "host": "127.0.0.1",
  "port": 1080,
  "username": "user"      // password → Credential Manager (GlDrive:proxy:host:port:user)
}
```

When set, `FtpClientFactory` swaps `AsyncFtpClient` for `AsyncFtpClientSocks5Proxy` with an `FtpProxyProfile`. TOFU cert validation still applies.

#### `irc` — `IrcConfig`

```jsonc
"irc": {
  "enabled": true,
  "host": "irc.example.net",
  "port": 6697,
  "useTls": true,
  "nick": "mynick",
  "altNick": "mynick_",
  "realName": "GlDrive",
  "autoConnect": true,
  "inviteNick": "sitebot",          // FTP account to run SITE INVITE against, blank disables
  "fishEnabled": true,
  "fishMode": "CBC",                // ECB or CBC
  "channels": [
    { "name": "#site",    "key": "channelpw", "autoJoin": true },
    { "name": "#off",     "key": "",          "autoJoin": false }
  ],
  "announceRules": [
    {
      "enabled": true,
      "channel": "#announce",
      "pattern": "\\[NEW\\] in \\[(?<section>[^\\]]+)\\] (?<release>\\S+)",
      "autoRace": true
    }
  ]
}
```

Channel names prefixed with `-` in older configs disable autojoin. FiSH keys per nick/channel live in `fish-keys-{serverId}.json`, set via the IRC `/key` and `/keyx` slash commands.

#### `spreadConfig` — `SiteSpreadConfig`

```jsonc
"spreadConfig": {
  "sections": {
    "TV":    "/site/tv",
    "MP3":   "/site/mp3",
    "0DAY":  "/site/0day"
  },
  "priority": "Normal",             // VeryLow, Low, Normal, High, VeryHigh
  "maxUploadSlots": 1,
  "maxDownloadSlots": 1,
  "downloadOnly": false,
  "affils": [ "GROUP1", "GROUP2" ],
  "skiplist": [
    { "pattern": "*.nfo", "isRegex": false, "action": "Allow", "scope": "All",
      "matchDirectories": false, "matchFiles": true, "section": null }
  ]
}
```

`SitePriority` maps to the scoring table in [system-architecture.md#spread--fxp-engine](system-architecture.md#spread--fxp-engine):

| Enum | Score contribution |
|---|---|
| `VeryLow` | 0 |
| `Low` | 625 |
| `Normal` | 1250 |
| `High` | 1875 |
| `VeryHigh` | 2500 |

### `logging` — `LoggingConfig`

```jsonc
"logging": {
  "level": "Information",   // Verbose, Debug, Information, Warning, Error
  "maxFileSizeMb": 10,
  "retainedFiles": 3
}
```

`SerilogSetup.SetLevel(string)` lets you change the level at runtime without a restart — the Settings dialog does this. Logs roll daily by default; `rollOnSizeLimit` kicks in as a secondary safety when a single day's log grows past the size limit.

### `downloads` — `DownloadConfig`

```jsonc
"downloads": {
  "localPath": "C:\\Users\\me\\Downloads\\GlDrive",
  "categoryPaths": {
    "TV":     "D:\\TV",
    "Movies": "D:\\Movies"
  },
  "maxConcurrentDownloads": 1,
  "streamingBufferSizeKb": 256,
  "writeBufferLimitMb": 0,
  "qualityDefault": "1080p",
  "openRouterModel": "openai/gpt-oss-120b:free",
  "autoDownloadWishlist": true,
  "autoExtract": true,
  "deleteArchivesAfterExtract": true,
  "speedLimitKbps": 0,
  "skipIncompleteReleases": false,
  "maxRetries": 3,
  "retryDelaySeconds": 30,
  "scheduleEnabled": false,
  "scheduleStartHour": 0,
  "scheduleEndHour": 6,
  "verifySfv": true,
  "playSoundOnComplete": false,
  "theme": "Dark"                   // Dark, Light, System
}
```

API keys are **not** stored here. `ResolveOmdbKey()`, `ResolveTmdbKey()`, and `ResolveOpenRouterKey()` on `DownloadConfig` pull them from Credential Manager at call time. If they appear in the JSON (e.g., from an older config), `ConfigManager.MigrateApiKeys()` moves them on first load.

Scheduling wraps around midnight: `scheduleStartHour: 22` and `scheduleEndHour: 6` means downloads run 22:00 → 06:00.

Download destinations resolve in this order: an exact raw site-section key in `categoryPaths`, a logical `TV`/`Movies` key inferred from the release name, then `localPath`. Logical keys are case-insensitive. TV covers season/episode tokens, season packs and valid calendar dates with video evidence; Movies requires recognizable video quality, source or codec evidence. A year or bare `WEB` alone is insufficient. Unknown media types retain the default path.

When a logical mapping supplies the root, the normal layout omits its extra `TV`/`Movies` segment. Exact section matches retain the prior layout, including when the raw section itself is named `TV`. Existing queued paths, explicitly supplied MCP `localPath` values and selected torrent destinations stay unchanged.

#### Scored wishlist profiles

`downloads.scoredQualityEnabled` defaults to **false**, preserving existing download decisions. When enabled, the wishlist compares candidates with pending and successfully downloaded copies for the same movie or TV unit. Equal or worse copies are rejected; reaching the cutoff after a successful download completes a movie. A TV series remains watching, with separate copy records per episode/season pack. Failure/cancellation releases pending claims; pausing and automatic retries retain them. No local files are removed or renamed.

`downloads.profiles` is a list of user-defined profiles. Each has an `id`, `name`, `allowedResolutions` (the existing quality enum), `minimumScore`, `cutoffScore`, optional `minimumBytes`/`maximumBytes`, and `customFormats` (`pattern` plus signed `score`). Other controls are `allowUnknownResolution`, `allowFullDisc`, `allowLowQualitySources`, `allowUnknownSize`, `allowUpgrades`, `preferProperRepack`, and `upgradeProperRepackAfterCutoff`. Unknown sizes are never probed; a strict size requirement rejects candidates lacking size evidence. Custom regexes have bounded execution and invalid profiles fail closed.

The built-in score prefers BluRay over WEB, higher resolution within a source tier, untouched H264/H265 or remux over encodes, preferred editions, HDR and PROPER/REPACK. Profiles can add custom scores. Existing items without a profile keep an equivalent legacy resolution profile; the recommended built-ins exclude unknown resolution, full discs and CAM/TS/TC and stop at the selected WEB quality. Select a profile through `wishlist.upsert`; manage custom profiles using `config_patch` and inspect their schema with `config_schema`.

Owned records here mean successful GlDrive downloads, not a scan of a media library. Existing historical grabs are conservative duplicate evidence, not proof of ownership. Library imports, replacement/deletion of old copies and burst-window failover are separate features.

#### Trusted sitebot observations

Each IRC channel accepts a `sitebot` object: `enabled` (default true), `botNick` (default empty), optional `userHostPattern` (`*`/`?` wildcards against `user@host`), and `templates` (default empty). An empty bot nickname leaves decoding inactive. Trust is configured explicitly; it is never learned from arbitrary chat. Changes restart only the IRC service. Built-in parsers cover common pzs-ng/ngBot/dZSbot NEW, CHECKERED-FLAG/COMPLETE, CROSSED STICKS/HALFWAY, RACE, NUKE/UNNUKE and TV-MAZE/IMDB families.

Custom templates specify `kind` and an anchored `pattern` with named `section` and `release` groups. Optional captures include `bytes`, `size`/`unit`, `files`, `speed`/`speedunit`, `winner`, `leader`, `actor`, `nukees`, `factor`, `reason`, `imdb`, `network`, `status`, `genres`, `rating` and `votes`. Regexes must support nonbacktracking matching and have bounded length and execution time. Sitebot MB/GB units use binary multipliers. Bot facts are bounded, expire and remain advisory.

`spread.useSitebotCompletionHints` defaults to **false**. When enabled, announced file counts are cross-checked against existing destination verification for outcome diagnostics; they never establish ownership or trigger another LIST. Normal completion detection remains authoritative. `spread.useSitebotMetadata` also defaults to **false**; enabling it lets a trusted exact-source/release metadata event answer before HTTP only when it contains every field needed by the active filters. Missing evidence falls back to the existing lookup.

Final explicit COMPLETE winners are recorded per destination only with a successful existing listing after the announcement and confirmed completion. A HALFWAY leader, copied-byte share or missing winner is never a win/loss observation. The legacy site-id `winner` field remains separate from a bot's winning username. Use `spread.report` for daily/section/site results and `spread.sitebot_facts` to inspect observations. This core does not skip queued races based solely on bot text, wake FTP probes, send nuke notifications, or change per-race toasts.

### `spread` — `SpreadConfig`

`sizeAwareRaceQueue` defaults to **true**. At dispatch, eligible requests use highest response ratio `(wait + predicted work) / predicted work`. Prediction prefers trusted sitebot announced bytes and an existing route speed, with section × resolution history medians and broader/local fallback estimates when evidence is missing. Predicted work is bounded, persisted with the request and visible in queue snapshots. No prediction performs a lookup or FTP request. Set false to restore FIFO within the existing priority classes.

`raceQueueMaxAgeMinutes` defaults to **120** (1–1440). Requests beyond that age use oldest-first ordering within their class. Cleanup deadlines still take precedence; fresh work stays ahead of ordinary partial resumes. Deferred requests retain their next-probe time. This is nonpreemptive: a running race keeps its slot and a fresh arrival waits for that attempt to finish. Ageing prevents smaller jobs from indefinitely overtaking older eligible work within the same class; partials retain their existing bounded resume/cleanup deadline.

`mergeRaceAnnounces` defaults to **true**. Duplicate announces enrich the existing queued, starting or active request with participants and valid source hints. Source eligibility respects `neverSource`; a receiving-only announcement cannot displace a usable feeder. The original age, backoff, resume counts and owned-partial cleanup state are preserved. Active destination paths stay pinned, and new hints use existing pools and normal scans before any ownership is inferred. Set false for the old first-request-wins behavior. The defaults address queue delays and discarded-source evidence without adding capacity, preemption, logins or polling.

```jsonc
"spread": {
  "spreadPoolSize": 3,              // separate FXP pool, bounded by transfer and account gates
  "transferTimeoutSeconds": 60,     // per-file
  "hardTimeoutSeconds": 1200,       // idle timeout: seconds without payload progress
  "absoluteRaceTimeoutMinutes": 90, // absolute active-attempt cap (1–1440 minutes)
  "partialRaceDeadlineHours": 10,   // from first upload (1–11 hours)
  "maxConcurrentRaces": 1,
  "autoRaceOnNotification": true,
  "notifyOnRaceComplete": true,
  "nukeMarkers": [ ".nuke", "NUKED-" ],
  "globalSkiplist": [
    { "pattern": "sample", "isRegex": false, "action": "Deny",
      "matchDirectories": true, "matchFiles": false }
  ]
}
```

`globalSkiplist` applies to every race; per-site `spreadConfig.skiplist` runs first. First match wins (see [system-architecture.md#spread--fxp-engine](system-architecture.md#spread--fxp-engine) for evaluation order).

`hardTimeoutSeconds` measures inactivity in payload progress. Active attempts also obey `absoluteRaceTimeoutMinutes`, even if bytes continue moving. A ceiling- or stall-stopped race with landed files can resume at most three times, after due fresh races. This resume budget is separate from the existing crash/restart budget and survives restart in `race-queue.json`.

`partialRaceDeadlineHours` starts at the first upload and survives retries and restarts. When a partial cannot finish, cleanup uses existing pools and exact successful release-root creation records. It checks current contents before issuing `SITE WIPE`, preserves complete or shared directories, and never treats an existing directory as one GlDrive created. Cleanup failures remain visible and persisted; unavailable sites can prevent remote cleanup. `race-history.json` records the stop reason, resume count and first-upload timestamp. The two new settings are editable through JSON or MCP `config_patch`; omitted settings retain the defaults above.

---

## Credential Manager keys

Nothing sensitive lives in `appsettings.json`. `CredentialStore` uses these target name patterns, all with `CredentialPersistence.LocalMachine`:

| Purpose | Target name |
|---|---|
| FTP password | `GlDrive:{host}:{port}:{username}` |
| SOCKS5 proxy password | `GlDrive:proxy:{host}:{port}:{username}` |
| IRC server password | `GlDrive:irc:{host}:{port}:{nick}` |
| SSH (glftpd installer) | `GlDrive:ssh:{host}:{port}:{username}` |
| OMDB API key | `GlDrive:api:omdb` |
| TMDB API key | `GlDrive:api:tmdb` |
| OpenRouter API key | `GlDrive:api:openrouter` |

You can inspect or remove them from **Control Panel → User Accounts → Credential Manager → Windows Credentials**. Searching "GlDrive:" narrows the list.

---

## trusted_certs.json

Managed by `CertificateManager` in the Tls subsystem. Keyed by `host:port`, storing SHA-256 fingerprint + first-trusted timestamp. Written atomically to `.tmp` then renamed. The file's ACL grants full control to the current user only.

```jsonc
{
  "ftp.example.com:21": {
    "fingerprint": "AABBCC…",
    "trustedAt":   "2026-04-13T12:10:00Z"
  }
}
```

On a fingerprint change, `CertificateManager` fires the `CertificatePrompt` delegate — the Settings dialog wires this to a "Certificate changed" dialog that requires explicit user approval before the new fingerprint is persisted. Rejection blocks the connection. Clearing a cert via Settings → Diagnostics → Trust manager removes that key so the next connection re-TOFUs.

---

## extractor-settings.json

Written by `ExtractorWindow` when the user configures watch folders or the extraction destination. Auto-loaded on window open; preserved across updates because the uninstaller skips `%AppData%\GlDrive`.

---

## Logs

Rolling file sink at `%AppData%\GlDrive\logs\gldrive-{yyyyMMdd}.log`, daily with size-based rollover to `gldrive-{yyyyMMdd}_NNN.log`. Output template:

```
{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}
```

The Settings dialog exposes a "View Logs" button that opens the folder in Explorer. Retention is `logging.retainedFiles` days.

The watchdog subprocess writes to the same daily file when it restarts the main process after a crash — look for `[WARN]` lines with `CRASH:` prefix text to find prior segfault reasons.

---

## See also

- [codebase-summary.md#data-files](codebase-summary.md#data-files) — quick reference for all files
- [system-architecture.md#update--crash-recovery](system-architecture.md#update--crash-recovery) — why `.running` and `.updating` exist
- [deployment-guide.md](deployment-guide.md) — how the installer preserves `%AppData%\GlDrive` on upgrade

### Wanted acquisition windows and cache sweeps

`downloads.wantedDecisionWindowEnabled` defaults to `false`. Opting in collects wanted variants across mounted servers for a fixed `wantedDecisionWindowMinutes` (default 15, range 1–60). A fresh completion event from an explicitly trusted B7 channel bot makes a candidate eligible; later upload/progress/nuke events revoke that hint. The B1 scorer ranks candidates when `scoredQualityEnabled` is on. Otherwise the existing quality filter applies and equal candidates keep arrival order. Download planning and verification remain authoritative. Unknown-complete candidates expire after `wantedCandidateMaxAgeHours` (default 24, range 1–168).

`downloads.wantedIndexSweepsEnabled` also defaults to `false`. When enabled, adding/updating/importing wanted items and ordinary index rebuilds search existing cache snapshots. No extra LIST, probe, or login is issued. `wantedIndexSweepLimit` bounds offers per site (default 100, range 1–1000). `wishlist.wanted` exposes offers and decision windows. Cached directories alone are suggestions; automatic acquisition additionally requires the window, `autoDownloadWishlist`, and a fresh trusted completion hint. Old configurations retain immediate A2 matching. Pending windows are in memory; ordinary cache sweeps and new events rebuild them after restart, while B1's durable download claims still prevent duplicate acquisition. Early cutoff bypass, persistent feed history, and a dedicated Wanted grid are not included.

### Monitored series and race-completed pulls

Existing TV items retain `monitorMode: Legacy`. Explicitly select `Future`, `Missing`, `All`, or `LatestSeason` on a TV item with `tvMazeId` to enable daily episode inventory refresh. Future uses `monitorSinceUtc` or the item's added date. Inventory and attempt timestamps persist in the wishlist; failed metadata requests preserve the last good inventory. No inventory means no automatic monitored-series grab. Successful managed downloads establish owned episodes; this is not a scan of an existing media library.

`wishlist.series` reports per-episode not-aired, missing, on-site, downloading and owned states. `wishlist.season_plan` uses cached indexes to choose missing episodes or one non-overlapping older-season pack. `wishlist.season_pull` explicitly admits the ready plan through normal download claims. A ready plan covers currently aired gaps; it does not infer that the newest season has ended. An episode known under a date and under SxxEyy is one acquisition. Monitored series require missing episodes, so previously owned episodes are retained without automatic replacement.

`downloads.preferFastestWantedSource` defaults off and uses measured source-to-local SpeedTracker samples when enabled. `downloads.raceCompletedPullsEnabled` defaults off, requires the wanted window, explicit `racePullServerIds` (empty by default), and per-show `autoPullCompletedRaces: true` (default false). Only verified complete receiving destinations with known paths become candidates. Partial destinations never trigger a pull. Background series pulls share existing pools, wait while a site has active or queued racing, and cooperate with race demand. CPSV lanes yield safely; native PASV lanes finish their current file before yielding, avoiding teardown under native reads. No eager pool, cap increase, LAN scan, or extra FTP index crawl is added. Running downloads are never retargeted. A dedicated series calendar/UI and importing ownership from an external library remain deferred.

### Race controls and own-account nukes

Race cards support working selection/Stop, a safe Reset at a scan boundary, confirmed cleanup of owned partials, and Re-race from history. Add/Remove site applies to queued requests; active/starting edits and removal of the source or a site with owned partials are rejected. Reset retains ownership, native reads, login holds and policy denials. Re-race goes through normal admission and reports its real outcome. Cleanup reuses the existing account-bound GlDrive-created-directory and positive-incompleteness checks; complete, shared, foreign and known nuked content remains protected.

`spread.ownNukeAlertsEnabled` defaults to `true`: passive exact-account attribution is low risk and adds no traffic. Trusted B7 NUKE events and the existing SITE NUKES poll share persisted notification dedupe. The nuker is never treated as the nukee; missing/ambiguous attribution does not raise an own-account alert. Boxed relative-age replies are parsed without generating a new telemetry row every time their age changes. `spread.nukes` exposes retained observations. Disabling alerts does not disable known-nuke safety holds. No polling cadence, login budget, automatic wipe scope, or remote UNNUKE command is added. Per-site learned autonuke deadlines remain deferred.

### Site and announce health

`health.enabled` defaults to `true`. Site health distinguishes healthy, degraded, down, quarantined and authentication-rejected states. Retry state persists in `site-health.json`: the existing 30-second to five-minute ladder applies for the first 30 minutes of an outage, followed by 15 minutes, one hour and four hours. BNC cooldowns and authentication holds remain authoritative. Only failed logins count as outage evidence: a dropped live connection, a login-limit reply (the account's own sessions holding its slots) and local pool waits do not, so they never defer race logins or the `!user` ghost kill. The supervisor emits one outage notice and one recovery notice rather than a notice per failed attempt. The state file is advisory: if `site-health.json` is unreadable it is copied to `site-health.json.corrupt` and tracking restarts empty, and a failed write keeps the ladder in memory. Neither ever holds logins. Manual retry remains available. The Mounts cards show health and the next retry; hovering over pool slots shows active borrow purposes. `servers.get` exposes the detailed evidence and account budget.

`servers[].pool.loginRateLimitEnabled` defaults to `true`, with `maxLoginAttemptsPerFiveMinutes: 8` (range 2–100). All explicit logins use `GuardedFtpLogin`; successful attempts count too. One slot is reserved for the existing bounded ghost recovery. Attempts and account holds persist in `ftp-login-governor.json`. Reusing an existing pooled connection does not spend this budget. The simultaneous-login gate, reserved race capacity and authentication circuit still apply. Only a server reply that explicitly throttles (for example "cooldown" or "rate limit") arms the two-hour hold. A bare `421`/`450`, which most daemons use for "too many users", does not; a login-limit reply holds ordinary logins for 90 s and still allows ghost recovery. An unreadable budget file is copied to `ftp-login-governor.json.corrupt` and the budget restarts empty; a failed write keeps the budget in memory.

`health.tcpProbesEnabled` defaults to `false`. When enabled, escalated retries may first make a five-second TCP-only check of the configured endpoint, only after unreachable/timeout evidence and at most once per 15 minutes across restarts. Configured SOCKS5 routes are honored with no direct fallback. Probes send no FTP login or TLS handshake. There is no LAN scanning or alternate-host discovery.

`health.announceSilenceAlertsEnabled` defaults to `true`. Each channel learns a separate UTC hour-of-week rate from observed joined time and accepted announces. A bucket needs at least two hours of exposure and 20 messages; an alert additionally needs at least 60 minutes of observed silence and an expected-count threshold corresponding to a zero-message probability of 0.001. App-off time and missed timer intervals do not train the baseline. State and notification dedupe persist in `announce-feed-health.json`; cold channels report `Learning`. Diagnosis distinguishes disconnection, a missing join, bot absence and decrypt failures. `irc.status` includes these snapshots.

`health.announceRecoveryEnabled` defaults to `false`. Opting in permits a missing configured channel to use the existing invite/join path, with a durable 30-minute per-server recovery budget and an available FTP service. It never disconnects a joined but silent channel. Historical baseline import and a dedicated feed-health chart are not included.
