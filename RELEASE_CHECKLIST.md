# Release checklist — FaceTray v0.1.0 on-machine pass

Run on SEAN's Windows machine (Windows 10 or 11). Until this pass is done, all Windows runtime behaviour stays labelled **Unverified**.

## Install & first run
- [ ] Portable ZIP: unzip, run `FaceTray.exe` — no error, tray icon appears, ticker opens on first run showing the sign-in state.
- [ ] Installer: `FaceTray-Setup-v0.1.0-x64.exe` installs per-user without admin; Start Menu shortcut works.
- [ ] SmartScreen note: record exact wording shown for the unsigned build.

## Sign-in
- [ ] Click “Sign in to Facebook” — login window loads facebook.com; sign in including the two-factor step.
- [ ] Close the login window — the poller refreshes on its own and the ticker fills.
- [ ] Quit and relaunch FaceTray — still signed in (no login prompt).
- [ ] Sign out on facebook.com in a browser, wait one poll — FaceTray shows the signed-out state with the sign-in button.

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
- [ ] Settings window

## Uninstall
- [ ] Uninstall keeps `%APPDATA%\FaceTray` and the session folder when answered No; removes them when answered Yes; autostart entry removed either way.
