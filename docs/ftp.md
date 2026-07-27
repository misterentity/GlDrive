---
title: FTP — FTPS connections, pooling, CPSV, GnuTLS
domain: ftp
status: active
last-reviewed: 2026-07-27
verified-against:
  - source: read at git 161d19f on 2026-07-27
---

# FTP — FTPS connections, pooling, CPSV, GnuTLS

> **What's in this doc:** the FTPS client stack (FluentFTP + GnuTLS), the bounded connection pool with ghost-kill, per-account login gating, CPSV data connections for glftpd-behind-a-BNC, the serialized-GnuTLS crash fix, and streaming downloads.
>
> **What's NOT:** how listings are cached and presented as a drive (→ [[filesystem]]); FXP site-to-site transfers, which borrow pool connections but negotiate their own data channels (→ [[spread]]); TOFU certificate trust (→ [[config]]).

## Client factory

`FtpClientFactory` (`Ftp/FtpClientFactory.cs:13`) builds FTPS clients using FluentFTP with the GnuTLS provider (or a SOCKS5 proxy variant when configured). GnuTLS quirks that are load-bearing:

- `GnuAdvanced.NoTickets` + `PreferTls12` work around glftpd's TLS 1.3 session-ticket bug. `GnuAdvanced` enum values go directly in the `AdvancedOptions` list.
- Every client's stream is wrapped in `SerializedGnuTlsStream` via `Config.CustomStream` (see [[ftp#gnutls-crash-fix]]).

### Ghost connections

A BNC keeps stale sessions ("ghosts") alive after a dropped client. `FtpClientFactory.KillGhosts` (`Ftp/FtpClientFactory.cs:203`) logs in with a `!username` prefix to clear them. The pool calls this automatically when it can't create a new connection (`Ftp/FtpConnectionPool.cs:619`, `:642`). **Rapid reconnects trigger a ~2h BNC cooldown**, so ghost-kill is a last resort, not a routine step.

## Connection pool

`FtpConnectionPool` (`Ftp/FtpConnectionPool.cs:10`) is a bounded `Channel<AsyncFtpClient>` pool.

- **Poisoning:** a connection whose GnuTLS stream may be corrupt is marked `Poisoned` and `Discard`ed instead of `Return`ed, preventing reuse of a bad stream.
- **Exhaustion:** `IsExhausted` (`Ftp/FtpConnectionPool.cs:416`) is true when the pool is connected but every connection is poisoned/discarded (`_created <= 0 && _active <= 0`). `Reinitialize` (`Ftp/FtpConnectionPool.cs:455`) revives such a dead pool.
- **Fail-fast:** the pool throws `InvalidOperationException` when `_created <= 0` rather than hanging on an empty channel. `_created` is clamped at 0 because `Reinitialize` resets it while pre-reinit connections are still draining (`Ftp/FtpConnectionPool.cs:806`).

## Login gate

`ServerLoginGate` (`Ftp/ServerLoginGate.cs:43`, registry at `:175`) accounts for a server's concurrent-login cap. Dave's accounts allow **4 logins**; the main pool, an FXP transfer, and a scan all consume against that budget, so over-subscription causes BNC cooldowns. The gate is the single source of truth for "can I open another connection right now" — the racing engine reserves permits through it (see [[spread#login-budget]]).

## CPSV data connections

This is the most intricate part of the FTP layer. glftpd behind a BNC needs **CPSV** instead of PASV for data connections, and FluentFTP has no native CPSV. `CpsvDataHelper` (`Ftp/CpsvDataHelper.cs:21`) implements it manually:

1. Send `CPSV`, parse the backend `IP:port` from the reply (`Ftp/CpsvDataHelper.cs:64`). The reply carries a **server-controlled IP** — loopback and other unsafe targets are refused (`Ftp/CpsvDataHelper.cs:58`).
2. Open a raw TCP socket to that backend address (different from the control host).
3. Send the data command (`LIST`/`RETR`/`STOR`) on the control channel.
4. Negotiate TLS **as the server** (`AuthenticateAsServerAsync`) — with CPSV, glftpd does `SSL_connect` on the data channel, i.e. it is the TLS client.
5. Use a lazily-created self-signed RSA-2048 cert for the data-TLS server role (glftpd doesn't validate it; the validity window is deliberately loose — `Ftp/CpsvDataHelper.cs:37`).

`FtpOperations` (`Ftp/FtpOperations.cs`) routes each operation through either standard FluentFTP or `CpsvDataHelper` based on the server's detected capability.

## GnuTLS crash fix

<!-- verified-against: read Ftp/SerializedGnuTlsStream.cs at git 161d19f on 2026-07-27 -->

The historically #1 reliability bug: `GnuTlsInternalStream` overrode only synchronous `Read`, so the default `Stream.ReadAsync` dispatched an **uncancellable native recv** to the thread pool. When teardown then freed the GnuTLS session under that live recv, the process took an access-violation crash (Windows Event 1026, no managed stack).

`SerializedGnuTlsStream` (`Ftp/SerializedGnuTlsStream.cs:40`) is the fix — a drop-in `IFtpStream` for `Config.CustomStream` that composes the real `GnuTlsStream` and shares one `SemaphoreSlim` (`Ftp/SerializedGnuTlsStream.cs:43`) between every native read/write **and** `Dispose`. Recv and `sess.Dispose()` are therefore mutually exclusive. `Dispose` waits up to `DisposeDrainTimeout` = 20s (`Ftp/SerializedGnuTlsStream.cs:63`) for an in-flight recv; on timeout it **leaks the session rather than freeing it under a live read**. Confirmed: 0 Event-1026 in a 24h run under 1111 FXP transfers, down from 6–12 native crashes/day.

`GnuTlsReflectionGuard` (`Ftp/GnuTlsReflectionGuard.cs`) protects the reflection the pool uses to reach the stream's `BaseStream` field, verifying it's our field before poking it.

**Never re-enable `Config.Noop`** — the pool owns keepalive; the GnuTLS NOOP daemon races teardown.

## Streaming downloads

`StreamingDownloader` (`Ftp/StreamingDownloader.cs`) streams FTP → disk with resume support, for files too large to whole-file buffer through [[filesystem]]. FluentFTP v53's `OpenRead` resume parameter is **positional** (`path, FtpDataType.Binary, restart`), not the named `restartPosition`. It is consumed by the download manager (→ [[downloads]]).
