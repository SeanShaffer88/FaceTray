# Changelog

All notable changes to FaceTray are recorded here. Versions follow semantic versioning.

## [0.3.0] — 2026-10-24

The second “all 20 enhancements” release: pins and folded repeats for triage, alert rules that respect you (per-type rules, Focus Assist, one gentle re-alert, a morning digest), three fallback extraction strategies with session-state detection and a self-healing renderer, a full backup/restore, portable mode, and a native ARM64 build. **All v0.3.0 runtime behaviour is Unverified** — it has been built, unit-tested (265 Core tests) and CI-gated, but has not run on a real Windows desktop yet; it will be verified in the same on-machine pass as v0.2.0 (RELEASE_CHECKLIST.md).

### Reading & triage
- **Pins** — pin a notification (hover 📌, right-click, or the `p` key) and it lives in a Pinned section above Today, exempt from the automatic history prune.
- **Undo mark-all-read** — after “Mark all read”, the ticker footer offers Undo for a few seconds; it restores exactly the notifications that action newly marked.
- **Row context menu, consolidated** — Open in browser / Copy link / Mark as read / Snooze 1 hour / Snooze until tomorrow / Pin or Unpin.
- **Power keys** — with the ticker open and the search box not focused: `j`/`k` move between rows, Enter opens, `m` marks read, `s` snoozes an hour, `p` pins/unpins, `x` expands a folded group. The list is shown in the `···` menu.
- **Exact-time tooltips** — when Facebook exposes a machine-readable time for a row, hovering shows it (“Wed 21 Oct 2026, 16:32 (3h ago)”); otherwise the relative label stands alone.
- **Open all unread** — the `···` menu can open every unread notification of the active account in the browser (with a confirmation past five, which says it ignores the ticker's filters) and marks them read.
- **Collapse repeats** — repeat notifications about the same post fold into one row with a ×N chip; click the chip or press `x` to expand them inline.

### Alerts
- **Per-type alert rules** — Settings → “Alert me about”: pop-ups only for the kinds you tick (comments & mentions, likes & reactions, birthdays, everything else). The pop-up sound follows the same decision.
- **Focus Assist respected** — when Windows says notifications are not welcome (Focus Assist, presentation mode, a fullscreen game), pop-ups and sounds hold off while counting continues.
- **One gentle re-alert** — a comment or mention still unread after 30 minutes earns exactly one more pop-up. Never twice for the same notification.
- **Morning digest** — optional: one pop-up at your chosen time summarising what arrived since the last digest, instead of an overnight drip-feed.
- **Per-account bubble colour** — each account picks its tray counter colour (red, orange, green, blue, purple, pink), so the tray tells you which account has news.

### Robustness
- **Fallback extraction strategies** — the reader now tries three semantic strategies in order (notification-link anchors, then post/story permalinks carrying a time label, then structured rows), so one Facebook markup change has to break all three. Settings → Diagnostics reports which strategy is in use and how often each has fired.
- **Session-state detection** — FaceTray now tells a Facebook “confirm it's you” checkpoint apart from a sign-out, and the ticker says exactly that (with a button into the sign-in window) instead of pretending you are logged out.
- **Renderer self-healing** — if Facebook's embedded view crashes, FaceTray rebuilds it and resumes polling (up to three times per session, with a pause between attempts), keeping your sign-in.
- **Staleness line** — the ticker footer always says how fresh the list is (“Updated 5 min ago”, or “Last updated 26 min ago — showing saved notifications” when it could not refresh).

### Data & platform
- **Full backup & restore** — Settings can save one backup file holding the settings plus every account's read marks, snoozes, pins and cached notifications, and restore it (with a confirmation; FaceTray restarts to apply). Backups never contain your Facebook sign-in.
- **Portable mode** — create an empty `portable.txt` next to `FaceTray.exe` and everything (settings, history, sign-ins) lives in a `FaceTray-data` folder beside it: run FaceTray from a USB stick and take it with you.
- **ARM64 build** — a native win-arm64 build joins CI and the downloads, for Snapdragon and other ARM64 Windows machines.

## [0.2.0] — 2026-10-24

The “all 20 enhancements” release: a deeper, quieter, more capable ticker, multiple accounts, and the safety nets for the day Facebook changes its layout. **All v0.2.0 runtime behaviour is Unverified** — it has been built, unit-tested (197 Core tests) and CI-gated, but none of the new behaviour has run on a real Windows desktop yet; RELEASE_CHECKLIST.md carries the on-machine pass that promotes it.

### Ticker & reading
- **Stacked avatars** — group notifications now render up to three overlapping circular avatars, like Facebook's own menu, instead of only the first.
- **Per-row mark as read** — hovering (or keyboard-focusing) a row shows a ✓ button that marks just that notification read locally and updates the counter, without opening the browser.
- **Search** — a search box under the header filters the list by person or text, on top of the All/Unread pills.
- **Type filters** — Settings can hide Comments & mentions, Likes & reactions, Birthdays, or everything else from the ticker *and* the counter.
- **Deeper history** — the local history now keeps up to 100 notifications (was the latest 20); the ticker shows 20 at a time with a “Show more” button.
- **Snooze** — each row (hover clock button or right-click) can be snoozed for 1 hour or until tomorrow morning; snoozed items read as read, stop counting, and resurface as unread when the snooze ends.

### Alerts & tray
- **Rich pop-up** — the plain tray balloon is replaced by a small Facebook-dark card (avatar, text, time, “+N more”), click to open the notification, auto-dismisses in ~6 seconds.
- **Quiet hours** — a nightly window (default 22:00–07:00, off until enabled) suppresses pop-ups and sounds while polling and counting continue.
- **Sound cue** — optional system sound with the pop-up, off by default.
- **Global hotkey** — Ctrl+Shift+F toggles the ticker from anywhere (can be turned off; if another app owns the chord, FaceTray logs it and carries on).
- **Recent in the tray menu** — the right-click menu now lists the five most recent notifications; clicking one opens it in the browser and marks it read.

### Accounts, settings & data
- **Multiple accounts** — add up to 8 Facebook accounts, each with its own sign-in session and history; the original account keeps its existing sign-in untouched. Switching restarts FaceTray into the chosen account. Removing an account deletes its session and local data.
- **Settings export/import** — back up all settings to a JSON file and restore them on another PC (no sign-in data is ever in settings).
- **Adaptive polling** — checks sooner while unread notifications wait, and relaxes after a run of quiet polls.
- **Auto-prune** — history, read marks and snoozes older than 7/30/90 days (your choice, default 30) are pruned automatically; older files from v0.1.x load safely.

### Reliability
- **Update notifier** — once per launch FaceTray checks GitHub for a newer release (can be turned off); an update adds a tray-menu entry and a note in Settings.
- **Extraction health warning** — if Facebook's page stops matching what the reader expects (title count vs rows found), the ticker shows a slim “Facebook's layout may have changed” banner instead of silently going stale, and the mismatch is logged.
- **Diagnostics panel** — Settings shows version, active account, last poll result and extraction health, with “Open log folder” and “Copy diagnostics” (counts and versions only — never notification text or full URLs).

### Polish & accessibility
- **Ticker themes** — Facebook dark (default), a Light palette, or Follow Windows (light/dark plus real high-contrast system colours).
- **Ticker text size** — 100% / 125% / 150% scaling for ticker text, applied live from Settings.

## [0.1.1] — 2026-10-23

Field fixes from SEAN's first on-machine run of v0.1.0.

- **Fixed: relative time printed twice in every ticker row** (field report). The scraped row text ends with the same relative-time token (“9h”, “1d”) that the ticker prints on its own line; `NotificationItem.Create` now strips the trailing time from the text (`StripTrailingTime`), and cached v0.1.0 entries are healed on load.
- **Improved: avatar capture for Facebook's SVG / background-image profile pictures** (field report — avatars rendered as grey placeholders). The extraction script now also reads `<img>` `currentSrc`/`srcset`, SVG `<image>` hrefs, and `background-image` URLs, alongside plain `<img src>`; an avatar that is captured but fails to load is now logged (host + path only) to separate capture failures from download failures. *Unverified until SEAN's next run.*
- **Field verification (v0.1.0, 2026-10-23):** SEAN ran v0.1.0 on his Windows machine — the app launches, the signed-out ticker state renders as designed, after sign-in the ticker listed his real notifications matching Facebook's own menu item-for-item, and the Settings window renders with the expected states (captures on file).

## [0.1.0] — 2026-10-23

First release.

- Tray icon with a dynamically rendered red unread-counter bubble (1–9, `9+`) and an unread-count tooltip.
- Facebook-style notification ticker: “Notifications” header with `···` menu, All / Unread pills, Today / Earlier grouping, 56px circular avatars with coloured type badges (comment/mention, like, birthday, other), blue unread dots, “See previous notifications” footer.
- Click a notification to open it in the default browser; it is marked read locally and the counter updates immediately.
- Embedded WebView2 sign-in (persistent per-user profile, two-factor friendly) and a hidden polling view over `facebook.com/notifications`; extraction uses semantic selectors only, with the page-title count as fallback.
- Poll interval settings (30s–5min), Pause/Resume, optional new-notification tray pop-up (off by default), optional Start with Windows, local cache so the ticker shows the last-good list offline.
- Right-click tray menu, single-instance with second-launch ticker toggle, `--background` quiet start, `--smoke-test` CI mode.
- Per-user NSIS installer script with data-preserving uninstall.
- Windows runtime behaviour is **Unverified** at release time — see RELEASE_CHECKLIST.md for the on-machine pass that promotes it.
