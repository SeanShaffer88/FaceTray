# Known limitations

Kept current as work proceeds. Anything that affects users also belongs in the release notes.

- **Windows runtime Unverified (v0.1.0).** FaceTray was built and unit-tested on a Linux VM, which cannot run the WPF app. Tray behaviour, WebView2 polling, sign-in and the ticker's look are Unverified until SEAN's on-machine pass in RELEASE_CHECKLIST.md.
- **DOM extraction Unverified against the real Facebook layout.** The extraction script uses semantic signals only (notification links carrying `notif_id` / `notif_t`, aria-labels, roles, visible text) and never obfuscated class names, but Facebook's live DOM has not been exercised yet. If row extraction misses, the counter still degrades to the page-title count; the list may be empty until selectors are tuned.
- **Unread detection is heuristic.** The script looks for an explicit “Unread” accessible label in the row; rows Facebook does not label that way may appear read. The title count corrects the *number*, not individual rows.
- **Multi-avatar rows simplified.** Facebook stacks several avatars for group actions; v0.1 shows only the first avatar (the rest are parsed and cached, just not rendered).
- **Actor bolding is best-effort.** Names are bolded when a known action verb separates them from the rest of the text; otherwise the whole line renders regular rather than guessing.
- **Time labels come from Facebook.** Grouping (Today vs Earlier) and the time line use Facebook's relative labels (`9h`, `1d`); an unrecognized label lands in Earlier.
- **Clear data does not sign you out.** Settings → Clear data removes the cache and local read marks. The WebView2 profile folder cannot be deleted safely while the app runs, so full sign-out means deleting the session folder shown in Settings after closing FaceTray (or choosing “also delete” in the uninstaller).
- **High-contrast mode.** The ticker intentionally hard-codes Facebook's dark palette in v0.1; Windows high-contrast colours do not override it yet. Settings and Login use the family palette and standard controls.
- **Unsigned.** Plain EXE and installer ship unsigned per SEAN's standing rule; SmartScreen may warn on first run.
- **One account.** A single Facebook profile per Windows user in v0.1; multi-account support is a possible later slice.
