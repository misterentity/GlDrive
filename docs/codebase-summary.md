# Codebase Summary

## Scale & Layout

Single .NET project: `src/GlDrive/GlDrive.csproj` — **345 `.cs` files, 11 `.xaml` files** as of v1.44.55.

```
fmountr/                                (repo root)
├── GlDrive.sln                         Solution wrapper (no project refs)
├── README.md                           User-facing overview
├── CLAUDE.md                           Notes for AI assistants
├── docs/                               This documentation
├── installer/
│   ├── build.ps1                       Build pipeline (publish + ISCC + zip)
│   ├── release.ps1                     Release pipeline (build + gh release)
│   ├── GlDrive.iss                     Inno Setup script
│   └── deps/winfsp.msi                 Optional bundled WinFsp driver
└── src/GlDrive/
    ├── GlDrive.csproj                  Target net10.0-windows, win-x64, WinExe, UseWpf
    ├── Program.cs                      Entry point, watchdog dispatch, crash recovery
    ├── App.xaml / App.xaml.cs          WPF lifecycle, startup sequence, global styles
    ├── Assets/                         Icons, images
    ├── Config/                         AppConfig, ServerConfig, ConfigManager, CredentialStore, IrcConfig, SpreadConfig
    ├── Logging/SerilogSetup.cs         Rolling file sink, runtime level switch
    ├── Services/                       ServerManager, MountService, ConnectionMonitor, NewReleaseMonitor, SingleInstanceGuard, UpdateChecker
    ├── Ftp/                            FtpClientFactory, FtpConnectionPool, FtpOperations, CpsvDataHelper, StreamingDownloader
    ├── Tls/CertificateManager.cs       TOFU SHA-256 fingerprint pinning
    ├── Filesystem/                     GlDriveFileSystem (WinFsp), FileNode, DirectoryCache, NtStatusMapper
    ├── Downloads/                      DownloadManager/Store/History, Wishlist, NotificationStore, SfvVerifier, ArchiveExtractor, FtpSearchService, OMDB/TMDB/TVMaze/PreDB clients, SceneNameParser
    ├── Spread/                         SpreadManager, SpreadJob, FxpTransfer, SpreadScorer, SpeedTracker, SkiplistEvaluator, RaceHistoryStore, IrcAnnounceListener, IrcPatternDetector
    ├── Irc/                            IrcClient, IrcService, IrcMessage, FishCipher, FishBase64, Dh1080, FishKeyStore
    ├── Player/                         MediaStreamServer, PlayerViewModel (+ VLC/cast), PlayerResumeStore, TorrentSearchService, TorrentStreamService
    └── UI/                             WPF views, view models, converters, theme dictionaries — see below
```

## Subsystems

Each folder under `src/GlDrive/` is a distinct subsystem. Dependencies flow top-down in the table: upper subsystems depend on lower ones, not the reverse.

| Subsystem | Responsibility | Key types |
|---|---|---|
| **UI** | WPF tray, dashboard, settings, wizard, extractor, IRC/spread/player/browse views | `TrayViewModel`, `DashboardWindow/ViewModel`, `SettingsWindow/ViewModel`, `WizardWindow`, `ExtractorWindow`, `IrcViewModel`, `SpreadViewModel`, `BrowseViewModel`, `PlayerViewModel`, `ThemeManager`, `WebViewHost` |
| **Services** | Lifecycle orchestration across servers; update distribution | `ServerManager`, `MountService`, `ConnectionMonitor`, `NewReleaseMonitor`, `SingleInstanceGuard`, `UpdateChecker` |
| **Spread** | FXP racing engine, IRC-announce learning, race history | `SpreadManager`, `SpreadJob`, `FxpTransfer`, `SpreadScorer`, `SpeedTracker`, `FxpModeDetector`, `SkiplistEvaluator`, `RaceHistoryStore`, `IrcAnnounceListener`, `IrcPatternDetector` |
| **Irc** | TLS IRC client with FiSH encryption and DH1080 key exchange | `IrcClient`, `IrcService`, `IrcMessage`, `FishCipher`, `FishBase64`, `Dh1080`, `FishKeyStore` |
| **Downloads** | Queue, retry, schedule, SFV verify, extract, metadata, search, wishlist | `DownloadManager`, `DownloadStore`, `WishlistStore`, `WishlistMatcher`, `NotificationStore`, `SfvVerifier`, `ArchiveExtractor`, `FtpSearchService`, `SceneNameParser`, `OmdbClient`, `TmdbClient`, `TvMazeClient`, `PreDbClient` |
| **Player** | Local HTTP media server, VLC integration, torrent streaming, resume tracking | `MediaStreamServer`, `PlayerResumeStore`, `TorrentSearchService`, `TorrentStreamService` |
| **Filesystem** | WinFsp virtual filesystem: metadata, I/O, caching, NTSTATUS mapping | `GlDriveFileSystem`, `FileNode`, `DirectoryCache`, `NtStatusMapper` |
| **Ftp** | FluentFTP factory, bounded pool, operations router, CPSV protocol, streaming download | `FtpClientFactory`, `FtpConnectionPool`, `PooledConnection`, `FtpOperations`, `CpsvDataHelper`, `StreamingDownloader` |
| **Tls** | Trust-on-first-use certificate pinning | `CertificateManager` |
| **Logging** | Serilog configuration with daily rolling file sink | `SerilogSetup` |
| **Config** | Typed config tree, JSON persistence, legacy migration, credential storage | `AppConfig`, `ServerConfig`, `ConnectionConfig`, `MountConfig`, `TlsConfig`, `CacheConfig`, `PoolConfig`, `NotificationConfig`, `SearchConfig`, `IrcConfig`, `SpreadConfig`, `DownloadConfig`, `LoggingConfig`, `ConfigManager`, `CredentialStore` |

