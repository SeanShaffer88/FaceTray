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

## 2026-10-24 — Multi-account profiles: per-profile session folders, restart to switch

- **Decision:** v0.2.0 supports up to 8 Facebook accounts. Each profile is a separate WebView2 session folder plus its own cache / read-state / snooze files. The DEFAULT profile keeps the historic v0.1.x names (`%LOCALAPPDATA%\FaceTray\WebView2`, `notifications-cache.json`, `read-state.json`) so an existing sign-in survives the upgrade untouched; other profiles use `WebView2-<id>` folders and `-<id>` file suffixes. Profile metadata lives in settings (`Profiles` + `ActiveProfileId`, repaired by `Profiles.Sanitize`). Switching profiles saves settings and relaunches the process via a delayed `cmd` start; removing a profile deletes its session folder and data files on Save (failures are queued in `PendingProfileDeletions` and retried each start).
- **Why:** The MuseDesktop pattern, proven there: a WebView2 environment is bound to its user-data folder for the life of the process, so two accounts can never be live in one process — restart-to-switch is the honest design, and per-profile everything keeps accounts' cookies and local state fully separate.
- **Alternatives:** One environment per account in one process (impossible — same folder binding, and parallel environments over different folders still multiply memory and polling); a single shared session with account switching inside Facebook's own UI (fragile, and the “account” would silently follow Facebook's switcher state).
- **Consequences:** Only the active account polls. A switch costs a restart (a second or two). Profile ids are constrained to filesystem-safe lowercase so they can be used directly in file names.

## 2026-10-24 — Custom toast window instead of a WinRT / toolkit notification dependency

- **Decision:** The new-notification pop-up is a hand-rolled borderless WPF window (Facebook-dark card, avatar + text + time, click-to-open, ~6 s auto-dismiss, hover pauses the timer). It replaces the WinForms balloon tip entirely.
- **Why:** Matching the ticker's look and carrying a clickable notification payload are both awkward with the stock balloon, and Windows toast notifications would need either the CommunityToolkit/WinRT notification packages (new dependencies, app-registration and COM activator baggage for an unpackaged app) or a shortcut-registration dance — all to show one small card. The custom window needs no new NuGet dependency (a project rule: no unnecessary dependencies) and behaves identically on Windows 10 and 11.
- **Alternatives:** WinForms `NotifyIcon` balloon (v0.1; plain text, no avatar, click target is the whole balloon with no payload routing); WinRT toast via toolkit (rejected above); Windows notifications via a registered AUMID shortcut (extra install surface, still no styling control).
- **Consequences:** FaceTray owns the pop-up's lifetime, placement (bottom-right of the work area) and focus behaviour (never takes activation). It is not a system notification: it does not appear in the Action Center and is suppressed by FaceTray's own quiet-hours setting rather than Windows Focus Assist.

## 2026-10-23 — Local read marks instead of relying on Facebook's read state

- **Decision:** Clicking a notification marks it read in a local store (`read-state.json`, atomic, capped at 500 ids); the extraction parser applies those ids over the page's unread flags.
- **Why:** Facebook only updates its own read state when the user visits the site; a local mark makes the counter respond the moment a notification is opened from FaceTray.
- **Consequences:** Read state can differ between FaceTray and facebook.com until the next site visit — acceptable, and “Mark all read” is explicitly a local action. Multi-avatar rows show only the first avatar in v0.1 (see KNOWN_LIMITATIONS.md).
