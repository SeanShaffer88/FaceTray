# Release checklist — FaceTray on-machine pass

Run on SEAN's Windows machine (Windows 10 or 11). Items are ticked only when a run or capture actually proves them.

## Field pass — 2026-10-23 (SEAN, v0.1.0, three captures)
Proven by that run:
- [x] App launches on SEAN's machine; ticker opens showing the signed-out state (“You're not signed in to Facebook in FaceTray yet” + Sign in button + footer) — 2026-10-23.
- [x] After sign-in, the ticker lists his real notifications matching Facebook's own menu (same 5 items, order, Today/Earlier grouping, type badges) — 2026-10-23.
- [x] Settings window renders correctly with the expected states (poll interval “Every minute”, Start-with-Windows off, tray pop-up off, session-folder path shown) — 2026-10-23.
Found by that run, fixed in v0.1.1: relative time printed twice per row; avatars all grey placeholders.

## Field pass — 2026-10-23 (SEAN, v0.1.1, three captures + his confirmation)
Proven by that run:
- [x] Avatars show real profile photos on every row — 2026-10-23 capture.
- [x] Relative time appears exactly once per row — 2026-10-23 capture.
- [x] Unread pill filters the list; with nothing unread it shows “No unread notifications.” — 2026-10-23 capture.
- [x] Tray counter bubble correct — confirmed by SEAN 2026-10-23.
- [x] Clicking a notification opens it in the default browser — confirmed by SEAN 2026-10-23.
- [x] Installer works — confirmed by SEAN 2026-10-23 (v0.1.1 installer).

## v0.2.0 verification (SEAN's next run — nothing below is proven yet)
Ticker & reading:
- [ ] Stacked avatars: a group notification (several people, one post) shows up to 3 overlapping faces; single-person rows unchanged.
- [ ] Per-row mark as read: hovering a row shows the ✓ button; clicking it clears the dot and drops the counter without opening the browser; the button is reachable by keyboard.
- [ ] Search: typing a name/text filters the list; clearing restores it; search composes with the Unread pill.
- [ ] Type filters: unticking “Birthdays” (Settings) removes birthday rows from the ticker AND the counter; re-ticking restores them.
- [ ] Deeper history: after several polls the ticker holds more than 20 notifications; “Show more” reveals 20 more at a time down to the cached history.
- [ ] Snooze: snoozing a row for 1 hour makes it read and drops the counter; it resurfaces as unread after the hour (or after “until tomorrow” at 08:00).
Alerts & tray:
- [ ] Rich pop-up: a new notification raises the dark card with avatar + text + time; clicking it opens the notification in the browser; it dismisses itself in ~6 s; the ✕ dismisses without opening.
- [ ] Quiet hours: with quiet hours on and the current time inside the window, a new notification does NOT pop up or sound, but the counter still rises.
- [ ] Sound: with the sound toggle on (and pop-ups on), a new notification plays the system sound; off: silent.
- [ ] Global hotkey: Ctrl+Shift+F toggles the ticker from another app; with the setting off it does nothing; if another app owns the chord, FaceTray still runs (log line only).
- [ ] Tray menu recents: right-click shows “Recent” with up to 5 notifications; clicking one opens it in the browser and marks it read; with 2+ accounts the menu names the active account.
Accounts, settings & data:
- [ ] Multi-account: add a second account in Settings, Save, Switch — FaceTray restarts, asks for that account's sign-in, and shows its notifications; switching back returns to the first account STILL signed in; the original v0.1.x sign-in survives the upgrade untouched.
- [ ] Remove account: removing the non-active account and Saving deletes its session folder and data files (check %LOCALAPPDATA%\FaceTray and %APPDATA%\FaceTray); the active account cannot be removed.
- [ ] Settings export/import: export to a file, change settings, import the file — values are restored after Save; importing a non-FaceTray file is rejected with a message and changes nothing.
- [ ] Adaptive polling: with unread waiting, polls come faster than the configured interval; after several empty polls they slow down (log timestamps show it).
- [ ] Auto-prune: with “Keep history for 7 days”, cached items/read marks older than 7 days disappear after a restart.
Reliability:
- [ ] Update notifier: running an older build against a newer published release shows “Update available” in the tray menu and Settings; the tray entry opens the GitHub release page; with the check off, neither appears.
- [ ] Extraction health: (when Facebook next changes its layout, or by inspection after a bad poll) two mismatched polls in a row raise the “layout may have changed” banner in the ticker; a healthy poll clears it.
- [ ] Diagnostics: Settings → Diagnostics shows version, active account, last poll numbers matching the log; “Copy diagnostics” pastes text containing NO notification text and NO full URLs; “Open log folder” opens Explorer at the log.
Polish & accessibility:
- [ ] Ticker themes: Light looks right (readable rows, badges/dots still coloured); Follow Windows follows a Windows light/dark switch and uses system colours under High Contrast mode.
- [ ] Text size: 150% enlarges ticker text and panel without clipped rows; 100% restores the original size; the setting applies on Save without a restart.

