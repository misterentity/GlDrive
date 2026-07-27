---
title: Downloads — manager, search, wishlist, extraction
domain: downloads
status: active
last-reviewed: 2026-07-27
---

# Downloads — manager, search, wishlist, extraction

> **What's in this doc:** *(planned)* the per-server download manager and queue, cross-category FTP search, wishlist auto-download, and archive extraction with the give-up classifier.
>
> **What's NOT:** the FTP streaming primitive downloads build on (→ [[ftp#streaming-downloads]]); the FXP racing engine (→ [[spread]]); the WinFsp mount (→ [[filesystem]]).

## Status: planned

This seam is **mapped but not yet authored**. It owns `src/GlDrive/Downloads/**` (17 files) — see the entry in `_meta/doc-ownership.yml` and the scope note in [[_backlog#undocumented-behavior]].

Known load-bearing pieces to cover when authored (verify against code before writing):

- `DownloadManager` + per-server `DownloadStore` (`downloads-{serverId}.json`) — `List<DownloadItem>` + `SemaphoreSlim` queue; resume, speed limiting, exponential-backoff auto-retry, scheduling, SFV verification.
- `WishlistMatcher` — auto-download from `WishlistStore` (`wishlist.json`).
- `FtpSearchService` — parallel category search.
- `ExtractFailureClassifier` — permanent-vs-transient extract failure classification (reworked v3.10.33/.37: incomplete volume sets, truncated payloads, and UnRAR CRC/password exit codes are permanent; open-error exit 6 stays transient).
