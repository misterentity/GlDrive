---
title: Documentation Backlog
domain: meta
status: active
last-reviewed: 2026-07-27
---

# Documentation Backlog

> **What's in this doc:** known documentation gaps, TODO-verify flags, and undocumented code paths. Append-only — items get resolved by removing them when the corresponding doc section is written/verified.
>
> **What's NOT:** finished documentation (those live in the domain docs).

## TODO verify

<!-- Sections in domain docs where verification was skipped (tool unavailable, ambiguous query). Re-verify and remove the entry. -->

- [[filesystem]] — exact `DirectoryCache` TTL default and LRU eviction bound not yet stated; read `src/GlDrive/Filesystem/DirectoryCache.cs` and fill in.

## Undocumented behavior

<!-- Code paths or features that exist but have no domain doc section. Add an entry when noticed; resolve by writing the section. -->

Four domain seams are mapped in `doc-ownership.yml` but not yet authored (vault-init seeded the 5 highest-traffic seams only):

- **downloads** — `src/GlDrive/Downloads/**` (17 files): `DownloadManager` + per-server `DownloadStore`, queue/resume/speed-limit/scheduling, SFV verify, `WishlistMatcher`, `FtpSearchService`, and `ExtractFailureClassifier` (the extractor give-up logic reworked in v3.10.33/.37).
- **irc** — `src/GlDrive/Irc/**` (10 files): `IrcService`/`IrcClient`, FiSH (`FishCipher`), DH1080 key exchange (`Dh1080`), `FishKeyStore`, scrollback + DPAPI PM history.
- **config** — `src/GlDrive/Config/**` + `src/GlDrive/Tls/**`: `AppConfig`/`ConfigManager` (camelCase JSON, single→multi-server migration), `CredentialStore`, `CertificateManager` TOFU.
- **ui** — `src/GlDrive/UI/**` (30 files): `TrayViewModel`, `DashboardWindow`, `SettingsWindow`, `WizardWindow`, `ExtractorWindow`, `ThemeManager`.

## Unmatched files

<!-- Files appearing in `match-docs.mjs` output that don't map to any doc-ownership.yml entry. Resolve by either adding an ownership entry or confirming the file legitimately doesn't belong to any doc. -->

- _none yet_
