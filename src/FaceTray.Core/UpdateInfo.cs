using System.Text.Json;
using System.Text.Json.Serialization;

namespace FaceTray.Core;

/// <summary>
/// A newer FaceTray release, from GitHub's “latest release” JSON. The
/// update check is the app's only network call besides facebook.com
/// itself, and it can be turned off in Settings.
/// </summary>
public sealed class UpdateInfo
{
    /// <summary>The release tag as published, e.g. “v0.2.0”.</summary>
    public string Tag { get; init; } = "";

    /// <summary>The tag without a leading “v”, e.g. “0.2.0”.</summary>
    public string Version { get; init; } = "";

    /// <summary>The release page URL (github.com only — the shell re-checks before opening).</summary>
    public string Url { get; init; } = "";

    private sealed class ReleaseJson
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
    }

    /// <summary>Parses GitHub's /releases/latest response body. False on anything unexpected.</summary>
    public static bool TryParse(string? json, out UpdateInfo? info)
    {
        info = null;
        if (string.IsNullOrWhiteSpace(json))
            return false;
        try
        {
            var release = JsonSerializer.Deserialize<ReleaseJson>(json);
            if (release is null || release.Draft || string.IsNullOrWhiteSpace(release.TagName))
                return false;
            var tag = release.TagName.Trim();
            info = new UpdateInfo
            {
                Tag = tag,
                Version = StripLeadingV(tag),
                Url = release.HtmlUrl ?? "",
            };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public bool IsNewerThan(string currentVersion) => IsNewer(Version, currentVersion);

    /// <summary>
    /// Semver-ish comparison on the dotted numeric core only. A leading
    /// “v” is ignored. Anything after a “-” (a prerelease-style suffix)
    /// does not take part: a suffixed tag counts as newer ONLY when its
    /// core version is strictly greater than the current core version —
    /// “v0.2.0-beta” is not newer than a running 0.2.0.
    /// </summary>
    public static bool IsNewer(string? latestVersionOrTag, string? currentVersion)
    {
        if (!TryParseCore(latestVersionOrTag, out var latest))
            return false;
        if (!TryParseCore(currentVersion, out var current))
            return false;
        for (var i = 0; i < Math.Max(latest.Length, current.Length); i++)
        {
            var l = i < latest.Length ? latest[i] : 0;
            var c = i < current.Length ? current[i] : 0;
            if (l != c)
                return l > c;
        }
        return false;
    }

    internal static string StripLeadingV(string tag) =>
        tag.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? tag[1..] : tag;

    private static bool TryParseCore(string? versionOrTag, out int[] parts)
    {
        parts = Array.Empty<int>();
        if (string.IsNullOrWhiteSpace(versionOrTag))
            return false;
        var core = StripLeadingV(versionOrTag.Trim());
        var dash = core.IndexOf('-');
        if (dash >= 0)
            core = core[..dash];
        if (core.Length == 0)
            return false;
        var segments = core.Split('.');
        var parsed = new int[segments.Length];
        for (var i = 0; i < segments.Length; i++)
        {
            if (!int.TryParse(segments[i], out parsed[i]) || parsed[i] < 0)
                return false;
        }
        parts = parsed;
        return true;
    }
}
