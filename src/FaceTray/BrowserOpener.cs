using System.Diagnostics;
using FaceTray.Core;

namespace FaceTray;

/// <summary>
/// Opens links in the user's default browser. Every URL is validated by
/// <see cref="UrlGuard"/> first — only https facebook.com links are ever
/// handed to the shell.
/// </summary>
public static class BrowserOpener
{
    public static void Open(string? url, DiagnosticLog log)
    {
        if (!UrlGuard.TryNormalizeNotificationUrl(url, out var normalized))
        {
            log.Warn($"Refused to open non-Facebook URL: {UrlGuard.RedactForLog(url)}");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(normalized) { UseShellExecute = true });
            log.Info($"Opened in browser: {UrlGuard.RedactForLog(normalized)}");
        }
        catch (Exception ex)
        {
            log.Warn($"Open in browser failed: {ex.Message}");
        }
    }
}
