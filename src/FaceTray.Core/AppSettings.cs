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

    /// <summary>Clamp/repair values that cannot be trusted (hand-edited or corrupt files).</summary>
    public AppSettings Sanitized()
    {
        PollIntervalSeconds = Math.Clamp(PollIntervalSeconds, MinPollIntervalSeconds, MaxPollIntervalSeconds);
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
