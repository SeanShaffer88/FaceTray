namespace FaceTray.Core;

/// <summary>
/// A user-facing filter family over <see cref="NotificationType"/>: the
/// Settings checkboxes (and <see cref="AppSettings.HiddenTypeGroups"/>)
/// speak in these groups.
/// </summary>
public enum NotificationTypeGroup
{
    CommentsAndMentions,
    LikesAndReactions,
    Birthdays,
    Other,
}

public static class NotificationTypeGroups
{
    public static NotificationTypeGroup Of(NotificationType type) => type switch
    {
        NotificationType.Comment or NotificationType.Mention => NotificationTypeGroup.CommentsAndMentions,
        NotificationType.Like => NotificationTypeGroup.LikesAndReactions,
        NotificationType.Birthday => NotificationTypeGroup.Birthdays,
        _ => NotificationTypeGroup.Other,
    };

    /// <summary>The settings-file key for a group (the enum name).</summary>
    public static string Key(NotificationTypeGroup group) => group.ToString();

    public static bool TryParseKey(string? key, out NotificationTypeGroup group)
    {
        group = NotificationTypeGroup.Other;
        return key is not null
            && Enum.TryParse(key, ignoreCase: true, out group)
            && Enum.IsDefined(group);
    }
}

/// <summary>
/// Which notifications are visible and which ones count — the pure logic
/// behind the ticker's search box, the type filters, the snooze display
/// state and the tray counter. Items passed here already carry their final
/// IsUnread flag (page state with the local read store and active snoozes
/// applied by the parser / poller).
/// </summary>
public static class NotificationVisibility
{
    public static bool IsHidden(NotificationItem item, IReadOnlySet<string>? hiddenGroupKeys)
    {
        if (hiddenGroupKeys is null || hiddenGroupKeys.Count == 0)
            return false;
        return hiddenGroupKeys.Contains(NotificationTypeGroups.Key(NotificationTypeGroups.Of(item.Type)));
    }

    /// <summary>Case-insensitive substring over actor + text. An empty query matches everything.</summary>
    public static bool MatchesSearch(NotificationItem item, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return true;
        var q = query.Trim();
        return item.Text.Contains(q, StringComparison.OrdinalIgnoreCase)
            || item.ActorText.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The ticker list: hidden types out, then search, then the Unread pill.</summary>
    public static IReadOnlyList<NotificationItem> FilterForDisplay(
        IReadOnlyList<NotificationItem> items,
        string? searchQuery,
        IReadOnlySet<string>? hiddenGroupKeys,
        bool unreadOnly)
    {
        var visible = new List<NotificationItem>();
        foreach (var item in items)
        {
            if (IsHidden(item, hiddenGroupKeys))
                continue;
            if (unreadOnly && !item.IsUnread)
                continue;
            if (!MatchesSearch(item, searchQuery))
                continue;
            visible.Add(item);
        }
        return visible;
    }

    /// <summary>
    /// The tray counter. Hidden types never count. The page-title count
    /// may lift the number when nothing is hidden (rows below the fold —
    /// the v0.1 behaviour); with any type hidden the title count no longer
    /// describes what the user wants counted, so only visible unread items
    /// count.
    /// </summary>
    public static int CountUnread(
        IReadOnlyList<NotificationItem> items,
        IReadOnlySet<string>? hiddenGroupKeys,
        int? titleCount)
    {
        var visibleUnread = items.Count(i => i.IsUnread && !IsHidden(i, hiddenGroupKeys));
        if (hiddenGroupKeys is not null && hiddenGroupKeys.Count > 0)
            return visibleUnread;
        return TitleCount.ResolveUnreadCount(visibleUnread, items.Count, titleCount);
    }
}

/// <summary>Short display text helpers for menus.</summary>
public static class DisplayText
{
    /// <summary>Truncates to at most <paramref name="maxChars"/> characters, ending with “…” when cut.</summary>
    public static string Truncate(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || maxChars <= 0)
            return "";
        var clean = text.Trim();
        if (clean.Length <= maxChars)
            return clean;
        if (maxChars <= 1)
            return "…";
        return clean[..(maxChars - 1)].TrimEnd() + "…";
    }
}
