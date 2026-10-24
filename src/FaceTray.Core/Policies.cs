namespace FaceTray.Core;

/// <summary>
/// Quiet hours: while in force, toasts and the sound cue are suppressed,
/// but polling and the counter keep working. The window may wrap midnight
/// (the usual case: 22:00 → 07:00).
/// </summary>
public static class QuietHours
{
    /// <summary>
    /// True when <paramref name="now"/>'s local time-of-day falls in
    /// [start, end). Equal start and end mean “no window” (never quiet).
    /// </summary>
    public static bool IsQuiet(DateTimeOffset now, TimeSpan start, TimeSpan end)
    {
        start = Normalize(start);
        end = Normalize(end);
        if (start == end)
            return false;
        var tod = now.TimeOfDay;
        return start < end
            ? tod >= start && tod < end
            : tod >= start || tod < end;
    }

    public static bool IsQuiet(DateTimeOffset now, AppSettings settings) =>
        settings.QuietHoursEnabled && IsQuiet(now, settings.QuietHoursStartTime, settings.QuietHoursEndTime);

    internal static TimeSpan Normalize(TimeSpan value)
    {
        var dayTicks = TimeSpan.TicksPerDay;
        var ticks = value.Ticks % dayTicks;
        if (ticks < 0)
            ticks += dayTicks;
        return TimeSpan.FromTicks(ticks);
    }
}

/// <summary>“HH:mm” time-of-day text (the settings-file format for quiet hours).</summary>
public static class TimeOfDayText
{
    /// <summary>
    /// Parses “H:mm” / “HH:mm” / “HH:mm:ss” text into a time-of-day,
    /// wrapping hours past 24 (“25:00” → 01:00). Parsed by hand because
    /// TimeSpan.TryParse reads “25:00:00” as 25 days. False on garbage.
    /// </summary>
    public static bool TryParse(string? text, out TimeSpan timeOfDay)
    {
        timeOfDay = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var parts = text.Trim().Split(':');
        if (parts.Length is < 2 or > 3)
            return false;
        if (!int.TryParse(parts[0], out var hours) || hours < 0)
            return false;
        if (!int.TryParse(parts[1], out var minutes) || minutes is < 0 or > 59)
            return false;
        var seconds = 0;
        if (parts.Length == 3 && (!int.TryParse(parts[2], out seconds) || seconds is < 0 or > 59))
            return false;
        timeOfDay = QuietHours.Normalize(new TimeSpan(hours, minutes, seconds));
        return true;
    }

    /// <summary>Canonical “HH:mm” for a time-of-day.</summary>
    public static string Format(TimeSpan timeOfDay)
    {
        var normalized = QuietHours.Normalize(timeOfDay);
        return $"{(int)normalized.TotalHours:D2}:{normalized.Minutes:D2}";
    }
}

/// <summary>
/// Adaptive polling (v0.2.0): news is checked sooner while unread items are
/// waiting, and the cadence relaxes after several polls in a row find
/// nothing new. Deliberately no battery / metered-connection awareness —
/// recorded in KNOWN_LIMITATIONS.md.
/// </summary>
public static class PollPolicy
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxInterval = TimeSpan.FromMinutes(5);

    /// <param name="baseInterval">The user's configured interval.</param>
    /// <param name="unreadCount">Unread after the last poll.</param>
    /// <param name="consecutiveEmptyPolls">Polls in a row that found no new items.</param>
    public static TimeSpan NextInterval(TimeSpan baseInterval, int unreadCount, int consecutiveEmptyPolls)
    {
        if (unreadCount > 0)
        {
            var half = TimeSpan.FromTicks(baseInterval.Ticks / 2);
            return half < MinInterval ? MinInterval : half;
        }
        if (consecutiveEmptyPolls >= 3)
        {
            var doubled = TimeSpan.FromTicks(baseInterval.Ticks * 2);
            return doubled > MaxInterval ? MaxInterval : doubled;
        }
        return baseInterval;
    }
}

/// <summary>
/// How healthy one extraction looks, judged by comparing what the script
/// found with the unread count Facebook bakes into the page title. The
/// title is the independent signal: if it claims unread notifications
/// exist but the script found no rows at all, Facebook's layout has
/// almost certainly changed under us.
/// </summary>
public enum ExtractionHealthState
{
    Healthy,
    Suspect,
    Broken,
}

public static class ExtractionHealth
{
    /// <param name="rowsFound">Rows the script extracted.</param>
    /// <param name="extractedUnread">Unread among the extracted rows (before local read/snooze state).</param>
    /// <param name="titleUnread">Unread count from the page title, or null when the title carried none.</param>
    public static ExtractionHealthState Evaluate(int rowsFound, int extractedUnread, int? titleUnread)
    {
        if (titleUnread is int title)
        {
            if (title > 0 && rowsFound <= 0)
                return ExtractionHealthState.Broken;
            if (Math.Abs(title - extractedUnread) >= 2)
                return ExtractionHealthState.Suspect;
        }
        return ExtractionHealthState.Healthy;
    }
}
