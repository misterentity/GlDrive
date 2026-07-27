---
title: Documentation Vault — Home
domain: meta
status: active
last-reviewed: 2026-07-27
vault-doctor-ignore-dirs: [superpowers]
---

# Documentation Vault

**For AI agents and humans.** This is the root of the docs vault. Start here.

> If you are an agent working in this repo, read this note first, then the specific domain doc for your task. Do not grep the whole vault — domain docs are designed to be read directly.

---

## Scope

This vault documents **GlDrive** — a Windows 11 tray app that mounts glftpd FTPS servers as local drive letters (WinFsp + FluentFTP + GnuTLS), with an FXP racing engine, download manager, IRC client, and an LLM self-tuning loop. Single app project `src/GlDrive/GlDrive.csproj` (.NET 10 WPF, win-x64) plus `src/GlDrive.Tests`.

Per-feature design history (specs, plans, ADRs under `docs/`, and the curated `docs/changelog.md`) lives alongside this vault and is out of scope for the domain docs.

---

## Domain docs

<!-- One row per domain doc. Add/remove rows as your vault grows. -->

| Task context | Doc |
|---|---|
| Mounting a server as a drive, WinFsp callbacks, dir cache, error mapping | [[filesystem]] |
| FTPS connections, pool, ghost-kill, CPSV, GnuTLS crash fix, login gate | [[ftp]] |
| FXP racing engine, transfer modes, scoring, skiplist, completion | [[spread]] |
| Daily LLM config self-tuning: telemetry → digest → gated validators | [[ai-agent]] |
| Startup, multi-server lifecycle, monitors, watchdog, auto-update | [[services]] |
| Download manager, search, wishlist, archive extraction *(planned)* | [[downloads]] |
| IRC client, FiSH/DH1080 encryption *(planned)* | [[irc]] |
| Config schema, credentials, TLS TOFU *(planned)* | [[config]] |
| Tray, dashboard, settings, theming *(planned)* | [[ui]] |

*Planned docs are mapped in `_meta/doc-ownership.yml` and tracked in [[_backlog]]; not yet authored.*

---

## Cheatsheets

`docs/_cheatsheets/` holds ≤50-line quick-references for high-frequency lookups whose answers are buried in 300+ line domain docs. **The lookup loads the cheatsheet (small) instead of the full doc (large).** That's the context budget you save by maintaining this layer. See `_meta/vault-conventions.md` → Cheatsheets for the trigger rule.

<!-- One row per cheatsheet. Add as repetitive lookups emerge. -->

| Lookup | Cheatsheet | Parent doc |
|---|---|---|
| ... | ... | ... |

## Machinery

- [[vault-conventions]] — the playbook (naming, frontmatter, verification rules)
- [docs/_meta/doc-ownership.yml](_meta/doc-ownership.yml) — code-path → doc map consumed by `scripts/match-docs.mjs`
- [[docs-sync-prompt]] — the workflow followed when you say "update docs"
- [[_backlog]] — gaps, TODO-verify flags, undocumented behavior

---

## Conventions (see [[vault-conventions]] for full detail)

- Filenames: `kebab-case.md`
- Wikilinks: `[[filename]]` or `[[filename#section]]`
- Every doc: frontmatter with `title`, `domain`, `status`, `last-reviewed`, and (for verification-citing docs) `verified-against`
- Code citations include `file:line` anchors
- Schema/API/live-behavior content verified against ground truth at write time
- Archive, don't delete
