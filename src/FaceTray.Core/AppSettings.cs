using System.Text.Json;

namespace FaceTray.Core;

/// <summary>
/// Everything the app persists about itself. Versioned JSON; unknown fields
/// are ignored and missing fields fall back to defaults, so a settings file
/// written by an older or newer build still loads.
/// </summary>
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;

    public const int MinPollIntervalSeconds = 30;
    public const int MaxPollIntervalSeconds = 300;

    /// <summary>How often the hidden notifications page is re-read. 60s default, clamped 30–300.</summary>
    public int PollIntervalSeconds { get; set; } = 60;

    /// <summary>Start with Windows (HKCU Run). Off by default; user opt-in only.</summary>
    public bool StartWithWindows { get; set; }

    /// <summary>Tray balloon when new unread notifications arrive. Off by default — no nagging.</summary>
    public bool ShowToasts { get; set; }

    /// <summary>True once the first-run sign-in prompt has been shown.</summary>
    public bool HasSeenFirstRun { get; set; }

    // ---- v0.2.0 additions (unknown to older builds, which ignore them) ----

    /// <summary>
    /// Type groups hidden from the ticker AND the unread counter, stored as
    /// <see cref="NotificationTypeGroups.Key"/> names. Empty = everything shown.
    /// </summary>
    public List<string> HiddenTypeGroups { get; set; } = new();

    /// <summary>Suppress toasts and the sound cue during quiet hours. Off by default.</summary>
    public bool QuietHoursEnabled { get; set; }

    /// <summary>
    /// Local time quiet hours begin, as an “HH:mm” string. (Stored as text,
    /// not a TimeSpan: System.Text.Json's TimeSpan converter rejects
    /// hand-edited values like “25:00”, which must sanitize, not fail.)
    /// </summary>
    public string QuietHoursStart { get; set; } = "22:00";

    /// <summary>Local time quiet hours end, as an “HH:mm” string.</summary>
    public string QuietHoursEnd { get; set; } = "07:00";

    /// <summary>Parsed <see cref="QuietHoursStart"/> (22:00 when unparseable).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public TimeSpan QuietHoursStartTime =>
        TimeOfDayText.TryParse(QuietHoursStart, out var start) ? start : TimeSpan.FromHours(22);

    /// <summary>Parsed <see cref="QuietHoursEnd"/> (07:00 when unparseable).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public TimeSpan QuietHoursEndTime =>
        TimeOfDayText.TryParse(QuietHoursEnd, out var end) ? end : TimeSpan.FromHours(7);

    /// <summary>Play the system asterisk sound with a new-notification pop-up. Off by default.</summary>
    public bool PlaySoundOnToast { get; set; }

    /// <summary>Ctrl+Shift+F toggles the ticker from anywhere. On by default.</summary>
    public bool GlobalHotkeyEnabled { get; set; } = true;

    /// <summary>Once-per-launch check of GitHub for a newer FaceTray release. On by default.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Days of notification history kept locally; one of 7 / 30 / 90.</summary>
    public int HistoryKeepDays { get; set; } = 30;

    /// <summary>Ticker palette: “FacebookDark” (default), “Light” or “FollowWindows”.</summary>
    public string TickerTheme { get; set; } = TickerThemes.FacebookDark;

    /// <summary>Ticker text size: 100, 125 or 150 (percent).</summary>
    public int TickerTextScalePercent { get; set; } = 100;

    /// <summary>Facebook accounts (see <see cref="Profiles"/>). The default profile always exists.</summary>
    public List<ProfileInfo> Profiles { get; set; } = new();

    /// <summary>The active profile's id.</summary>
    public string ActiveProfileId { get; set; } = Core.Profiles.DefaultId;

    /// <summary>
    /// Profiles whose data deletion failed or was deferred; the app retries
    /// deleting their session folders and data files on each start.
    /// </summary>
    public List<string> PendingProfileDeletions { get; set; } = new();

    public ProfileInfo ActiveProfile =>
        Profiles.FirstOrDefault(p => p.Id == ActiveProfileId)
        ?? new ProfileInfo { Id = Core.Profiles.DefaultId, Name = Core.Profiles.DefaultName };

    /// <summary>Clamp/repair values that cannot be trusted (hand-edited or corrupt files).</summary>
    public AppSettings Sanitized()
    {
        PollIntervalSeconds = Math.Clamp(PollIntervalSeconds, MinPollIntervalSeconds, MaxPollIntervalSeconds);

        var hidden = new List<string>();
        foreach (var key in HiddenTypeGroups ?? new List<string>())
        {
            if (NotificationTypeGroups.TryParseKey(key, out var group))
            {
                var canonical = NotificationTypeGroups.Key(group);
                if (!hidden.Contains(canonical))
                    hidden.Add(canonical);
            }
        }
        HiddenTypeGroups = hidden;

        QuietHoursStart = TimeOfDayText.Format(QuietHoursStartTime);
        QuietHoursEnd = TimeOfDayText.Format(QuietHoursEndTime);

        HistoryKeepDays = HistoryKeepDays switch { 7 => 7, 90 => 90, _ => 30 };
        TickerTheme = TickerThemes.IsValid(TickerTheme) ? TickerTheme : TickerThemes.FacebookDark;
        TickerTextScalePercent = TickerTextScalePercent switch { 125 => 125, 150 => 150, _ => 100 };

        Profiles = Core.Profiles.Sanitize(Profiles, ActiveProfileId, out var activeId);
        ActiveProfileId = activeId;

        PendingProfileDeletions = (PendingProfileDeletions ?? new List<string>())
            .Where(id => Core.Profiles.IsValidId(id) && id != ActiveProfileId)
            .Distinct()
            .ToList();
        return this;
    }

    /// <summary>Deep copy (JSON round-trip) — the Settings window edits a clone so Cancel can discard cleanly.</summary>
    public AppSettings Clone() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this)) ?? new AppSettings();
}

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON.
/// Saves are atomic (write to a temp file, then replace) so a crash or
/// power loss mid-save cannot leave a torn file. A file that fails to
/// parse is preserved next to the original as <c>.corrupt-*</c> and
/// defaults are used — the user's data is never silently discarded.
/// </summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static AppSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new AppSettings();
            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, Options);
            return (settings ?? new AppSettings()).Sanitized();
        }
        catch (Exception)
        {
            try
            {
                var backup = $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
                File.Copy(path, backup, overwrite: false);
            }
            catch
            {
                // Best effort only — returning defaults is the contract.
            }
            return new AppSettings();
        }
    }

    public static void Save(string path, AppSettings settings)
    {
        AtomicJson.Write(path, JsonSerializer.Serialize(settings.Sanitized(), Options));
    }

    /// <summary>
    /// Settings backup: the sanitized settings as JSON. Contains no session
    /// data (cookies live in the WebView2 profile folder, never in settings),
    /// so the file is safe to move between PCs.
    /// </summary>
    public static string Export(AppSettings settings)
    {
        var clean = settings.Clone().Sanitized();
        return JsonSerializer.Serialize(clean, Options);
    }

    /// <summary>
    /// Adopts an exported settings JSON. Returns false (and defaults) on
    /// anything unparseable — never throws.
    /// </summary>
    public static bool TryImport(string? json, out AppSettings settings)
    {
        settings = new AppSettings();
        if (string.IsNullOrWhiteSpace(json))
            return false;
        try
        {
            var parsed = JsonSerializer.Deserialize<AppSettings>(json, Options);
            if (parsed is null)
                return false;
            settings = parsed.Sanitized();
            return true;
        }
        catch (Exception)
        {
            settings = new AppSettings();
            return false;
        }
    }
}

/// <summary>The ticker palette choices (Settings → “Ticker appearance”).</summary>
public static class TickerThemes
{
    public const string FacebookDark = "FacebookDark";
    public const string Light = "Light";
    public const string FollowWindows = "FollowWindows";

    public static bool IsValid(string? theme) =>
        theme is FacebookDark or Light or FollowWindows;
}

/// <summary>Shared atomic JSON file write (temp file + replace) for Core stores.</summary>
internal static class AtomicJson
{
    public static void Write(string path, string json)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    public static JsonSerializerOptions IndentedOptions => new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
}
