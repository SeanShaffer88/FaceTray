using System.Text.RegularExpressions;

namespace FaceTray.Core;

/// <summary>One Facebook account FaceTray can be signed into.</summary>
public sealed class ProfileInfo
{
    /// <summary>Stable id, also used in file/folder names. See <see cref="Profiles.IsValidId"/>.</summary>
    public string Id { get; set; } = "";

    /// <summary>User-facing name (“Personal”, “Work”, …).</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// This account's tray counter bubble colour, as a palette hex
    /// (v0.3.0). Sanitized against <see cref="AccentPalette"/> — anything
    /// unrecognized falls back to FaceTray red.
    /// </summary>
    public string AccentColor { get; set; } = AccentPalette.DefaultHex;
}

/// <summary>
/// The fixed counter-bubble colour palette (v0.3.0). A deliberately
/// closed set: the tray icon is rendered with GDI+, and a free-form hex
/// from a hand-edited settings file should never reach the renderer
/// unvalidated. The default profile's default is FaceTray red.
/// </summary>
public static class AccentPalette
{
    public const string DefaultHex = "#E5484D";

    public static readonly IReadOnlyList<(string Name, string Hex)> Colors = new[]
    {
        ("Red", "#E5484D"),
        ("Orange", "#E07B39"),
        ("Green", "#46A758"),
        ("Blue", "#3E8FE1"),
        ("Purple", "#8E4EC6"),
        ("Pink", "#E93D82"),
    };

    /// <summary>Canonical palette hex for an input (case-insensitive), or false when it is not in the palette.</summary>
    public static bool TryNormalize(string? hex, out string canonical)
    {
        canonical = DefaultHex;
        if (string.IsNullOrWhiteSpace(hex))
            return false;
        var candidate = hex.Trim();
        foreach (var (_, paletteHex) in Colors)
        {
            if (candidate.Equals(paletteHex, StringComparison.OrdinalIgnoreCase))
            {
                canonical = paletteHex;
                return true;
            }
        }
        return false;
    }

    public static string NormalizeOrDefault(string? hex) =>
        TryNormalize(hex, out var canonical) ? canonical : DefaultHex;
}

/// <summary>
/// The multi-account model (v0.2.0, MuseDesktop pattern). Each profile is a
/// separate WebView2 session folder and its own cache / read-state / snooze
/// files, so accounts never share cookies or local state. The DEFAULT
/// profile deliberately keeps the historic v0.1.x folder and file names, so
/// an existing sign-in survives the upgrade untouched; other profiles use
/// id-suffixed names (see <see cref="AppPaths"/>).
/// </summary>
public static class Profiles
{
    public const string DefaultId = "default";
    public const string DefaultName = "Default";
    public const int MaxProfiles = 8;

    private static readonly Regex IdPattern = new(
        @"^[a-z0-9][a-z0-9-]{0,23}$", RegexOptions.Compiled);

    public static bool IsDefault(string? id) =>
        string.IsNullOrWhiteSpace(id) || id == DefaultId;

    /// <summary>Ids become file-name suffixes, so they stay lowercase, short and filesystem-safe.</summary>
    public static bool IsValidId(string? id) =>
        id is not null && IdPattern.IsMatch(id);

    public static string NewId() =>
        "p" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// Repairs a profile list from settings: guarantees the default profile
    /// exists (first), every id is valid and unique (invalid or duplicate
    /// ids are regenerated), names are non-empty, the list is capped at
    /// <see cref="MaxProfiles"/>, and the active id names a profile in the
    /// list (falling back to the default profile).
    /// </summary>
    public static List<ProfileInfo> Sanitize(
        IEnumerable<ProfileInfo>? profiles, string? activeId, out string sanitizedActiveId)
    {
        var result = new List<ProfileInfo>();
        var seen = new HashSet<string>();
        foreach (var profile in profiles ?? Enumerable.Empty<ProfileInfo>())
        {
            if (profile is null)
                continue;
            var id = (profile.Id ?? "").Trim();
            if (!IsValidId(id) || !seen.Add(id))
            {
                id = NewId();
                seen.Add(id);
            }
            var name = (profile.Name ?? "").Trim();
            if (name.Length == 0)
                name = id == DefaultId ? DefaultName : "Account";
            if (name.Length > 60)
                name = name[..60];
            result.Add(new ProfileInfo
            {
                Id = id,
                Name = name,
                AccentColor = AccentPalette.NormalizeOrDefault(profile.AccentColor),
            });
            if (result.Count >= MaxProfiles)
                break;
        }

        var defaultIndex = result.FindIndex(p => p.Id == DefaultId);
        if (defaultIndex < 0)
        {
            result.Insert(0, new ProfileInfo { Id = DefaultId, Name = DefaultName });
            if (result.Count > MaxProfiles)
                result.RemoveAt(result.Count - 1);
        }
        else if (defaultIndex > 0)
        {
            var def = result[defaultIndex];
            result.RemoveAt(defaultIndex);
            result.Insert(0, def);
        }

        sanitizedActiveId = activeId is not null && result.Any(p => p.Id == activeId)
            ? activeId
            : DefaultId;
        return result;
    }
}
