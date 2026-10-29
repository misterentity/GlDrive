# Problems and health checks

Open **Problems** from the dashboard or tray. The count also appears in the dashboard status strip. Checks run in the background after startup, after settings saves, and every five minutes; **Refresh checks** reads the current evidence immediately.

Checks use existing state and configuration:

- Sustained FTP outages while health tracking is enabled, with positive timeout or unreachable-host evidence lasting at least 30 minutes and an escalated retry ladder. Initial retries, BNC cooldowns, login limits and local capacity holds are not reported as dead sites. Outages offer review instructions, not an automatic mounting-disable fix.
- Missing local download drive roots. Network paths, mapped network drives and failed readiness reads remain unknown and are never probed.
- An explicit Verbose or Debug disk-log setting. The check never changes your choice automatically or guesses how long it has been enabled.
- Destination folders outside configured search roots and crawl depth.
- A notification monitor that observed zero categories at its current watch path.
- Enabled announce rules targeting unconfigured or unjoined channels.
- Existing learned announce expectations with an observed silence episode.
- Pending agent proposals still affected by a freeze, and pending certificate-change decisions.
- Duplicate drive letters on enabled drive mounts.

Findings include evidence and a suggested action. **Preview fix** shows the exact configuration change, affected server settings, remount and IRC restart classification, and runtime effect; **Apply preview** is a separate explicit click. The doctor rechecks the condition and settings revision, then uses the normal validated configuration save and runtime refresh, attributed as `settings-ui:problems`. Every settings save refreshes runtime settings for all servers and may resume due mount retries for enabled, unmounted servers, subject to the existing retry and login limits. A changed revision requires a fresh preview. Frozen settings show their freeze note; an explicit settings edit does not remove the freeze. If a complete redacted preview cannot be shown, use Settings to review the change.

The available fixes set disk logging to Information, add a known destination to search roots, or enable AutoJoin for an existing configured channel. Applying a search-root fix restarts that server's crawler and immediately rebuilds the whole index across all configured roots, using the main pool shared with downloads and the release monitor. Channel changes disclose their join/invite activity before applying. Checks themselves issue no network requests. Conditions requiring an unknown path, credential repair, certificate trust or an agent decision provide review instructions instead of inventing a fix. Certificate acceptance remains exclusive to the local **Certificate decisions** window opened from Settings.

`problems.list` returns the cached redacted findings, their check timestamp, and `partial` and `bounded` flags. It cannot apply fixes, clear freezes or accept certificates. A partial result means some evidence was unavailable or incomplete; it is not a clean bill of health. Frozen decisions use the newest 256 KiB / 1,000 audit lines. This normal window sets `bounded` without marking the checks unavailable or asking for another refresh. Malformed rows or reaching the 64-target result cap still set `partial`; malformed optional payloads do not discard otherwise usable rows. The panel returns at most 256 findings, and older evidence outside those bounds is not inferred to be absent.

The first version does not learn new race-rate/disk-exhaustion trends, fetch expected episode dates, or provide per-finding snoozes. Existing passive announce baselines provide the expectation/silence checks. Diagnostics failures retain the prior in-memory findings and never hold logins, racing, IRC or downloads.
