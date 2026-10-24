using System.Runtime.InteropServices;
using FaceTray.Core;

namespace FaceTray;

/// <summary>
/// Reads Windows' shell-wide notification state (SHQueryUserNotificationState)
/// — the signal behind Focus Assist, presentation mode and fullscreen
/// games — and reports whether FaceTray may show a pop-up right now.
///
/// Honest scope (see KNOWN_LIMITATIONS.md): Windows does not expose
/// per-app or per-rule Focus Assist details to applications; this shell
/// state is all any app can read. Any failure to read it means ALLOW —
/// FaceTray never suppresses pop-ups on a guess.
/// </summary>
public static class FocusAssistGate
{
    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    /// <summary>
    /// True when pop-ups are welcome. <paramref name="stateName"/> is the
    /// state name for the log (names only, never user data).
    /// </summary>
    public static bool AllowsNotifications(out string stateName)
    {
        try
        {
            var hr = SHQueryUserNotificationState(out var raw);
            if (hr == 0) // S_OK
            {
                stateName = ShellNotificationGate.Name(raw);
                return ShellNotificationGate.AllowsNotifications(raw);
            }
            stateName = "Unavailable";
            return true;
        }
        catch
        {
            stateName = "Unavailable";
            return true;
        }
    }
}
