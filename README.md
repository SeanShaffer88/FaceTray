# FaceTray

## TL;DR

FaceTray puts your Facebook notifications in the Windows system tray. The tray icon shows a red counter bubble with how many unread notifications you have; click it and a ticker pops up that looks like Facebook's own notifications menu — latest notifications, grouped into Today and Earlier. Click any notification and it opens in your default browser. You sign in once, inside FaceTray, and it stays signed in.

## Features

- **Live counter on the tray icon** — re-rendered at runtime (1–9, then `9+`; no bubble at zero). The tooltip says “FaceTray — 3 unread”.
- **Facebook-style ticker** — 380px dark panel (`#242526`), “Notifications” header with a `···` menu, All / Unread filter pills, Today / Earlier sections, 56px circular avatars with a coloured type badge (green speech bubble for comments and mentions, blue thumbs-up for likes, pink cake for birthdays, grey bell otherwise), relative times, a blue dot on unread rows, and the “See previous notifications” footer button.
- **Click a notification → your default browser**, at that notification's exact Facebook link. It is marked read locally at once, so the counter drops immediately.
- **Right-click tray menu** — Open Facebook, Notifications page, Refresh Now, Mark all read, Pause/Resume polling, Settings, Exit. A second launch of FaceTray toggles the ticker instead of starting a copy.
- **Adjustable polling** — every 30 seconds, minute, 2 minutes or 5 minutes (Settings). Optional tray pop-up when new notifications arrive (off by default) and optional Start with Windows (off by default, starts quietly in the tray).
- **Forgiving states** — sign-in prompt when you're signed out, “You're all caught up”, the last-good list stays on screen when you're offline, and a plain-language error with Retry when a load fails.

## Install

Prerequisite: Microsoft's **WebView2 Runtime** (most Windows 10/11 PCs already have it — MuseDesktop uses the same one). FaceTray is a self-contained publish, so no .NET install is needed.

- **Portable ZIP:** unzip anywhere and run `FaceTray.exe`.
- **Installer:** `FaceTray-Setup-v0.1.0-x64.exe` (per-user, no admin; built with `installer/FaceTray.nsi` from the staged publish). Uninstall keeps your data unless you answer Yes to the “also delete” prompt.

Unsigned, like SEAN's other plain-EXE releases — Windows SmartScreen may warn on first run.

## How it works

There is no working official API for personal Facebook notifications (the Graph API notifications endpoint was removed by Meta years ago), so FaceTray does what your browser does:

1. On first run, a sign-in window opens on facebook.com inside FaceTray's own WebView2 profile (`%LOCALAPPDATA%\FaceTray\WebView2`). Sign in there once, including any two-factor step. FaceTray never sees or stores your password — the session lives in that profile, exactly like a browser's.
2. A hidden WebView2 page re-reads `facebook.com/notifications` at your chosen interval. A small script picks out the notification rows using semantic signals only — links, accessible labels, roles and visible text — never Facebook's internal styling class names, which change without notice.
3. The unread count is cross-checked against the count Facebook puts in the page title (`(3) Facebook`), so if the row reading ever misses, FaceTray degrades to showing the correct count instead of a wrong zero.

## Privacy

Local-only. No telemetry, no third-party servers, no token extraction — nothing leaves your PC except Facebook itself, over the same connection your browser would make. Settings, the last-seen notification list and your local read marks live in `%APPDATA%\FaceTray` as plain JSON; the diagnostic log (`%APPDATA%\FaceTray\logs\facetray.log`) records hosts and paths only, never full URLs, page content, cookies or tokens. Settings → Clear data wipes the local cache and read marks; the Settings window also shows the session folder path if you ever want to delete it (while FaceTray is closed) to sign out completely.

## Honest limitation

FaceTray reads Facebook's web UI, so a Facebook layout change can break the ticker's list until the extraction script is updated — the title-count fallback is the safety net for the counter. See [KNOWN_LIMITATIONS.md](KNOWN_LIMITATIONS.md) for the full, current list.

## Screenshots

Captured from real runs on SEAN's Windows machine — no placeholders.

The ticker before sign-in — the first thing a new user sees:

![Ticker asking for sign-in](docs/screenshots/ticker-signin.png)

Settings:

![FaceTray Settings](docs/screenshots/settings.png)

Still to capture:

1. Tray icon at 0 / 3 / 9+ unread (taskbar corner, 200% crop)
2. Ticker popup — mixed read/unread (a real capture exists from the v0.1.0 field run, but it shows friends' names, so it is not published as-is — a privacy-blurred version may follow)
3. Login window

## Building

```powershell
dotnet build FaceTray.sln -c Release
dotnet test tests/FaceTray.Core.Tests -c Release   # full suite, never a filtered subset
dotnet publish src/FaceTray/FaceTray.csproj -c Release -r win-x64 --self-contained true -o publish/win-x64
```

Layout: `src/FaceTray.Core` is pure, testable logic (.NET 10, no Windows APIs); `src/FaceTray` is the WPF shell. CI (`.github/workflows/ci.yml`) builds, tests, publishes and runs the app's `--smoke-test` mode on Windows.

## License

MIT — see [LICENSE](LICENSE). Copyright (c) 2026 Sean Shaffer.
