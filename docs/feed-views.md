# Saved release-feed views

The Notifications tab can save the current release text, category and server-display-name filters under a name. The saved-view selector shows an unread count for each view. Selecting a view does not acknowledge arrivals; **Mark view read** acknowledges its current retained, visible items. Changing a filter selects a custom view until it is saved again. Saving an existing name replaces that view's filters.

Read markers use notification identities, so a later arrival with an older or identical timestamp is still unread. Counts and the live dashboard both cover the existing retained feed of 1,000 notifications. Removing a saved view keeps the notifications.

The feed context menu offers **Mute category in feed for this server**. This hides that server/category in all feed views, including unread counts. **Clear feed mutes** reveals them again. Muting does not mark hidden items read. These preferences are applied only to display: they never modify notification polling exclusions, wishlist matching, auto-racing or the alert router.

Preferences and read markers are stored locally in `feed-views.json`. Corrupt state is copied to `.corrupt` and starts empty. A failed write retains the current preferences in memory and warns once. Storage bounds evict the oldest preferences; they never reject a release, login, race or alert. There is no new default acquisition setting or background provider work: every filter, acknowledgement and mute is an explicit user action.

Automation uses the same store: `feed.views`, `feed.save`, `feed.remove`, `feed.mark_read` and `feed.mute`. Set `muted: false` to unmute one server/category. `notifications.list` continues to return the underlying retained feed, unaffected by display preferences. These commands make no FTP, IRC, model or provider requests.
