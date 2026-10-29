# Local recovery and portable backups

Open **Settings → Diagnostics → Recovery Center** for local credential presence,
recorded key ages and preserved recovery files. Values and digests are never shown.
Unknown credential ages are labelled unknown. A protected-copy timestamp is the
copy time, not the date the secret was rotated. Viewing or refreshing this page
does not connect to a site or change settings.

The initial secret migration is deliberately staged. `dataStewardship.copyConfigSecrets`
defaults to `true`: startup copies configured control and channel secrets into a
DPAPI-protected recovery file and reads it back. Failure leaves the original values
alone and warns once. Identical values are not rewritten. The current config remains
authoritative. **Removal from appsettings.json is deferred** so older applications
and MCP bridges continue to find their secrets after a downgrade. Existing snapshots
and backups can still contain plaintext secrets; this change does not rewrite them.

`dataStewardship.guardFeedPreferences` defaults to `false`. When enabled, saved feed
preferences retain up to three last-good generations, and an unexplained loss of
more than half their records preserves the rejected candidate. Explicit removal and
unmuting remain immediate and can save an empty view. Corrupt files are copied to a
`.corrupt` file and tracking starts empty; write failures retain the working state
in memory. Generation copies run in the background. This first adoption is confined
to feed preferences, which do not control downloads, joins or races. No primary file
format changes, automatic generation restores or new engine admission limits are introduced.

FiSH entries are validated individually: an invalid entry is skipped with a warning,
and valid keys retain their timestamps. Empty targets and keys cannot be set.
An unreadable store is copied and verified byte-for-byte before its primary is moved
out of the active filename pattern. Pending keys are then promoted into a verified,
readable primary. If moving fails, new keys persist in `.pending`; inventories may
skip damaged bytes only when an identical quarantine copy exists. Pending keys also
load when the primary is missing. The Problems panel and Recovery Center report the
degraded state. The original primary format stays compatible with previous builds,
and unknown key material remains preserved for local recovery.

## Portable backup

Choose a new output filename and enter a passphrase of at least 12 characters twice.
Export requires an exact confirmation match. Invalid FiSH entries are skipped and
counted while valid entries remain exportable. Backups use
Argon2id (64 MiB, three passes, one lane) and AES-256-GCM with fresh salt and nonce.
The passphrase and derived key are never saved. Every export is reopened,
authenticated and validated before its final filename is published.

The bundle includes settings, full FiSH entries and configured FTP, IRC, proxy,
API and alert credentials. It does not include SSH installer credentials, trust
pins, queues, histories, private messages, downloads or caches. Unattended scheduling
and full-profile restore are deferred; losing the passphrase makes a bundle unusable.

Import requires **Restart into offline recovery**, which starts no engine services.
Review the backup timestamp and counts, then confirm the import. By default every
existing local secret is retained. Replacing existing values, including newer ones,
requires the separate replacement checkbox and confirmation. The confirmed file
is identified by its authenticated digest so replacing it while the prompt is open
cannot change what is imported.

Settings pass through the normal validator and attributed save path. Current hook
definitions and control access policy are retained. Secret writes are verified and
partial failures are reported for retry; import never deletes vault entries. Each
server's FiSH entries are staged together in one verified write, retaining a unique
restricted pre-import copy that retries cannot overwrite. Restart
normally only after reviewing the result. The Recovery Center does not offer secret
values or backup import through MCP or the automation UI tree.

If the current settings file is unreadable, offline repair requires the explicit
replacement choice because its secret values cannot be compared. The damaged source
is copied byte-for-byte to a unique restricted recovery file and verified before
saving. An intervening settings edit cancels the repair.

Default `dataStewardship`, `recovery` and `controlApi.security` sections are omitted
from saved settings so the preceding release's strict MCP config tools keep working
after a downgrade. The current config tools still expose all defaults and accept
normal edits to these settings. Explicit local rollback also removes nondefault
versions of those sections, after preserving and verifying an exact restricted
`.pre-downgrade` copy; the owner token and the other settings remain in place.
If rollback fails, the helper restores the original settings unless they changed
in the meantime. A manual binary downgrade with these features opted in needs the
same settings projection; it is not performed automatically by an older build.
