using System.Runtime.InteropServices;
using System.Text;

namespace FaceTray.Core;

/// <summary>
/// The inputs to the diagnostics report. By construction this state can
/// only carry versions, counts, timestamps and the user's own profile
/// names — there is no field a notification's text, an actor name, a full
/// URL or any page content could be put into, so the report can never
/// leak them.
/// </summary>
public sealed class DiagnosticsState
{
    public string AppVersion { get; init; } = "";
    public string OsDescription { get; init; } = RuntimeInformation.OSDescription;
    public string ActiveProfileId { get; init; } = Profiles.DefaultId;
    public string ActiveProfileName { get; init; } = "";
    public int ProfileCount { get; init; }
    public DateTimeOffset? LastPollUtc { get; init; }
    public int LastPollRows { get; init; }
    public int LastPollUnread { get; init; }
    public ExtractionHealthState ExtractionHealth { get; init; } = ExtractionHealthState.Healthy;

    /// <summary>
    /// Which extraction strategy the last poll used (v0.3.0): 1 primary,
    /// 2 permalink fallback, 3 row fallback; 0 before any poll.
    /// </summary>
    public int LastStrategy { get; init; }

    /// <summary>How often each strategy produced the list this session (counts only).</summary>
    public IReadOnlyDictionary<int, int> StrategyCounts { get; init; } = new Dictionary<int, int>();

    /// <summary>The session state the last poll found (name only).</summary>
    public string SessionStateName { get; init; } = "";
    public int PollIntervalSeconds { get; init; }
    public int CachedItemCount { get; init; }
    public int UnreadCount { get; init; }
    public string SessionFolder { get; init; } = "";
}

/// <summary> Builds the “Copy diagnostics” clipboard text (Settings → Diagnostics).</summary>
public static class DiagnosticsReport
{
    public static string Build(DiagnosticsState state)
    {
        var sb = new StringBuilder();
        sb.AppendLine("FaceTray diagnostics");
        sb.AppendLine($"Version: {state.AppVersion}");
        sb.AppendLine($"OS: {state.OsDescription}");
        sb.AppendLine($"Profile: {state.ActiveProfileName} ({state.ActiveProfileId})");
        sb.AppendLine($"Profiles: {state.ProfileCount}");
        sb.AppendLine(state.LastPollUtc is { } lastPoll
            ? $"Last poll: {lastPoll.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC — {state.LastPollRows} rows, {state.LastPollUnread} unread"
            : "Last poll: never");
        sb.AppendLine($"Extraction health: {state.ExtractionHealth}");
        if (state.LastStrategy > 0)
        {
            sb.AppendLine($"Extraction: {StrategyLabel(state.LastStrategy)} · last poll");
            sb.AppendLine(
                $"Strategies this session: primary {CountOf(state, 1)}, permalink fallback {CountOf(state, 2)}, row fallback {CountOf(state, 3)}");
        }
        if (!string.IsNullOrEmpty(state.SessionStateName))
            sb.AppendLine($"Session: {state.SessionStateName}");
        sb.AppendLine($"Unread counter: {state.UnreadCount}");
        sb.AppendLine($"Cached notifications: {state.CachedItemCount}");
        sb.AppendLine($"Poll interval (configured): {state.PollIntervalSeconds}s");
        sb.AppendLine($"Session folder: {state.SessionFolder}");
        return sb.ToString();
    }

    /// <summary>Human name for a strategy number, as shown in Settings → Diagnostics.</summary>
    public static string StrategyLabel(int strategy) => strategy switch
    {
        1 => "strategy 1 (primary)",
        2 => "strategy 2 (permalink fallback)",
        3 => "strategy 3 (row fallback)",
        _ => $"strategy {strategy}",
    };

    private static int CountOf(DiagnosticsState state, int strategy) =>
        state.StrategyCounts.TryGetValue(strategy, out var count) ? count : 0;
}