Subsystem interaction diagrams are in [system-architecture.md](system-architecture.md).

## Key Dependencies

All versions are pinned via `RestorePackagesWithLockFile = true`. Runtime dependencies are self-contained — the publish output carries everything the app needs.

### Runtime / Core

| Package | Version | Runtime | Why it's here |
|---|---|---|---|
| `winfsp.net` | 2.1.25156 | runtime | Managed bindings for WinFsp — powers the virtual drive letter (`GlDriveFileSystem : FileSystemBase`) |
| `FluentFTP` | 53.0.2 | runtime | FTPS client. Used by `FtpClientFactory` / `FtpConnectionPool` / `FtpOperations` |
| `FluentFTP.GnuTLS` | 1.0.38 | runtime | GnuTLS stream implementation for FluentFTP. `GnuAdvanced.NoTickets` + `PreferTls12` work around glftpd's TLS 1.3 session-ticket bug |
| `H.NotifyIcon.Wpf` | 2.4.1 | runtime | System tray icon — requires `ForceCreate(false)` + `GeneratedIconSource` |
| `Serilog` | 4.3.1 | runtime | Structured logging, read at runtime from `LoggingConfig.Level` |
| `Serilog.Sinks.File` | 6.0.0 | runtime | Daily-rolling file sink at `%AppData%\GlDrive\logs\gldrive-{date}.log` |
| `Meziantou.Framework.Win32.CredentialManager` | 1.7.17 | runtime | Windows Credential Manager wrapper. All FTP/IRC/proxy/SSH/API keys live there, not in the config JSON |
| `Microsoft.Extensions.Configuration.Json` | 10.0.5 | runtime | JSON config binding |
| `Microsoft.Extensions.Configuration.Binder` | 10.0.5 | runtime | Typed-config binding |
| `SharpCompress` | 0.47.2 | runtime | RAR/ZIP/7z extraction in `ArchiveExtractor` and `MediaStreamServer` |
| `System.IO.Hashing` | 9.0.14 | runtime | CRC32 for `SfvVerifier`; SHA-256 in `CertificateManager` / `UpdateChecker` |
| `BouncyCastle.Cryptography` | 2.6.2 | runtime | Blowfish (ECB/CBC) for FiSH + BigInteger ops for DH1080 key exchange |
| `Microsoft.Web.WebView2` | 1.0.3856.49 | runtime | Embedded Chromium for World Monitor / Discord / Streems dashboard tabs |
| `LibVLCSharp` + `LibVLCSharp.WPF` | 3.9.6 | runtime | VLC bindings and WPF control for the in-app media player |
| `VideoLAN.LibVLC.Windows` | 3.0.23 | runtime | Bundled LibVLC native DLLs |
| `MonoTorrent` | 3.0.2 | runtime | DHT + torrent streaming via `TorrentStreamService` |
| `SSH.NET` | 2024.2.0 | runtime | SSH/SFTP client used by `GlftpdInstallerWindow` for remote install flows |
| `System.Security.Cryptography.ProtectedData` | 10.0.3 | runtime | DPAPI wrapper — encrypts `fish-keys-{serverId}.json` on disk |

There are **no** development/test-only dependencies — the project has no tests.

## Data Files

All per-user state lives under `%AppData%\GlDrive\`. See [configuration-guide.md](configuration-guide.md) for the schema.

| Path | Owner | Format |
|---|---|---|
| `appsettings.json` | `ConfigManager` | camelCase JSON |
| `trusted_certs.json` | `CertificateManager` | JSON `{ host:port → { fingerprint, trustedAt } }` (user-only ACL) |
| `downloads-{serverId}.json` | `DownloadStore` | JSON, debounced 2 s |
| `race-history.json` | `RaceHistoryStore` | JSON, capped at 500 entries |
| `wishlist.json` | `WishlistStore` | JSON, global |
| `notifications.json` | `NotificationStore` | JSON, capped at 1000 entries |
| `fish-keys-{serverId}.json` | `FishKeyStore` | DPAPI-encrypted JSON |
| `extractor-settings.json` | `ExtractorWindow` | JSON |
| `logs/gldrive-{date}.log` | Serilog | Plain text, daily rolling |
| `.running` / `.updating` | `Program.cs` / `App.xaml.cs` | Marker files (timestamps / `CRASH:` prefix) |
| Windows Credential Manager | `CredentialStore` | `GlDrive:{host}:{port}:{user}` / `GlDrive:irc:...` / `GlDrive:api:{service}` / etc. |

## Entry Points & Modes

`Program.Main()` dispatches on argv before the WPF app ever starts:

| Argv | Handler | Purpose |
|---|---|---|
| *(none)* | normal startup | Spawn watchdog, run WPF app |
| `--watchdog <pid>` | `Program.RunWatchdog` | Hidden subprocess; on parent crash, reads Windows Event Log for the reason, restarts the app |
| `--apply-update <pid> <extractDir> <installDir>` | `UpdateChecker.ApplyUpdate` | Elevated updater; waits for parent exit, renames files to `.old`, copies new files, launches child, **force-kills self** to avoid GnuTLS teardown crash |
| `--screenshots` | `ScreenshotCapture.CaptureAll` | Renders wizard/dashboard/settings to PNGs for README |

## See also

- [system-architecture.md](system-architecture.md) — subsystem diagrams and protocol details
- [code-standards.md](code-standards.md) — conventions the codebase actually uses
- [configuration-guide.md](configuration-guide.md) — every config key with defaults and behavior
- [deployment-guide.md](deployment-guide.md) — how the installer and update zip are built
