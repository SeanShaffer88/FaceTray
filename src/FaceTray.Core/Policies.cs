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

/// <summary>
/// What state the Facebook session is in (v0.3.0), judged from where the
/// notifications page ended up. A checkpoint (“confirm it's you”) is NOT
/// a sign-out: the session exists but Facebook is holding it hostage
/// until the user confirms in the sign-in window, and the ticker says
/// exactly that instead of pretending the user is logged out.
/// </summary>
public enum SessionState
{
    Unknown,
    Active,
    SignedOut,
    Checkpoint,
}

public static class SessionClassifier
{
    /// <summary>
    /// Classifies from the page's path (never logged anywhere beyond the
    /// resulting enum) with the title as a fallback when the path says
    /// nothing.
    /// </summary>
    /// <param name="currentPath">URL path only, e.g. “/notifications” or “/checkpoint/…”.</param>
    /// <param name="titleText">The page title.</param>
    public static SessionState Classify(string? currentPath, string? titleText)
    {
        var path = (currentPath ?? "").Trim().ToLowerInvariant();
        if (path.Length > 0)
        {
            if (path.Contains("/checkpoint"))
                return SessionState.Checkpoint;
            if (path.Contains("/login") || path.Contains("/recover"))
                return SessionState.SignedOut;
            // Any other Facebook path means the session carried us past
            // the login gates.
            return SessionState.Active;
        }

        var title = (titleText ?? "").Trim().ToLowerInvariant();
        if (title.Contains("checkpoint") || title.Contains("confirm it's you") || title.Contains("confirm your identity"))
            return SessionState.Checkpoint;
        if (title.Contains("log in") || title.Contains("login"))
            return SessionState.SignedOut;
        return SessionState.Unknown;
    }
}

/// <summary>
/// Windows' shell-wide notification state, as returned by
/// SHQueryUserNotificationState (v0.3.0). This is the signal behind
/// Focus Assist, presentation mode and fullscreen games. Windows does
/// NOT expose per-app or per-rule Focus Assist details to apps — the
/// shell state is all any app can read, and FaceTray honours exactly
/// that (see KNOWN_LIMITATIONS.md).
/// </summary>
public enum ShellNotificationState
{
    /// <summary>No user session present (e.g. the lock / logon screen).</summary>
    NotPresent = 1,
    /// <summary>The user is busy (legacy state).</summary>
    Busy = 2,
    /// <summary>A fullscreen Direct3D app (a game) is running.</summary>
    RunningD3dFullScreen = 3,
    /// <summary>Presentation mode is on.</summary>
    PresentationMode = 4,
    /// <summary>Notifications are accepted normally.</summary>
    AcceptsNotifications = 5,
    /// <summary>Quiet time / Focus Assist is in force.</summary>
    QuietTime = 6,
    /// <summary>A fullscreen immersive app is in the foreground.</summary>
    App = 7,
}

public static class ShellNotificationGate
{
    /// <summary>
    /// True only when the shell state says notifications are welcome.
    /// An unrecognized future state allows them — suppressing pop-ups
    /// forever on a value we do not understand would be worse than one
    /// pop-up too many.
    /// </summary>
    public static bool AllowsNotifications(int rawState) =>
        !Enum.IsDefined(typeof(ShellNotificationState), rawState)
        || (ShellNotificationState)rawState == ShellNotificationState.AcceptsNotifications;

    /// <summary>The state name for logs (state names only — never anything user-specific).</summary>
    public static string Name(int rawState) =>
        Enum.IsDefined(typeof(ShellNotificationState), rawState)
            ? ((ShellNotificationState)rawState).ToString()
            : $"Unknown({rawState})";
}

/// <summary>
/// Whether a pop-up may fire for an item (v0.3.0): pop-ups enabled, the
/// item's type group is in the user's “alert me about” set, and it is
/// not quiet hours. The sound cue follows the same decision. The
/// shell-side Focus Assist state is checked separately by the caller
/// (it needs P/Invoke) and composes with this.
/// </summary>
public static class ToastDecision
{
    public static bool ShouldToast(AppSettings settings, NotificationItem item, DateTimeOffset now)
    {
        if (!settings.ShowToasts)
            return false;
        var groupKey = NotificationTypeGroups.Key(NotificationTypeGroups.Of(item.Type));
        if (!settings.AlertTypeGroups.Contains(groupKey))
            return false;
        if (QuietHours.IsQuiet(now, settings))
            return false;
        return true;
    }
}

