# Known limitations

Kept current as work proceeds. Anything that affects users also belongs in the release notes.

- **Windows runtime Partially Verified (2026-10-23).** SEAN ran v0.1.0 on his Windows machine: the app launches, the signed-out ticker state renders as designed, the live notification list matches Facebook's own menu, and the Settings window renders with the expected states. Still **Unverified**: the tray counter end-to-end, click-a-notification-opens-the-browser, and the installer flow — his captures did not cover them (see RELEASE_CHECKLIST.md).
- **DOM extraction field-Verified for the standard notifications layout (2026-10-23).** The extraction script uses semantic signals only (notification links carrying `notif_id` / `notif_t`, aria-labels, roles, visible text) and never obfuscated class names; on SEAN's machine it listed his real notifications identical to Facebook's own menu (same items, order, grouping and type badges). Facebook can change its layout at any time; if row extraction ever misses, the counter still degrades to the page-title count.
- **Avatars: v0.1.1 fix Unverified.** In v0.1.0 every avatar rendered as the grey placeholder: Facebook often serves profile pictures as SVG `<image>` elements or `background-image` divs, which the v0.1.0 script ( `<img src>` only) did not capture. v0.1.1 captures all three forms and logs captured-but-failed avatar loads; whether real profile photos now render is **Unverified** until SEAN's next run.
- **Unread detection is heuristic.** The script looks for an explicit “Unread” accessible label in the row; rows Facebook does not label that way may appear read. The title count corrects the *number*, not individual rows.
- **Multi-avatar rows simplified.** Facebook stacks several avatars for group actions; v0.1 shows only the first avatar (the rest are parsed and cached, just not rendered).
- **Actor bolding is best-effort.** Names are bolded when a known action verb separates them from the rest of the text; otherwise the whole line renders regular rather than guessing.
- **Time labels come from Facebook.** Grouping (Today vs Earlier) and the time line use Facebook's relative labels (`9h`, `1d`); an unrecognized label lands in Earlier.
- **Clear data does not sign you out.** Settings → Clear data removes the cache and local read marks. The WebView2 profile folder cannot be deleted safely while the app runs, so full sign-out means deleting the session folder shown in Settings after closing FaceTray (or choosing “also delete” in the uninstaller).
- **High-contrast mode.** The ticker intentionally hard-codes Facebook's dark palette in v0.1; Windows high-contrast colours do not override it yet. Settings and Login use the family palette and standard controls.
- **Unsigned.** Plain EXE and installer ship unsigned per SEAN's standing rule; SmartScreen may warn on first run.
- **One account.** A single Facebook profile per Windows user in v0.1; multi-account support is a possible later slice.
