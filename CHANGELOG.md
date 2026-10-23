# Changelog

All notable changes to FaceTray are recorded here. Versions follow semantic versioning.

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