/// <summary>
/// The morning digest (v0.3.0): once a day, at or after the configured
/// local time, when notifications have accumulated since the last
/// digest. The count is of items FaceTray has SEEN arrive since then —
/// see KNOWN_LIMITATIONS.md for the honest simplification.
/// </summary>
public static class DigestPolicy
{
    public static bool IsDue(
        bool enabled, string? digestTimeText, DateTimeOffset now,
        string? lastDigestDateText, int newSinceLastDigest)
    {
        if (!enabled || newSinceLastDigest <= 0)
            return false;
        var dueAt = TimeOfDayText.TryParse(digestTimeText, out var parsed)
            ? parsed
            : TimeSpan.FromHours(8);
        if (now.TimeOfDay < dueAt)
            return false;
        if (DateOnly.TryParseExact(
                (lastDigestDateText ?? "").Trim(), "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var last)
            && last >= DateOnly.FromDateTime(now.DateTime))
            return false; // already delivered today (or the clock says a later day)
        return true;
    }
}

/// <summary>
/// Renderer self-healing budget (v0.3.0): after a WebView2 renderer
/// process failure the poller recreates its hidden browser at most
/// <see cref="MaxRecreations"/> times per session, waiting
/// <see cref="Backoff"/> before each attempt, then stops and says so.
/// </summary>
public static class RendererRetryPolicy
{
    public const int MaxRecreations = 3;
    public static readonly TimeSpan Backoff = TimeSpan.FromSeconds(10);

    public static bool ShouldRecreate(int recreationsSoFar) =>
        recreationsSoFar is >= 0 and < MaxRecreations;
}

/// <summary>
/// The ticker footer's freshness line (v0.3.0). When the last poll
/// succeeded, news is “Updated …”; when it did not (offline, signed
/// out, checkpoint), the cached list must never masquerade as fresh —
/// it is labelled “Last updated … — showing saved notifications”.
/// </summary>
public static class StalenessText
{
    public static string Format(DateTimeOffset? lastSuccessUtc, DateTimeOffset nowUtc, bool isFresh)
    {
        if (lastSuccessUtc is null)
            return "Not updated yet";
        var age = nowUtc - lastSuccessUtc.Value;
        if (age < TimeSpan.Zero)
            age = TimeSpan.Zero;
        var ageText =
            age < TimeSpan.FromSeconds(60) ? "just now"
            : age < TimeSpan.FromHours(1) ? $"{Math.Max(1, (int)Math.Round(age.TotalMinutes))} min ago"
            : age < TimeSpan.FromDays(1) ? $"{(int)Math.Round(age.TotalHours)} h ago"
            : $"{(int)Math.Round(age.TotalDays)} d ago";
        if (isFresh)
            return ageText == "just now" ? "Updated just now" : $"Updated {ageText}";
        return ageText == "just now"
            ? "Last updated just now — showing saved notifications"
            : $"Last updated {ageText} — showing saved notifications";
    }
}

/// <summary>What a key press means for the focused ticker row (v0.3.0 power keys).</summary>
public enum RowKeyAction
{
    None,
    MoveDown,
    MoveUp,
    Open,
    MarkRead,
    SnoozeOneHour,
    TogglePin,
    ToggleExpand,
}

/// <summary>
/// Maps a key NAME (the WPF Key enum's name, so “J”, “Enter”, “Return”)
/// to a row action. Kept in Core so the mapping is unit-tested; the
/// shell resolves focus and applies the action. Active only while the
/// ticker's search box is not focused (letters must type there).
/// </summary>
public static class TickerKeyMap
{
    public static RowKeyAction Map(string? keyName) =>
        (keyName ?? "").Trim().ToUpperInvariant() switch
        {
            "J" => RowKeyAction.MoveDown,
            "K" => RowKeyAction.MoveUp,
            "ENTER" or "RETURN" => RowKeyAction.Open,
            "M" => RowKeyAction.MarkRead,
            "S" => RowKeyAction.SnoozeOneHour,
            "P" => RowKeyAction.TogglePin,
            "X" => RowKeyAction.ToggleExpand,
            _ => RowKeyAction.None,
        };
}
