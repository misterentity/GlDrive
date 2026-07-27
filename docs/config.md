---
title: Config — settings, credentials, TLS trust, file locations
domain: config
status: active
last-reviewed: 2026-07-27
---

# Config — settings, credentials, TLS trust, file locations

> **What's in this doc:** *(planned)* the `AppConfig` schema and migration, secret storage in Windows Credential Manager, TOFU certificate trust, and the on-disk file-location map.
>
> **What's NOT:** how the AI agent mutates config through validators (→ [[ai-agent#change-gating]]); what each subsystem does with its settings (→ the relevant domain doc).

## Status: planned

This seam is **mapped but not yet authored**. It owns `src/GlDrive/Config/**` and `src/GlDrive/Tls/**` — see `_meta/doc-ownership.yml` and [[_backlog#undocumented-behavior]]. Full detail (schema, migration, validator invariants) to be written by reading the code.

Known pieces to cover: `AppConfig` (`List<ServerConfig>` + global `DownloadConfig`/`LoggingConfig`), `ConfigManager` (camelCase JSON, single→multi-server auto-migration), `CredentialStore` (Credential Manager), `CertificateManager` TOFU.

## TLS TOFU

`CertificateManager` (`src/GlDrive/Tls/`) implements trust-on-first-use with SHA-256 fingerprints stored in `trusted_certs.json`, keyed by `host:port` globally. *(Stub — expand with `file:line` citations when authored.)*

## File locations

All under `%AppData%\GlDrive\` unless noted. Resolved via `ConfigManager.AppDataPath`.

| What | Path |
|---|---|
| App config | `appsettings.json` (camelCase) |
| Trusted certs | `trusted_certs.json` |
| Downloads (per server) | `downloads-{serverId}.json` |
| Race history | `race-history.json` |
| Wishlist | `wishlist.json` |
| Extractor settings | `extractor-settings.json` |
| FiSH keys | `fish-keys-{serverId}.json` (DPAPI) |
| PM history | `pm-history-{serverId}.json` (DPAPI) |
| AI telemetry / briefs | `ai-data/`, `ai-briefs/` |
| Logs | `logs/gldrive-{date}.log` |
| Heartbeat | `logs/last-heartbeat.json` |
| Update markers | `.update-deferred`, `.update-attempt`, `.updating`, `.update-auth` |
| Credentials | Windows Credential Manager, `GlDrive:{host}:{port}:{username}` and `GlDrive:api:{service}` |
