# Configuration Guide

All persistent state lives under **`%AppData%\GlDrive\`**. This guide walks through every file the app reads or writes and every config key it respects.

## The big picture

| File | Format | Owner | Editable? |
|---|---|---|---|
| `appsettings.json` | JSON (camelCase) | `ConfigManager` | Yes, but prefer the Settings dialog — the dialog validates and clamps values |
| `trusted_certs.json` | JSON | `CertificateManager` | Indirect via Settings → Diagnostics → Trust manager |
| `downloads-{serverId}.json` | JSON | `DownloadStore` | No (runtime queue state) |
| `race-history.json` | JSON | `RaceHistoryStore` | No |
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

`categoryPaths` wins over `localPath` when the downloaded release's category key matches.

### `spread` — `SpreadConfig`

```jsonc
"spread": {
  "spreadPoolSize": 2,              // per-server FXP pool floor; actual size = max(this, maxSlots)
  "transferTimeoutSeconds": 60,     // per-file
  "hardTimeoutSeconds": 1200,       // whole-race cap = 20 minutes
  "maxConcurrentRaces": 1,          // capped to 1 in chain mode (default since v1.44.35)
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
