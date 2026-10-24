# Release checklist — FaceTray on-machine pass

Run on SEAN's Windows machine (Windows 10 or 11). Items are ticked only when a run or capture actually proves them.

## Field pass — 2026-10-23 (SEAN, v0.1.0, three captures)
Proven by that run:
- [x] App launches on SEAN's machine; ticker opens showing the signed-out state (“You're not signed in to Facebook in FaceTray yet” + Sign in button + footer) — 2026-10-23.
- [x] After sign-in, the ticker lists his real notifications matching Facebook's own menu (same 5 items, order, Today/Earlier grouping, type badges) — 2026-10-23.
- [x] Settings window renders correctly with the expected states (poll interval “Every minute”, Start-with-Windows off, tray pop-up off, session-folder path shown) — 2026-10-23.
Found by that run, fixed in v0.1.1: relative time printed twice per row; avatars all grey placeholders.

## v0.1.1 verification (SEAN's next run)
- [ ] Avatars show real profile photos (not grey placeholders) — and if any still fail, the log gains an “Avatar image failed to load” line (captured-but-failed) vs no line (not captured).
- [ ] Relative time appears exactly once per row.

## Install & first run
- [x] Run `FaceTray.exe` — no error, ticker opens showing the sign-in state (2026-10-23 captures; which build route he launched was not recorded).
- [ ] Installer: `FaceTray-Setup-v0.1.0-x64.exe` installs per-user without admin; Start Menu shortcut works.
- [ ] SmartScreen note: record exact wording shown for the unsigned build.

## Sign-in
- [ ] Click “Sign in to Facebook” — login window loads facebook.com; sign in including the two-factor step.
- [x] After sign-in the ticker fills with real notifications (2026-10-23 capture, matching Facebook's own menu).
- [ ] Quit and relaunch FaceTray — still signed in (no login prompt).
- [x] FaceTray shows the signed-out state with the sign-in button (2026-10-23 capture, pre-sign-in).

## Counter accuracy
- [ ] Compare the tray bubble and tooltip against facebook.com's own count at 0, 1–9, and 10+ unread (`9+` shows).
- [ ] Open one notification from the ticker — counter drops by one at once; the item opens in the default browser at the right post.
- [ ] “Mark all read” (tray menu and ticker `···` menu) — bubble disappears; facebook.com itself still shows its own read state (expected difference, see DECISIONS.md).

## Ticker fidelity vs SEAN's reference capture
- [ ] Panel colour, header, All/Unread pills match the capture; Unread pill filters instantly.
- [ ] Today / Earlier sections; “See all” and “See previous notifications” open facebook.com/notifications in the browser.
- [ ] Rows: 56px circular avatars, type badges are green (comment/mention), blue (like), pink (birthday), grey (other); bold actor names; relative times; blue dot only on unread rows; hover highlight.
- [ ] Esc and click-away close the popup; arrow keys + Enter work through rows; second FaceTray launch toggles the popup.

## Polling & settings
- [ ] Each poll interval (30s / 60s / 2min / 5min) actually re-polls; Pause stops polling, Resume restarts it.
- [ ] Toasts on: a new notification raises one tray balloon; toasts off: none.
- [ ] Start with Windows on: sign out/in of Windows — FaceTray starts quietly in the tray (`--background`); off: it does not.
- [ ] Settings → Clear data: cache and read marks reset; app keeps working; sign-in survives (documented).

## Screenshots (wire into README when captured)
- [ ] Tray icon at 0 / 3 / 9+ unread (taskbar corner, 200% crop)
- [ ] Ticker popup — mixed read/unread
- [ ] Login window
- [x] Settings window (captured 2026-10-23; wired into README as docs/screenshots/settings.png)

## Uninstall
- [ ] Uninstall keeps `%APPDATA%\FaceTray` and the session folder when answered No; removes them when answered Yes; autostart entry removed either way.
