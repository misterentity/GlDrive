---
title: IRC — client, FiSH, DH1080
domain: irc
status: active
last-reviewed: 2026-07-27
---

# IRC — client, FiSH, DH1080

> **What's in this doc:** *(planned)* the IRC client, FiSH (Blowfish) message encryption, DH1080 key exchange, per-server key storage, and scrollback/PM persistence.
>
> **What's NOT:** how IRC announces trigger auto-races (→ [[spread#auto-race-triggers]]); the announce/pattern parsers that live under `Spread/` (→ [[spread]]).

## Status: planned

This seam is **mapped but not yet authored**. It owns `src/GlDrive/Irc/**` (10 files) — see `_meta/doc-ownership.yml` and [[_backlog#undocumented-behavior]].

Known load-bearing pieces to cover when authored (verify against code before writing):

- `IrcClient` (TcpClient + SslStream) and `IrcService` (client + FiSH + DH1080 + auto-reconnect).
- `FishCipher` (Blowfish ECB/CBC via BouncyCastle) and `FishKeyStore` per server (`fish-keys-{serverId}.json`, DPAPI-encrypted).
- `Dh1080` key exchange — canonical key = `std-base64(SHA256(shared))`; faithful `dh1080_b64decode` port; self-healing auto-rekey with corroboration (the FiSH/DH1080 crash arc, v3.10.15→.21).
- Scrollback buffer + DPAPI-encrypted PM history (`pm-history-{serverId}.json`, v3.10.23).
