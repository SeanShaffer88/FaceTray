using System.Text.RegularExpressions;

namespace FaceTray.Core;

/// <summary>
/// Guards every URL the app acts on. Notification links open in the user's
/// default browser, so only https URLs on facebook.com (or a subdomain)
/// are ever followed — anything the page hands us that fails this check is
/// dropped and logged by host only.
/// </summary>
public static class UrlGuard
{
    public const string FacebookHomeUrl = "https://www.facebook.com/";
    public const string NotificationsUrl = "https://www.facebook.com/notifications";

    public static bool IsAllowedNotificationUrl(string? url)
        => TryNormalizeNotificationUrl(url, out _);

    /// <summary>
    /// Validates and normalizes a notification link: must be absolute https,
    /// host facebook.com or *.facebook.com. Fragments are stripped (they do
    /// not change the destination and would break id stability).
    /// </summary>
    public static bool TryNormalizeNotificationUrl(string? url, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(url))
            return false;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return false;
        if (!uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!IsFacebookHost(uri.Host))
            return false;

        var builder = new UriBuilder(uri) { Fragment = "" };
        normalized = builder.Uri.ToString();
        return true;
    }

    public static bool IsFacebookHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;
        var h = host.Trim().ToLowerInvariant();
        return h == "facebook.com" || h.EndsWith(".facebook.com", StringComparison.Ordinal);
    }

    /// <summary>Host + path only, for logs. Never log query strings (they carry ids/tokens).</summary>
    public static string RedactForLog(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return "(unparseable url)";
        return $"{uri.Host}{uri.AbsolutePath}";
    }

    private static readonly Regex LoginPath = new(@"/login|/checkpoint", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>True when a WebView2 URL indicates the session is signed out.</summary>
    public static bool LooksLikeLoginUrl(string? url) =>
        url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && IsFacebookHost(uri.Host) && LoginPath.IsMatch(uri.AbsolutePath);
}

/// <summary>
/// Extracts the unread count Facebook bakes into the page title:
/// “(3) Facebook”, “(9+) Facebook”, “(20+) Notifications”. No match → null
/// (callers treat null as “unknown”, never as zero).
/// </summary>
public static class TitleCount
{
    private static readonly Regex Pattern = new(@"^\((\d+)\+?\)", RegexOptions.Compiled);

    public static int? Parse(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;
        var match = Pattern.Match(title.Trim());
        if (!match.Success)
            return null;
        return int.TryParse(match.Groups[1].Value, out var count) ? count : null;
    }

    /// <summary>
    /// Reconciles the parsed unread count with the title count. The list can
    /// miss rows below the fold, so a larger title count wins; when no rows
    /// were extracted at all, the title count is the only signal and is
    /// used as-is (count-only degradation, never a wrong zero).
    /// </summary>
    public static int ResolveUnreadCount(int parsedUnread, int parsedItemCount, int? titleCount)
    {
        if (parsedItemCount <= 0)
            return titleCount ?? 0;
        if (titleCount is int t && t > parsedUnread)
            return t;
        return parsedUnread;
    }
}

/// <summary>The text shown in the tray icon's red counter bubble.</summary>
public static class BadgeText
{
    /// <summary>null at 0 (no bubble), “1”–“9”, then “9+”.</summary>
    public static string? For(int unreadCount)
    {
        if (unreadCount <= 0)
            return null;
        return unreadCount > 9 ? "9+" : unreadCount.ToString();
    }

    /// <summary>Tray tooltip text.</summary>
    public static string TooltipFor(int unreadCount) =>
        unreadCount <= 0 ? "FaceTray" : $"FaceTray — {unreadCount} unread";
}
