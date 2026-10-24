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
- [ ] Toasts on: a new notification raises one tray balloon; toasts off: none.
- [ ] Start with Windows on: sign out/in of Windows — FaceTray starts quietly in the tray (`--background`); off: it does not.
- [ ] Settings → Clear data: cache and read marks reset; app keeps working; sign-in survives (documented).

## Screenshots (wire into README when captured)
- [ ] Tray icon at 0 / 3 / 9+ unread (taskbar corner, 200% crop)
- [x] Ticker popup (privacy-blurred version wired into README as docs/screenshots/ticker.png; Unread-empty state as ticker-unread.png — 2026-10-23)
- [ ] Login window
- [x] Settings window (captured 2026-10-23; wired into README as docs/screenshots/settings.png)

## Uninstall
- [ ] Uninstall keeps `%APPDATA%\FaceTray` and the session folder when answered No; removes them when answered Yes; autostart entry removed either way.
