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

## 2026-10-24 — Three semantic extraction strategies, tried in order

- **Decision:** v0.3.0's extraction script discovers rows three ways, in order: S1 anchors carrying `notif_id`/`notif_t` (the field-verified v0.1 approach); S2 anchors to post/story-style targets whose row carries a visible time leaf; S3 `listitem`/`article` rows containing an anchor and a time leaf, first anchor wins. The first strategy that yields rows wins; the payload reports which (`strategy`), the parser records it on the poll result, and Settings → Diagnostics shows the last strategy plus this session's per-strategy counts. All three use ONLY semantic signals — the no-class-names rule is unchanged and still guard-tested.
- **Why:** The v0.1.0 field run proved S1 against the real page, but a single markup change could still empty the list (the title-count fallback protects the number, not the rows). Layered fallbacks mean a Facebook change has to break three different semantic structures at once — and the strategy report shows *which* layer is carrying the list, an early warning before anything fully breaks.
- **Alternatives:** One bigger, cleverer selector set (a single point of failure, and cleverness is exactly how class-name selectors sneak back in); waiting for breakage and patching reactively (the v0.2.0 health banner made breakage visible, but the list still goes empty in the meantime).
- **Consequences:** S2/S3 are less precise than S1 (they key off permalinks and row structure), so a fallback list could in principle include a row S1 would not have picked; the parser's validation (Facebook https links only, dedupe, text shape, 30-row cap) still applies to every strategy. Strategy use is counts-only in diagnostics — no content, no URLs.

## 2026-10-24 — Portable mode via a user-created portable.txt marker

- **Decision:** If a file named `portable.txt` exists in the app base directory, ALL of FaceTray's data roots resolve under `<base>\FaceTray-data` — settings, caches AND the WebView2 session folders (`AppPaths` centralizes both roots, and both route through the resolution). Without the marker, every path is byte-identical to v0.2.0. The marker is never shipped in the repo, the installer or the ZIP; the user creates it (README documents how). The rule is a pure Core function (`DataRoot`) of the base directory plus an injected file-exists probe, so it is unit-tested. `FACETRAY_HOME` still wins over the marker.
- **Why:** A tray utility is exactly what people carry on a USB stick; portable mode makes the whole app — sign-ins included — one movable folder, with no UI and no migration code. A settings flag would be chicken-and-egg (the settings file is one of the things being relocated); an environment variable is undiscoverable for ordinary users.
- **Alternatives:** A `--portable` command-line flag (a forgotten flag silently splits data across two homes); always-portable (breaks the per-user install model and every existing AppData sign-in).
- **Consequences:** Two data worlds can exist (AppData and portable) and never merge; KNOWN_LIMITATIONS.md says so plainly.

## 2026-10-24 — Addendum: pop-ups now also honour Windows' notification state

- **Decision:** Amends the custom-toast decision above: pop-ups and the sound cue are additionally gated on `SHQueryUserNotificationState` — when Windows says notifications are not accepted (Focus Assist, presentation mode, fullscreen Direct3D), FaceTray holds pop-ups and sound while counting continues. The gate can only suppress on evidence: any failure to read the state allows pop-ups.
- **Why:** v0.2.0 suppressed pop-ups only via FaceTray's own quiet hours; SEAN's round-2 list asked for Focus Assist respect, and the shell-wide state is the only Focus Assist signal Windows exposes to apps (per-rule details are not available to any app — KNOWN_LIMITATIONS.md states this honestly).
- **Consequences:** A state change is logged once per change (state name only). The toast decision matrix itself (pop-ups on, per-type alert rules, quiet hours) lives in Core (`ToastDecision`) and is unit-tested; the shell gate composes on top of it.
