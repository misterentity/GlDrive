# Collection gaps

Open **Wishlist → Collection gaps**. Selecting a movie with a TMDB ID fills the ID field; otherwise enter a positive movie TMDB ID. Opening the window performs no lookup. Choose **Refresh collection** to fetch that movie's collection and compare its films with the existing library index, downloads and wishlist.

Refresh is explicit and bounded: at most two TMDB GET requests, with finite response and request limits. No timer, FTP traffic or background polling is added. TMDB uses the existing API key. Offline, keyless or unusable responses report **Unknown**, and an explicit provider null reports no collection. A usable partial response is labelled **Partial** and includes only validated members; it never claims the entire collection is present.

Each known film is **Owned**, **Downloading**, **Wanted**, **Missing** or **Unknown**. Positive ownership requires a good copy at any resolution, with the normal movie source-floor rules; low-source copies do not satisfy ownership. **Missing** requires a complete local index. The window shows when the index was refreshed and whether its evidence is complete. Existing paused or inactive wishlist entries still count as wanted and are never silently reactivated.

Choose **Refresh library index** to read the configured movie/TV library folders and selected Plex server. This works without enabling the control API and runs away from the UI thread. It updates the current collection comparison without fetching TMDB again. A successful folder scan can be complete when Plex is unconfigured; a configured but unavailable Plex server leaves the evidence incomplete. No files are changed by the scan.

Use **Add to wishlist** on a missing film with a known release year. Unknown ownership and undated films cannot be added through collection gaps; refresh the index to resolve incomplete ownership evidence. This prevents a broad, yearless wish from matching an older film with the same title. Add rechecks the current cached collection membership, library/download evidence and wishlist identities. It uses the normal wishlist add path with the current default quality. C4 source permissions are preserved; there is no low-source override. Adding a film schedules no immediate provider refresh and does not bypass normal wishlist matching.

Automation uses the same service:

- `collections.status`: read the last comparison without network work.
- `collections.refresh` with `movieTmdbId`: explicitly refresh one collection.
- `collections.add` with the original `movieTmdbId` and a known `filmTmdbId`: add one dated, confirmed missing member. Unknown ownership, undated members, stale windows and unknown member IDs are rejected. Use `library.index_refresh` to update ownership evidence explicitly.

These are manual actions, so no new global decision setting or automatic rate limit is introduced. Collection comparisons do not expose local file paths. IMDb-to-TMDB resolution, automatic discovery across the whole library, Plex collection-tag edits and remote release recommendations remain outside this safe core.
