using System.Text.RegularExpressions;

namespace FaceTray.Core;

/// <summary>A titled section of the ticker (“Today” / “Earlier”).</summary>
public sealed class NotificationGroup
{
    public NotificationGroup(string title, IReadOnlyList<NotificationItem> items)
    {
        Title = title;
        Items = items;
    }

    public string Title { get; }
    public IReadOnlyList<NotificationItem> Items { get; }
}

/// <summary>
/// Groups notifications the way Facebook's menu does: Today, then Earlier.
/// The bucket comes from the relative-time label: minutes/hours (and
/// “just now”) are Today; days/weeks and anything unparseable are Earlier.
/// Item order is preserved within each group.
/// </summary>
public static class NotificationGrouping
{
    public const string TodayTitle = "Today";
    public const string EarlierTitle = "Earlier";

    private static readonly Regex TimePattern = new(
        @"^(?<n>\d+)\s*(?<unit>m|min|mins|minute|minutes|h|hr|hrs|hour|hours|d|day|days|w|wk|week|weeks|mo|month|months|y|yr|year|years)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsToday(string? relativeTimeText)
    {
        if (string.IsNullOrWhiteSpace(relativeTimeText))
            return false;
        var t = relativeTimeText.Trim();
        if (t.Equals("just now", StringComparison.OrdinalIgnoreCase))
            return true;
        var match = TimePattern.Match(t);
        if (!match.Success)
            return false;
        var unit = match.Groups["unit"].Value.ToLowerInvariant();
        return unit.StartsWith("m") && !unit.StartsWith("mo") // minutes, not months
               || unit.StartsWith("h");
    }

    public static IReadOnlyList<NotificationGroup> GroupForDisplay(
        IReadOnlyList<NotificationItem> items, DateTimeOffset now)
    {
        // `now` is accepted for future absolute-time support; v0.1 buckets
        // purely from Facebook's relative labels, which need no clock math.
        _ = now;
        var today = new List<NotificationItem>();
        var earlier = new List<NotificationItem>();
        foreach (var item in items)
            (IsToday(item.RelativeTimeText) ? today : earlier).Add(item);

        var groups = new List<NotificationGroup>();
        if (today.Count > 0)
            groups.Add(new NotificationGroup(TodayTitle, today));
        if (earlier.Count > 0)
            groups.Add(new NotificationGroup(EarlierTitle, earlier));
        return groups;
    }
}