## Install & first run
- [x] Run `FaceTray.exe` — no error, ticker opens showing the sign-in state (2026-10-23 captures; which build route he launched was not recorded).
- [x] Installer installs per-user without admin (v0.1.1 installer confirmed by SEAN 2026-10-23).
- [ ] SmartScreen note: record exact wording shown for the unsigned build.

## Sign-in
- [ ] Click “Sign in to Facebook” — login window loads facebook.com; sign in including the two-factor step.
- [x] After sign-in the ticker fills with real notifications (2026-10-23 capture, matching Facebook's own menu).
- [ ] Quit and relaunch FaceTray — still signed in (no login prompt).
- [x] FaceTray shows the signed-out state with the sign-in button (2026-10-23 capture, pre-sign-in).

## Counter accuracy
- [x] Tray bubble shows the unread count (SEAN confirmed the bubble is correct, 2026-10-23; a per-count 0 / 1–9 / 10+ capture set is still welcome for the README).
- [x] Clicking a notification opens it in the default browser (SEAN confirmed, 2026-10-23).
- [ ] “Mark all read” (tray menu and ticker `···` menu) — bubble disappears; facebook.com itself still shows its own read state (expected difference, see DECISIONS.md).

## Ticker fidelity vs SEAN's reference capture
- [ ] Panel colour, header, All/Unread pills match the capture; Unread pill filters instantly.
- [ ] Today / Earlier sections; “See all” and “See previous notifications” open facebook.com/notifications in the browser.
- [ ] Rows: 56px circular avatars, type badges are green (comment/mention), blue (like), pink (birthday), grey (other); bold actor names; relative times; blue dot only on unread rows; hover highlight.
- [ ] Esc and click-away close the popup; arrow keys + Enter work through rows; second FaceTray launch toggles the popup.

## Polling & settings
- [ ] Each poll interval (30s / 60s / 2min / 5min) actually re-polls; Pause stops polling, Resume restarts it.
- [ ] Pop-ups on: a new notification raises one pop-up card (v0.2.0 replaced the tray balloon); pop-ups off: none. (Detailed v0.2.0 pop-up checks are in the v0.2.0 section above.)
- [ ] Start with Windows on: sign out/in of Windows — FaceTray starts quietly in the tray (`--background`); off: it does not.
- [ ] Settings → Clear data: cache and read marks reset; app keeps working; sign-in survives (documented).

## Screenshots (wire into README when captured)
- [ ] Tray icon at 0 / 3 / 9+ unread (taskbar corner, 200% crop)
- [x] Ticker popup (privacy-blurred version wired into README as docs/screenshots/ticker.png; Unread-empty state as ticker-unread.png — 2026-10-23)
- [ ] Login window
- [x] Settings window (captured 2026-10-23; wired into README as docs/screenshots/settings.png)

## Uninstall
- [ ] Uninstall keeps `%APPDATA%\FaceTray` and the session folder when answered No; removes them when answered Yes; autostart entry removed either way.
