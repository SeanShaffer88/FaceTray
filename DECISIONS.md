# Decisions

A living log of significant architecture, UX, data and dependency decisions: date, decision, why, alternatives considered, consequences.

## 2026-10-23 — Read notifications through an embedded WebView2 session, not the Graph API

- **Decision:** FaceTray signs the user in inside its own WebView2 profile and reads `facebook.com/notifications` locally with a semantic-selector extraction script.
- **Why:** The Graph API personal notifications endpoint (`/me/notifications`, `manage_notifications`) was removed by Meta years ago; what remains covers Pages via webhooks and a public SSL server, not a personal notification feed. The Muse Facebook connector likewise has no notifications command.
- **Alternatives:** Graph API (dead end, verified); RSS (no personal notification feed exists); a browser extension companion (extra install surface, still DOM-bound).
- **Consequences:** No API keys, app review or server needed; two-factor sign-in works because it is a real browser view. The cost is fragility: a Facebook layout change can break list extraction, mitigated by semantic-only selectors plus the page-title count fallback. DOM extraction is labelled **Unverified** until it runs against SEAN's live Facebook layout.

## 2026-10-23 — C# / WPF + WebView2 instead of the Rust default

- **Decision:** Build in C# on .NET 10 LTS with Microsoft's first-party WebView2 SDK, following MuseDesktop's Core/Shell split.
- **Why:** MuseDesktop already solved the hard parts in this stack — persistent WebView2 profile, tray, single-instance, autostart, atomic settings — and they transfer almost 1:1. The WebView2 SDK is first-party for WPF.
- **Alternatives:** Rust + wry + tray-icon (the ytTrayDL stack): viable, but the login window and DOM interop would be rougher and v0.1 slower to reach.
- **Consequences:** A deliberate, recorded deviation from SEAN's Rust default, same as MuseDesktop's. Do not "fix" the stack back to Rust without revisiting this with SEAN.

## 2026-10-23 — The ticker looks like Facebook's own notifications menu

- **Decision:** SEAN directed, with a reference capture, that the ticker match Facebook's menu: `#242526` panel, “Notifications” header + `···`, All/Unread pills, Today/Earlier sections, 56px avatars with coloured type badges, relative times, blue unread dots, “See previous notifications” footer.
- **Why:** Familiarity wins inside this one surface: the user already knows this menu.
- **Consequences:** The black/red family look is overridden *inside the ticker only*. The tray counter bubble stays FaceTray red (`#E5484D`), and Settings/Login keep the family palette.

## 2026-10-23 — Local read marks instead of relying on Facebook's read state

- **Decision:** Clicking a notification marks it read in a local store (`read-state.json`, atomic, capped at 500 ids); the extraction parser applies those ids over the page's unread flags.
- **Why:** Facebook only updates its own read state when the user visits the site; a local mark makes the counter respond the moment a notification is opened from FaceTray.
- **Consequences:** Read state can differ between FaceTray and facebook.com until the next site visit — acceptable, and “Mark all read” is explicitly a local action. Multi-avatar rows show only the first avatar in v0.1 (see KNOWN_LIMITATIONS.md).
