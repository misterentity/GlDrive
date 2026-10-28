# IRC attention and local history search

IRC keeps unread message and highlight/PM counts across dashboard closure, reconnects and app restarts. Reading live messages in the active IRC tab acknowledges only those displayed messages; later queued messages remain unread. Choose **Mark read** to clear the selected conversation, including older counts restored after a restart. Hidden tabs, inactive windows and callbacks from closed or replaced views do not clear unread state. Conversation badges retain all unread messages; the IRC tab and status strip count only human highlights and private messages. Incoming messages update only their conversation's counts. Restored unread state cannot recreate channels that are no longer joined.

The optional `ircAttention` settings apply live:

```json
{
  "ircAttention": {
    "nickHighlightsEnabled": true,
    "privateMessageAlertsEnabled": true,
    "keywords": [],
    "ignoredNicks": []
  }
}
```

The two defaults enable passive local notices for human conversation. No login, transfer, acquisition or network-rate decision changes. Keywords are literal whole tokens or phrases, case-insensitive, with IRC nickname case mapping. Both lists allow up to 64 entries of at most 128 characters. Own messages and system lines do not create unread attention. Configured sitebot senders, matched release announces, and channels explicitly targeted by enabled announce rules never highlight or notify. Global announce rules exclude matching lines while allowing ordinary conversation elsewhere. Ignored nicks suppress channel highlights; private messages still remain unread. Invalid optional settings disable notices and preserve their JSON for correction, without resetting the rest of the configuration. Automation rejects invalid edits to this block while allowing unrelated edits.

Mentions and private messages emit at most one notice per conversation every ten minutes, including when `alerts.enabled` is false. The first notice appears immediately; subsequent messages accumulate a count included with the next eligible message's notice. Unread counts continue to include every message. The throttle retains at most 512 conversation entries and evicts the least recently active entry at capacity. Mentions and private messages use the alert router. They are always local-only, even with outbound targets enabled, and are excluded from outbound digests. Notices contain generic text and counts; chat text and conversation names are never written into the plaintext alert inbox. PM history and unread conversation state remain DPAPI-protected. Unread persistence failures retain memory, warn once and never interrupt IRC. Corrupt state is copied to `.corrupt`; the oldest conversation bookkeeping is evicted at capacity.

In **Dashboard → IRC**, choose **Search logs** or press **Ctrl+F**. Search uses the selected server's last 30 days of channel logs. **Include private-message history** is off by default and searches only retained PM history when selected. Searches run in the background, with a 25 ms regex match timeout, a two-second whole-search budget and at most 200 results. The newest matches across channel and PM history are retained and displayed first. Live log writers and retention remain free to append or replace files. Partial results are labelled when a limit or unavailable file prevents a complete search.

Results remain in the local search window. They are never sent to an alert target, automation response or LLM. `irc.status` reports aggregate unread counts and search availability; `irc.search` returns local UI instructions only and accepts no search expression. Existing `irc.history` is unchanged and is not connected to this search feature. Search is explicit, adds no FTP/IRC traffic and changes no history.
