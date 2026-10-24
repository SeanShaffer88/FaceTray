# Changelog

All notable changes to FaceTray are recorded here. Versions follow semantic versioning.

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
