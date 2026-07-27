---
title: Filesystem — WinFsp mount layer
domain: filesystem
status: active
last-reviewed: 2026-07-27
verified-against:
  - source: read at git 161d19f on 2026-07-27
---

# Filesystem — WinFsp mount layer

> **What's in this doc:** how a glftpd server is presented as a Windows drive letter — the WinFsp `FileSystemBase` implementation, whole-file read/write buffering, the TTL directory cache, and FTP-exception → NTSTATUS mapping.
>
> **What's NOT:** the FTP connection pool and CPSV data channels the filesystem calls into (→ [[ftp]]); per-server mount lifecycle and drive-letter assignment (→ [[services]]); the FXP racing engine (→ [[spread]]).

## Overview

`GlDriveFileSystem : FileSystemBase` (`Filesystem/GlDriveFileSystem.cs:16`) is the WinFsp implementation that backs each mounted drive. One instance is created per server that has a drive letter (see [[services#multi-server-orchestration]]); it translates Windows filesystem calls into FTP operations through [[ftp]] and a TTL cache.

WinFsp's managed API lives in the `Fsp` / `Fsp.Interop` namespaces. `System.IO.FileInfo` collides with `Fsp.Interop.FileInfo`, so the file aliases `using FileInfo = Fsp.Interop.FileInfo;` — keep that alias when touching this layer.

## Read / write buffering

Reads and writes are **whole-file buffered** on the `FileNode` (`Filesystem/FileNode.cs`), not streamed block-by-block. `Open` (`Filesystem/GlDriveFileSystem.cs:194`) and `Create` (`Filesystem/GlDriveFileSystem.cs:246`) set up the node; `Read` (`Filesystem/GlDriveFileSystem.cs:333`) serves bytes from the buffer, and `Write` (`Filesystem/GlDriveFileSystem.cs:385`) accumulates into it. This trades memory for simplicity and works because the access pattern is media playback / whole-file copy, not random-access editing.

For large media that must not buffer whole-file, streaming download is handled outside this layer by `StreamingDownloader` (see [[ftp]]) and the media server (→ [[spread]] sibling subsystems / Player).

## Directory cache

`DirectoryCache` (`Filesystem/DirectoryCache.cs:7`) is a TTL-based `ConcurrentDictionary` with LRU eviction sitting between the filesystem and FTP `LIST` calls. A directory listing is cached for its TTL so repeated Explorer refreshes don't hammer the BNC (which rate-limits — see [[ftp]]). Cache invalidation happens on writes/creates in the affected directory.

## Error mapping

`NtStatusMapper` (`Filesystem/NtStatusMapper.cs:7`) is a static translator from FTP exceptions to NTSTATUS codes. WinFsp callbacks must return an NTSTATUS `int`; an unmapped exception surfaces to the user as a generic Windows error, so new FTP failure modes should get an explicit mapping here rather than falling through to the default.

## TODO

- Document the exact TTL default and LRU eviction bound for `DirectoryCache` (currently unstated here — see `Filesystem/DirectoryCache.cs`). Tracked in [[_backlog]].
