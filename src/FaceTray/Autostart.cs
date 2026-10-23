using Microsoft.Win32;

namespace FaceTray;

/// <summary>
/// "Start with Windows" via the per-user Run key. No elevation, no service,
/// and the entry always points at the current exe with --background so a
/// sign-in launch lands quietly in the tray instead of stealing focus.
/// </summary>
public static class Autostart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FaceTray";

    public static void Sync(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null) return;

            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                    key.SetValue(ValueName, $"\"{exe}\" --background");
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // Registry access can be policy-blocked; autostart is optional.
        }
    }
}
