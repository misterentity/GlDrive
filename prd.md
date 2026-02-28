\# Product Requirements Document

\# GlDrive — glftpd FTPS Drive Mount for Windows 11



\*\*Document version:\*\* 1.0  

\*\*Date:\*\* 2026-02-27  

\*\*Status:\*\* Draft  

\*\*Classification:\*\* Personal / Single-machine deployment  



---



\## Table of Contents



1\. \[Executive Summary](#1-executive-summary)

2\. \[Background \& Problem Statement](#2-background--problem-statement)

3\. \[Goals \& Non-Goals](#3-goals--non-goals)

4\. \[Technology Stack Decision](#4-technology-stack-decision)

5\. \[System Architecture](#5-system-architecture)

6\. \[Functional Requirements](#6-functional-requirements)

7\. \[Non-Functional Requirements](#7-non-functional-requirements)

8\. \[glftpd Protocol Compatibility Requirements](#8-glftpd-protocol-compatibility-requirements)

9\. \[TLS / Security Requirements](#9-tls--security-requirements)

10\. \[Configuration \& Credential Management](#10-configuration--credential-management)

11\. \[Windows Service / Auto-Start Architecture](#11-windows-service--auto-start-architecture)

12\. \[User Interface Requirements](#12-user-interface-requirements)

13\. \[Error Handling \& Resilience](#13-error-handling--resilience)

14\. \[Installer \& Deployment](#14-installer--deployment)

15\. \[Logging \& Diagnostics](#15-logging--diagnostics)

16\. \[Out of Scope](#16-out-of-scope)

17\. \[Technical Risks \& Mitigations](#17-technical-risks--mitigations)

18\. \[Recommended Technology Stack Summary](#18-recommended-technology-stack-summary)

19\. \[Dependency \& Licensing Summary](#19-dependency--licensing-summary)

20\. \[Glossary](#20-glossary)



---



\## 1. Executive Summary



\*\*GlDrive\*\* is a Windows 11 native application that mounts a single remote glftpd server as a local drive letter using FTPS Explicit SSL on a non-standard port. The application runs automatically at Windows logon as a user-session tray application (not a Windows service), providing seamless access to remote FTP storage through standard Windows Explorer and Win32 file APIs.



The application is designed for personal, single-machine use by a technically proficient user who administers or accesses a glftpd server and requires persistent, transparent drive-letter access to its file tree.



---



\## 2. Background \& Problem Statement



\### 2.1 What is glftpd?



\*\*glftpd\*\* (GreyLine FTP Daemon) is a highly configurable FTP server for Linux/Unix systems, currently at v2.16 (released December 2025). It is widely used in private file-sharing communities and personal server setups. Key characteristics relevant to this project:



\- Implements RFC 959 FTP with AUTH TLS (FTPS Explicit) via OpenSSL 3.x

\- \*\*Does not implement MLSD/MLST\*\* — only classic Unix-style `LIST` output

\- Typically runs on \*\*non-standard ports\*\* (e.g., 1337) rather than the default port 21

\- Uses self-signed TLS certificates (900-day validity by default)

\- Requires \*\*TLS session reuse\*\* on data connections (PROT P) for FTPS to function correctly

\- Has a known issue where \*\*TLS 1.3 upload data connections drop session tickets\*\* — TLS 1.2 is preferred for compatibility

\- Uses an internal flat-file user database with IP mask checks enforced before password validation

\- Supports passive mode via configurable `pasv\_ports` ranges



\### 2.2 The Problem



No existing Windows tool satisfactorily mounts an FTPS Explicit server as a drive letter while handling glftpd's specific requirements:



| Existing Tool | Limitation |

|---|---|

| Windows 11 built-in | No FTPS support; no drive letter for FTP at all |

| RaiDrive | Undocumented TLS session reuse; not tested with glftpd |

| Mountain Duck | No documented glftpd compatibility; expensive |

| NetDrive | Bugs with Explicit FTPS port configuration |

| rclone mount | Open critical bug for FTPS TLS session reuse |

| WinSCP | Explicitly does not support drive mounting |



The specific combination of FTPS Explicit + non-standard port + self-signed certificate + TLS session reuse + missing MLSD creates a compatibility matrix that no off-the-shelf tool handles reliably.



---



\## 3. Goals \& Non-Goals



\### 3.1 Goals



\- Mount a single glftpd server as a drive letter (e.g., `G:`) accessible from Windows Explorer and all Win32 applications

\- Connect via FTPS Explicit (AUTH TLS) on a configurable, non-standard port

\- Support self-signed TLS certificates using Trust On First Use (TOFU)

\- Implement proper TLS session reuse on data connections (critical for glftpd compatibility)

\- Run automatically at Windows logon without requiring elevated privileges

\- Provide a system tray icon for connection status, mount/unmount, and settings access

\- Persist credentials securely using the Windows Credential Manager

\- Survive network interruptions and reconnect automatically



\### 3.2 Non-Goals



\- Multi-site or simultaneous multi-connection support

\- Enterprise deployment, MDM/Intune management, or Group Policy integration

\- SFTP, WebDAV, or any protocol other than FTPS Explicit

\- Active mode FTP (PORT/EPRT) — passive mode (PASV) only

\- MLSD/MLST parsing (glftpd does not support this)

\- FTP-over-TLS Implicit (port 990) — Explicit only

\- Write-caching or offline mode (all writes flush directly to server)

\- File conflict resolution or sync semantics

\- Running as a traditional Windows service (see §11 for rationale)



---



\## 4. Technology Stack Decision



\### 4.1 Filesystem Driver: WinFsp ✅ Recommended



\*\*WinFsp\*\* (Windows File System Proxy, v2.1) is selected over Dokan (Dokany) for the following reasons:



\*\*WinFsp advantages:\*\*

\- Integrates with the Windows Cache Manager — directory listing caching occurs at kernel level, reducing repeated user-mode round-trips

\- Integrates with the Filter Manager — antivirus and other filesystem filters work correctly

\- Automatic kernel cleanup if the host process crashes (no orphan drive letters)

\- No known kernel-mode crashes or resource leaks (used by rclone, Cryptomator, SSHFS-Win)

\- Well-maintained with official .NET NuGet package (`winfsp.net`)

\- GPLv3 with FLOSS exception — non-issue for personal, non-distributed use



\*\*Dokan disadvantages that led to rejection:\*\*

\- Historical BSOD issues (partially resolved in v2.x; still noted in release notes)

\- No Cache Manager integration

\- Rejected by Mountain Duck's team as "not very stable"



\### 4.2 FTP Client Library: FluentFTP + FluentFTP.GnuTLS ✅ Recommended



\*\*FluentFTP\*\* (v53+, MIT) is selected as the FTP client library because:

\- Explicitly lists glftpd as a \*\*supported server type\*\* with a dedicated server profile

\- Supports FTPS Explicit via `FtpEncryptionMode.Explicit`

\- Handles non-standard port in constructor

\- Automatic Unix-style `LIST` parsing (30+ server types, including glftpd)

\- Supports self-signed cert acceptance via `ValidateCertificate` callback

\- Full async/await API via `AsyncFtpClient`

\- ~47M NuGet downloads; actively maintained



\*\*FluentFTP.GnuTLS\*\* (v1.0.38, LGPL 2.1) is required as an add-on to solve the single most critical problem: \*\*.NET's built-in `SslStream` does not expose TLS session resumption APIs\*\*, making FTPS data connections fail with servers that enforce session reuse. The GnuTLS add-on replaces `SslStream` with a native GnuTLS wrapper that implements proper TLS session resumption.



\*\*Limitations of FluentFTP.GnuTLS\*\* (accepted tradeoffs):

\- Async API calls execute internally synchronously

\- x64 architecture only (acceptable for Windows 11 personal use)

\- No client certificate authentication



\### 4.3 UI Framework: WPF on .NET 10 ✅ Recommended



\- \*\*.NET 10 LTS\*\* (November 2025, supported until November 2028)

\- \*\*WPF\*\* — mature XAML-based framework with MVVM support; ideal for settings dialogs and tray-resident applications

\- \*\*H.NotifyIcon.Wpf\*\* (550K+ NuGet downloads) for rich system tray icon with context menu, balloon notifications, and popup windows in pure XAML



WinUI 3 was rejected (no native system tray support, more complex deployment). WinForms was rejected (dated styling, weaker data binding).



\### 4.4 Architecture Pattern: Single-Process User-Session Tray App ✅ Recommended



A traditional \*\*Windows service\*\* was evaluated and rejected because:

\- Services run in \*\*Session 0\*\*, isolated from user desktop sessions

\- Drive letters created in Session 0 require `LocalSystem` account and additional registry workarounds (`EnableLinkedConnections`) to be visible to the logged-in user

\- User credentials (Windows Credential Manager) are not accessible from Session 0



A \*\*single-process tray application\*\* auto-started via \*\*Task Scheduler at logon\*\* is the correct pattern, used by rclone mount, SSHFS-Win-Manager, and Cryptomator. It creates drive letters directly in the user's session with zero namespace workarounds.



---



\## 5. System Architecture



```

┌──────────────────────────────────────────────────────────────────┐

│                        GlDrive.exe                               │

│                  (User Session — auto-started at logon)          │

│                                                                  │

│  ┌─────────────────┐        ┌──────────────────────────────────┐ │

│  │   WPF / Tray UI │        │      FTP Filesystem Layer        │ │

│  │                 │        │                                  │ │

│  │  - Tray icon    │◄──────►│  WinFsp FileSystemBase          │ │

│  │  - Context menu │ Events │   ├─ ReadDirectoryEntry()        │ │

│  │  - Settings dlg │        │   ├─ GetFileInfo()               │ │

│  │  - Notif. toasts│        │   ├─ ReadFile()                  │ │

│  └─────────────────┘        │   ├─ WriteFile()                 │ │

│                             │   ├─ CreateFile()                │ │

│  ┌─────────────────┐        │   └─ DeleteFile()                │ │

│  │  Config Manager │        │             │                    │ │

│  │                 │        │  Directory Cache Layer           │ │

│  │  appsettings.   │        │  (in-memory, TTL-based)          │ │

│  │  json           │        │             │                    │ │

│  │  + Windows      │        │  FTP Connection Pool             │ │

│  │  Credential Mgr │        │  (AsyncFtpClient × N)           │ │

│  └─────────────────┘        │             │                    │ │

│                             │  FluentFTP + GnuTLS              │ │

│                             │  (FTPS Explicit, PASV, TLS 1.2)  │ │

│                             └──────────────────────────────────┘ │

└──────────────────────────────────────────────────────────────────┘

&nbsp;                                       │

&nbsp;                              Network (TCP)

&nbsp;                                       │

&nbsp;                   ┌───────────────────▼──────────────────────┐

&nbsp;                   │           glftpd Server (Linux)           │

&nbsp;                   │   Port: <configurable non-standard>       │

&nbsp;                   │   Auth: FTPS Explicit (AUTH TLS)          │

&nbsp;                   │   TLS: OpenSSL 3.x, TLS 1.2               │

&nbsp;                   │   Cert: Self-signed (TOFU)                │

&nbsp;                   └──────────────────────────────────────────┘

```



\### 5.1 Component Responsibilities



\*\*WPF / Tray UI\*\* — All user interaction: tray icon status indicator, context menu (mount/unmount/settings/exit), settings dialog for connection configuration, and Windows toast/balloon notifications for connection events and errors.



\*\*Config Manager\*\* — Reads/writes `appsettings.json` in `%AppData%\\GlDrive\\`. Stores all non-secret configuration. Delegates FTP password storage to the Windows Credential Manager via DPAPI.



\*\*WinFsp FileSystemBase\*\* — Implements the filesystem callback interface WinFsp calls when Windows I/O requests arrive. Translates filesystem operations (open, read, write, delete, list directory) into FTP commands. Manages drive letter assignment and volume metadata.



\*\*Directory Cache Layer\*\* — An in-memory cache (TTL-based, configurable) for directory listing results. Essential because every WinFsp `ReadDirectoryEntry` call that results in a cache miss requires a full FTP data connection open/close cycle.



\*\*FTP Connection Pool\*\* — Maintains a pool of authenticated `AsyncFtpClient` instances. Controls concurrency for parallel read operations. Handles reconnection on stale connections.



\*\*FluentFTP + GnuTLS\*\* — The FTP protocol implementation layer. Handles AUTH TLS handshake, PBSZ/PROT P negotiation, PASV passive mode, LIST parsing, RETR/STOR/DELE/MKD/RMD/RNFR/RNTO commands.



---



\## 6. Functional Requirements



\### 6.1 Drive Mounting



| ID | Requirement |

|---|---|

| FR-01 | The application SHALL mount the configured glftpd server as a single drive letter in Windows Explorer |

| FR-02 | The drive letter SHALL be configurable (A–Z, excluding system-reserved letters) |

| FR-03 | The mounted drive SHALL be accessible from all Win32 applications via standard file paths (e.g., `G:\\section\\release\\`) |

| FR-04 | The drive SHALL appear as a removable drive type in Explorer with a configurable volume label |

| FR-05 | The drive SHALL be unmounted cleanly on application exit, tray menu action, or system shutdown |

| FR-06 | If mounting fails at startup, the application SHALL continue running and retry mount on a configurable interval |



\### 6.2 FTP Operations



| ID | Requirement |

|---|---|

| FR-10 | The application SHALL support file read (download via RETR) |

| FR-11 | The application SHALL support file write (upload via STOR) |

| FR-12 | The application SHALL support file deletion (DELE) |

| FR-13 | The application SHALL support directory creation (MKD) |

| FR-14 | The application SHALL support directory deletion (RMD) |

| FR-15 | The application SHALL support file and directory rename/move (RNFR/RNTO) |

| FR-16 | The application SHALL support directory listing (LIST with Unix-format parsing) |

| FR-17 | The application SHALL support file size reporting (SIZE command) |

| FR-18 | The application SHALL support last-modified timestamp (MDTM command) |

| FR-19 | The application SHALL support resume of interrupted transfers (REST STREAM) |

| FR-20 | The application SHALL use PASV passive mode exclusively; active mode (PORT/EPRT) is NOT supported |



\### 6.3 Directory Caching



| ID | Requirement |

|---|---|

| FR-25 | Directory listing results SHALL be cached in memory with a configurable TTL (default: 30 seconds) |

| FR-26 | The cache SHALL be invalidated for a directory on any write operation within that directory (create, delete, rename) |

| FR-27 | The user SHALL be able to force a full cache flush via tray menu action ("Refresh") |

| FR-28 | Cache TTL SHALL be configurable between 5 and 300 seconds |



\### 6.4 Connection Management



| ID | Requirement |

|---|---|

| FR-30 | The application SHALL maintain a connection pool of reusable authenticated FTP connections |

| FR-31 | Pool size SHALL be configurable (default: 3, range: 1–10) |

| FR-32 | The application SHALL detect stale/dropped connections and re-establish them transparently |

| FR-33 | On network interruption, the application SHALL attempt reconnection with exponential backoff (initial: 5s, max: 120s) |

| FR-34 | During reconnection, the drive SHALL remain mounted but return `ERROR\_NOT\_READY` for I/O operations |

| FR-35 | The user SHALL be notified via tray notification when the connection is lost and restored |



---



\## 7. Non-Functional Requirements



| ID | Requirement | Target |

|---|---|---|

| NFR-01 | \*\*Directory listing latency\*\* | First list (cache miss): ≤2× raw FTP round-trip; cached: <5ms |

| NFR-02 | \*\*File transfer throughput\*\* | SHALL NOT introduce >5% overhead vs direct FTP transfer |

| NFR-03 | \*\*Memory footprint\*\* | Application idle ≤80 MB RAM |

| NFR-04 | \*\*Startup to drive-ready\*\* | ≤10 seconds from process launch to drive letter available |

| NFR-05 | \*\*Crash safety\*\* | If process crashes, WinFsp kernel driver SHALL automatically unmount the drive (no orphan devices) |

| NFR-06 | \*\*x64 only\*\* | x64 Windows 11 target; ARM64 and x86 not required |

| NFR-07 | \*\*Windows 11 minimum\*\* | Windows 11 22H2 or later |

| NFR-08 | \*\*Single instance\*\* | Only one instance of GlDrive SHALL run at a time (mutex-enforced) |



---



\## 8. glftpd Protocol Compatibility Requirements



These requirements address glftpd-specific behaviors discovered during technical research.



| ID | Requirement | Rationale |

|---|---|---|

| GR-01 | The FTP client SHALL use Unix-style LIST output parsing exclusively; MLST/MLSD SHALL NOT be attempted | glftpd does not implement MLSD |

| GR-02 | The client SHALL send `FEAT` on connect and parse the server's feature list | Required to detect server capabilities |

| GR-03 | The client SHALL issue `PBSZ 0` followed by `PROT P` after AUTH TLS to enable data channel encryption | Required for FTPS data transfers |

| GR-04 | The client SHALL handle `421 Service not available` responses by surfacing a clear "IP not whitelisted" error in the UI | glftpd rejects unrecognized IPs before password check |

| GR-05 | The client SHALL handle abrupt TLS connection closure gracefully (no bidirectional SSL shutdown) | glftpd disables `SSL\_CLEAN\_SHUTDOWN` by default since v2.11 |

| GR-06 | The client SHALL support SSCN and CPSV commands if advertised in FEAT | Used for FXP (server-to-server) operations |

| GR-07 | The client SHALL parse glftpd's custom SITE command responses without error | glftpd adds SITE IDLE, SITE STAT, and others |

| GR-08 | The client SHALL handle the glftpd welcome banner without treating status codes as errors | glftpd sends multi-line 220 banners |

| GR-09 | File timestamps SHALL use MDTM response values; LIST timestamp parsing SHALL be used as fallback | Ensures accurate modification times in Explorer |



---



\## 9. TLS / Security Requirements



| ID | Requirement | Rationale |

|---|---|---|

| SEC-01 | The application SHALL use \*\*TLS 1.2\*\* as the preferred/default protocol; TLS 1.3 SHALL be configurable but off by default | glftpd has a known bug where TLS 1.3 upload data connections drop session tickets, preventing session reuse |

| SEC-02 | TLS session reuse SHALL be implemented on data connections via \*\*FluentFTP.GnuTLS\*\* | .NET SslStream does not expose session resumption APIs; GnuTLS provides this |

| SEC-03 | Self-signed certificates SHALL be accepted using \*\*Trust On First Use (TOFU)\*\* semantics | glftpd generates self-signed certificates by default |

| SEC-04 | On first connection, the server certificate fingerprint (SHA-256) SHALL be displayed to the user for manual verification before being stored | User must consciously accept the certificate |

| SEC-05 | On subsequent connections, the certificate fingerprint SHALL be compared against the stored value; mismatch SHALL block connection and alert the user | Prevents certificate substitution attacks |

| SEC-06 | Certificate fingerprints SHALL be stored in `%AppData%\\GlDrive\\trusted\_certs.json` | Persists across restarts |

| SEC-07 | The user SHALL be able to clear stored certificate fingerprints and re-run TOFU verification | Supports planned certificate rotation (glftpd 900-day certs) |

| SEC-08 | FTP passwords SHALL be stored exclusively in the \*\*Windows Credential Manager\*\* via DPAPI | Never stored in plaintext on disk |

| SEC-09 | The application SHALL enforce PASV-mode data connections; the application SHALL NOT open listening sockets for active mode FTP | PASV works through NAT/firewalls; PORT mode does not |

| SEC-10 | Cipher suite selection SHALL be delegated to GnuTLS defaults (`GnuSuite.Secure128` minimum) | Prevents negotiation of weak ciphers |



---



\## 10. Configuration \& Credential Management



\### 10.1 Configuration File



Location: `%AppData%\\GlDrive\\appsettings.json`



```json

{

&nbsp; "Connection": {

&nbsp;   "Host": "ftp.example.com",

&nbsp;   "Port": 1337,

&nbsp;   "Username": "myuser",

&nbsp;   "RootPath": "/",

&nbsp;   "PassivePorts": \[]

&nbsp; },

&nbsp; "Mount": {

&nbsp;   "DriveLetter": "G",

&nbsp;   "VolumeLabel": "glFTPd",

&nbsp;   "AutoMountOnStart": true

&nbsp; },

&nbsp; "Tls": {

&nbsp;   "PreferTls12": true,

&nbsp;   "CertificateFingerprintFile": "trusted\_certs.json"

&nbsp; },

&nbsp; "Cache": {

&nbsp;   "DirectoryListingTtlSeconds": 30,

&nbsp;   "MaxCachedDirectories": 500

&nbsp; },

&nbsp; "Connection": {

&nbsp;   "PoolSize": 3,

&nbsp;   "ReconnectInitialDelaySeconds": 5,

&nbsp;   "ReconnectMaxDelaySeconds": 120

&nbsp; },

&nbsp; "Logging": {

&nbsp;   "Level": "Information",

&nbsp;   "MaxFileSizeMb": 10,

&nbsp;   "RetainedFiles": 3

&nbsp; }

}

```



\### 10.2 Credential Storage



FTP passwords SHALL be stored in the Windows Credential Manager under the target name `GlDrive:<host>:<port>:<username>`. The application SHALL use `Windows.Security.Credentials.PasswordVault` (WinRT) or `System.Security.Cryptography.ProtectedData` (DPAPI) for read/write access. Passwords SHALL NEVER appear in `appsettings.json`, log files, or memory dumps beyond the duration of an active connection.



\### 10.3 Configurable Settings (UI-Exposed)



All settings SHALL be editable via the Settings dialog without requiring manual JSON editing. The Settings dialog SHALL have the following sections:



\- \*\*Connection\*\* — Host, Port, Username, Password (masked), Root path

\- \*\*Mount\*\* — Drive letter picker, Volume label, Auto-mount on start toggle

\- \*\*Security\*\* — TLS version preference, view/clear stored certificate fingerprints

\- \*\*Performance\*\* — Directory cache TTL, connection pool size

\- \*\*Diagnostics\*\* — Log level, open log folder button



---



\## 11. Windows Service / Auto-Start Architecture



\### 11.1 Why Not a Windows Service



A traditional Windows service was evaluated and explicitly rejected for this use case:



\- Services run in \*\*Session 0\*\*, which is isolated from the user's interactive desktop session (Session 1+)

\- Drive letters created from Session 0 are only visible to the logged-in user when running under `LocalSystem` — the highest-privilege service account — requiring additional registry configuration (`HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\System\\EnableLinkedConnections = 1`)

\- Even with `EnableLinkedConnections`, UAC token splitting creates a second DosDevices namespace for elevated processes, causing mapped drives to disappear in elevated command prompts

\- The Windows Credential Manager (used for FTP password storage) is \*\*not accessible\*\* from Session 0 service accounts

\- IPC between a service and a UI tray application adds significant complexity for a single-user personal tool



\### 11.2 Auto-Start via Task Scheduler



The application SHALL be registered as a Task Scheduler task at install time:



```xml

<Task>

&nbsp; <Triggers>

&nbsp;   <LogonTrigger>

&nbsp;     <StartBoundary/>  <!-- any logon -->

&nbsp;     <Enabled>true</Enabled>

&nbsp;   </LogonTrigger>

&nbsp; </Triggers>

&nbsp; <Principals>

&nbsp;   <Principal>

&nbsp;     <RunLevel>LeastPrivilege</RunLevel>  <!-- no elevation -->

&nbsp;   </Principal>

&nbsp; </Principals>

&nbsp; <Settings>

&nbsp;   <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>

&nbsp;   <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>  <!-- no time limit -->

&nbsp;   <RestartOnFailure>

&nbsp;     <Interval>PT1M</Interval>

&nbsp;     <Count>3</Count>

&nbsp;   </RestartOnFailure>

&nbsp; </Settings>

&nbsp; <Actions>

&nbsp;   <Exec>

&nbsp;     <Command>%ProgramFiles%\\GlDrive\\GlDrive.exe</Command>

&nbsp;     <Arguments>--minimized</Arguments>

&nbsp;   </Exec>

&nbsp; </Actions>

</Task>

```



\### 11.3 Single-Instance Enforcement



On startup, the application SHALL acquire a named mutex (`Global\\GlDriveInstance`). If the mutex is already held, the new instance SHALL focus the existing tray icon and exit. This prevents duplicate drive mounts.



\### 11.4 Graceful Shutdown



On Windows shutdown/logoff, the application SHALL receive `WM\_QUERYENDSESSION` / `WM\_ENDSESSION` and perform an orderly unmount before terminating. WinFsp's kernel driver provides crash safety as a backstop if the process is killed before unmounting.



---



\## 12. User Interface Requirements



\### 12.1 System Tray Icon



The tray icon SHALL use distinct visual states to communicate connection status:



| State | Icon appearance |

|---|---|

| Connected / Drive mounted | Green drive icon |

| Connecting / Reconnecting | Animated spinner overlay |

| Disconnected / Error | Red drive icon with X |

| Drive unmounted (manual) | Grey drive icon |



\### 12.2 Tray Context Menu



Right-clicking the tray icon SHALL show:



```

GlDrive — ftp.example.com (G:)

─────────────────────────────

● Connected                     ← status (non-clickable)

─────────────────────────────

&nbsp; Open Drive (G:\\)

&nbsp; Refresh (Clear Cache)

─────────────────────────────

&nbsp; Unmount Drive

&nbsp; Mount Drive                   ← shown when unmounted

─────────────────────────────

&nbsp; Settings...

&nbsp; View Logs...

─────────────────────────────

&nbsp; Exit

```



\### 12.3 Settings Dialog



A WPF window opened from the tray menu. Fields are organized in tabs matching §10.3. Changes take effect after clicking \*\*Save\*\*. A \*\*Test Connection\*\* button SHALL attempt an FTPS handshake and display success/failure with error detail. Settings dialog SHALL NOT require application restart to apply (except drive letter change, which requires remount).



\### 12.4 First-Run Wizard



On first launch (no `appsettings.json` present), the application SHALL present a stepped wizard:



1\. \*\*Welcome\*\* — brief explanation of what GlDrive does

2\. \*\*Connection details\*\* — host, port, username, password, root path

3\. \*\*TLS certificate\*\* — attempt connection, display certificate fingerprint with SHA-256 hash, prompt "Trust this certificate? \[Yes] \[No]"

4\. \*\*Mount options\*\* — drive letter picker, volume label, auto-mount toggle

5\. \*\*Confirm \& Connect\*\* — test connection, show success, optionally open drive in Explorer



\### 12.5 Notifications



The application SHALL use Windows toast notifications for the following events:



| Event | Notification |

|---|---|

| Drive mounted successfully | "GlDrive connected — G: is ready" |

| Connection lost | "GlDrive disconnected — reconnecting..." |

| Connection restored | "GlDrive reconnected" |

| Certificate mismatch | "GlDrive blocked — certificate changed! Open Settings." |

| Reconnect failed (all retries exhausted) | "GlDrive could not reconnect — manual action required" |



Notifications SHALL be suppressible via a Settings option.



---



\## 13. Error Handling \& Resilience



\### 13.1 FTP Error Mapping to Windows I/O Errors



WinFsp callbacks must return `NTSTATUS` codes. The following FTP error conditions SHALL be mapped:



| FTP Condition | NTSTATUS |

|---|---|

| File not found (550) | `STATUS\_OBJECT\_NAME\_NOT\_FOUND` |

| Permission denied (550 + ACL) | `STATUS\_ACCESS\_DENIED` |

| Disk full on server | `STATUS\_DISK\_FULL` |

| Connection lost mid-transfer | `STATUS\_CONNECTION\_ABORTED` |

| Timeout waiting for response | `STATUS\_IO\_TIMEOUT` |

| Authentication failure (530) | `STATUS\_LOGON\_FAILURE` |

| IP mask rejected (421) | `STATUS\_HOST\_UNREACHABLE` |



\### 13.2 Connection Loss During File Transfer



If a connection is lost mid-read: the application SHALL attempt to reconnect and re-issue the RETR command from the last successfully received byte (using REST STREAM). If reconnection fails within 30 seconds, the read SHALL return `STATUS\_CONNECTION\_ABORTED`.



If a connection is lost mid-write: the incomplete upload SHALL be abandoned. The application SHALL NOT attempt to resume partial uploads (STOR does not have a safe resume mechanism without server support). A toast notification SHALL inform the user that the upload was interrupted.



\### 13.3 glftpd-Specific Error Handling



\- \*\*421 IP mask rejection\*\* — Detected on connect; surface a specific error message: "Connection refused: Your IP address is not whitelisted on the glftpd server. Check glftpd's `users` file IP mask configuration."

\- \*\*Abrupt TLS closure\*\* — Handle `SslException` on data connection close without propagating as an error (glftpd disables bidirectional SSL shutdown)

\- \*\*550 on delete of file being uploaded by another user\*\* — Retry once after 2 seconds; if still 550, return access denied



---



\## 14. Installer \& Deployment



\### 14.1 Installer Technology



The installer SHALL be built with \*\*WiX Toolset 4+\*\* generating a standard Windows `.msi` file. MSI is required because WinFsp's kernel driver component is itself distributed as an MSI and requires the WiX Burn bootstrapper to chain prerequisites.



\### 14.2 Installer Chained Prerequisites



The Burn bootstrapper `.exe` SHALL chain:

1\. \*\*WinFsp MSI\*\* (downloaded or bundled) — installs the kernel filesystem driver

2\. \*\*.NET 10 Desktop Runtime\*\* (x64) — if not already present

3\. \*\*GlDrive.msi\*\* — the application files



\### 14.3 Installation Actions



On install:

\- Copy application files to `%ProgramFiles%\\GlDrive\\`

\- Register Task Scheduler task for auto-start at logon (see §11.2)

\- Create Start Menu shortcut

\- Create uninstaller entry in Programs \& Features



On uninstall:

\- Unmount any active drive letter

\- Remove Task Scheduler task

\- Remove application files (user config in `%AppData%\\GlDrive\\` is retained)



\### 14.4 Elevation Requirements



The installer requires elevation (UAC) for:

\- Writing to `%ProgramFiles%`

\- Installing the WinFsp kernel driver MSI



The application itself runs without elevation at runtime (Task Scheduler `LeastPrivilege`). WinFsp's kernel driver is already installed system-wide by the MSI; the user-mode application communicates with it without requiring admin rights.



---



\## 15. Logging \& Diagnostics



| Requirement | Detail |

|---|---|

| Log framework | Serilog with file sink |

| Log location | `%AppData%\\GlDrive\\logs\\gldrive-.log` (rolling daily) |

| Log rotation | Max 10 MB per file, retain last 3 files |

| Default log level | `Information` |

| Debug log level | `Debug` — logs all FTP commands and responses (passwords redacted) |

| Tray log access | "View Logs..." opens the log folder in Explorer |

| FTP command log | At Debug level, all FTP control channel exchanges SHALL be logged with timestamps |

| Performance log | At Debug level, timing for directory listing operations SHALL be logged |

| Sensitive data | Passwords SHALL NEVER appear in logs; they SHALL be replaced with `\[REDACTED]` |



---



\## 16. Out of Scope



The following are explicitly excluded from v1.0:



\- Multiple simultaneous FTP server connections

\- SFTP, FTPS Implicit, plain FTP (unencrypted)

\- FTP Active Mode (PORT/EPRT commands)

\- MLSD/MLST directory listing (glftpd does not support it)

\- FXP (file transfer between two FTP servers)

\- Bandwidth throttling

\- File upload progress bars in Explorer (Windows Explorer does not expose this API)

\- Offline/cached file access when disconnected

\- ARM64 or x86 Windows support

\- Windows 10 support

\- Any form of network share / UNC path access

\- Anti-virus or content scanning integration

\- Automated certificate renewal

\- Command-line interface / headless operation



---



\## 17. Technical Risks \& Mitigations



| Risk | Likelihood | Impact | Mitigation |

|---|---|---|---|

| \*\*TLS session reuse fails with future glftpd updates\*\* | Low | High | GnuTLS add-on handles session reuse at the TLS library level; also configurable fallback to disable PROT P |

| \*\*WinFsp version incompatibility after Windows Update\*\* | Low | High | Pin WinFsp version in installer; monitor WinFsp releases |

| \*\*glftpd certificate uses ECDSA secp521r1\*\* (incompatible with Windows Schannel) | Medium | High | GnuTLS handles this natively; Schannel is not used |

| \*\*Passive port range blocked by client-side firewall\*\* | Medium | Medium | Surface clear error ("PASV connection refused"); document firewall requirement |

| \*\*glftpd IP mask check blocks new client IPs\*\* | High | High | Surface specific 421 error with diagnostic message |

| \*\*Large directory listings (10,000+ files) cause Explorer timeout\*\* | Low | Medium | WinFsp supports paginated `ReadDirectory`; implement lazy listing with chunked responses |

| \*\*FluentFTP.GnuTLS x64-only limitation\*\* | N/A (x64 only target) | N/A | Accepted by design |

| \*\*WinFsp GPLv3 license concern for distribution\*\* | Low | Low | FLOSS exception applies; non-distributed personal use has no copyleft obligation |



---



\## 18. Recommended Technology Stack Summary



| Component | Technology | Version | License |

|---|---|---|---|

| Runtime | .NET | 10 LTS | MIT |

| UI Framework | WPF | .NET 10 | MIT |

| Tray icon | H.NotifyIcon.Wpf | Latest | MIT |

| Filesystem driver | WinFsp (`winfsp.net`) | 2.1+ | GPLv3 + FLOSS exception |

| FTP client | FluentFTP | 53+ | MIT |

| TLS session reuse | FluentFTP.GnuTLS | 1.0.38+ | LGPL 2.1 |

| Logging | Serilog | 4+ | Apache 2.0 |

| Config | Microsoft.Extensions.Configuration | 10 | MIT |

| Installer | WiX Toolset 4 / Burn | 4+ | MS-RL |

| Auto-start | Windows Task Scheduler | Built-in | — |

| Credential storage | Windows Credential Manager / DPAPI | Built-in | — |



---



\## 19. Dependency \& Licensing Summary



All dependencies are open-source or built-in Windows components. The only license requiring attention is WinFsp's GPLv3, which applies copyleft obligations only upon distribution. For personal, non-distributed use, no obligations arise. If the application is ever distributed publicly, the WinFsp runtime MSI (which is the kernel driver) is separately installed rather than linked — the application binary links against `winfsp.net` (MIT-licensed .NET wrapper); the GPLv3 kernel driver itself is installed independently by the WinFsp MSI.



FluentFTP.GnuTLS is LGPL 2.1 — it may be used as a dynamically referenced library without imposing LGPL requirements on the application binary.



---



\## 20. Glossary



| Term | Definition |

|---|---|

| \*\*FTPS Explicit\*\* | FTPS mode where the connection starts as plain FTP on the standard/configured port, then upgrades to TLS via the `AUTH TLS` command. Distinguished from FTPS Implicit (which uses TLS from the first byte on port 990). |

| \*\*glftpd\*\* | GreyLine FTP Daemon — a highly configurable Linux/Unix FTP server with OpenSSL-based TLS support |

| \*\*LIST\*\* | FTP command to retrieve a directory listing in Unix `ls -l` format. The only listing command supported by glftpd. |

| \*\*MLSD\*\* | FTP Machine-Readable Listing — a structured directory listing command (RFC 3659) that glftpd does NOT implement |

| \*\*PASV\*\* | FTP Passive Mode — the client asks the server to open a listening port for the data connection. Works through NAT/firewalls from the client side. |

| \*\*PROT P\*\* | FTP command to enable TLS protection on the data channel (after `PBSZ 0`). Required for FTPS data transfers. |

| \*\*REST STREAM\*\* | FTP restart/resume command — allows transfer to resume from a specified byte offset |

| \*\*TLS session reuse\*\* | TLS optimization where a previously negotiated session's keys are reused for a new connection, avoiding a full handshake. Required by some FTPS servers (including glftpd) for data channel connections. |

| \*\*TOFU\*\* | Trust On First Use — security policy where an unknown certificate is accepted on first encounter and stored; subsequent connections validate against the stored fingerprint |

| \*\*WinFsp\*\* | Windows File System Proxy — an open-source userspace filesystem framework for Windows that enables applications to implement custom virtual filesystems mounted as drive letters |

| \*\*Session 0\*\* | The isolated Windows session in which system services run, separated from interactive user sessions (Session 1+) |

| \*\*DPAPI\*\* | Data Protection API — a Windows API for encrypting data tied to a user account or machine, used here for credential storage |



---



\*End of document\*  

\*GlDrive PRD v1.0 — 2026-02-27\*



