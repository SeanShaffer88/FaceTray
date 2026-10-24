namespace FaceTray.Core;

/// <summary>
/// Where the app's files live. Everything is per-user:
/// settings, cache and logs under %APPDATA%\FaceTray, the WebView2 profile
/// (cookies / local storage — what keeps you signed in) under
/// %LOCALAPPDATA%\FaceTray\WebView2.
/// Tests and smoke runs can redirect everything via FACETRAY_HOME.
/// </summary>
public static class AppPaths
{
    /// <summary>
    /// The portable data root (v0.3.0), resolved from the marker file
    /// next to the executable. FACETRAY_HOME still wins over portable
    /// mode, and portable mode wins over the AppData defaults.
    /// </summary>
    private static string? PortableRoot =>
        DataRoot.TryResolvePortableRoot(AppContext.BaseDirectory, File.Exists);

    /// <summary>True when running in portable mode (see <see cref="DataRoot"/>).</summary>
    public static bool IsPortable => PortableRoot is not null;

    public static string RootDir
    {
        get
        {
            var overrideDir = Environment.GetEnvironmentVariable("FACETRAY_HOME");
            if (!string.IsNullOrWhiteSpace(overrideDir))
                return overrideDir;
            if (PortableRoot is { } portable)
                return portable;
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "FaceTray");
        }
    }

    public static string SettingsPath => Path.Combine(RootDir, "settings.json");

    /// <summary>Ids of notifications the user has opened / marked read locally.</summary>
    public static string ReadStatePath => Path.Combine(RootDir, "read-state.json");

    /// <summary>Last-good notification list, so the ticker can show something while offline.</summary>
    public static string CachePath => Path.Combine(RootDir, "notifications-cache.json");

    public static string LogDir => Path.Combine(RootDir, "logs");
    public static string LogPath => Path.Combine(LogDir, "facetray.log");

    /// <summary>WebView2 profile (cookies / local storage — what keeps you signed in).</summary>
    public static string WebViewDataDir
    {
        get
        {
            var overrideDir = Environment.GetEnvironmentVariable("FACETRAY_HOME");
            if (!string.IsNullOrWhiteSpace(overrideDir))
                return Path.Combine(overrideDir, "WebView2");
            if (PortableRoot is { } portable)
                return Path.Combine(portable, "WebView2");
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FaceTray", "WebView2");
        }
    }

    // ---- Per-profile paths (v0.2.0) ----
    // The default profile uses the historic v0.1.x names above, untouched, so
    // an existing sign-in survives. Other profiles get id-suffixed files and
    // a sibling session folder. The *In helpers are pure so tests can pin
    // the layout without touching the environment.

    public static string ReadStatePathFor(string? profileId) =>
        ReadStatePathIn(RootDir, profileId);

    public static string CachePathFor(string? profileId) =>
        CachePathIn(RootDir, profileId);

    /// <summary>Snoozed notification ids → when each snooze ends (default profile).</summary>
    public static string SnoozePath => Path.Combine(RootDir, "snooze-state.json");

    public static string SnoozePathFor(string? profileId) =>
        Profiles.IsDefault(profileId)
            ? SnoozePath
            : Path.Combine(RootDir, $"snooze-state-{profileId}.json");

    public static string WebViewDataDirFor(string? profileId)
    {
        if (Profiles.IsDefault(profileId))
            return WebViewDataDir;
        var overrideDir = Environment.GetEnvironmentVariable("FACETRAY_HOME");
        if (!string.IsNullOrWhiteSpace(overrideDir))
            return Path.Combine(overrideDir, $"WebView2-{profileId}");
        if (PortableRoot is { } portable)
            return Path.Combine(portable, $"WebView2-{profileId}");
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FaceTray", $"WebView2-{profileId}");
    }

    // ---- v0.3.0 per-profile paths: pins + re-alert tracking ----

    /// <summary>Pinned notification ids → when each was pinned (default profile).</summary>
    public static string PinPath => Path.Combine(RootDir, "pin-state.json");

    public static string PinPathFor(string? profileId) =>
        PinPathIn(RootDir, profileId);

    /// <summary>First-seen + re-alert tracking (default profile).</summary>
    public static string AlertTrackPath => Path.Combine(RootDir, "alert-track.json");

    public static string AlertTrackPathFor(string? profileId) =>
        AlertTrackPathIn(RootDir, profileId);

    internal static string ReadStatePathIn(string rootDir, string? profileId) =>
        Path.Combine(rootDir, Profiles.IsDefault(profileId)
            ? "read-state.json"
            : $"read-state-{profileId}.json");

    internal static string CachePathIn(string rootDir, string? profileId) =>
        Path.Combine(rootDir, Profiles.IsDefault(profileId)
            ? "notifications-cache.json"
            : $"notifications-cache-{profileId}.json");

    internal static string SnoozePathIn(string rootDir, string? profileId) =>
        Path.Combine(rootDir, Profiles.IsDefault(profileId)
            ? "snooze-state.json"
            : $"snooze-state-{profileId}.json");

    internal static string PinPathIn(string rootDir, string? profileId) =>
        Path.Combine(rootDir, Profiles.IsDefault(profileId)
            ? "pin-state.json"
            : $"pin-state-{profileId}.json");

    internal static string AlertTrackPathIn(string rootDir, string? profileId) =>
        Path.Combine(rootDir, Profiles.IsDefault(profileId)
            ? "alert-track.json"
            : $"alert-track-{profileId}.json");
}

/// <summary>
/// A tiny local-only diagnostic log: timestamped lines, no telemetry, and a
/// single rollover file so it can never grow without bound. Callers pass
/// already-safe text; URLs must go through <see cref="UrlGuard.RedactForLog"/>
/// (host + path only) and page content / cookies / tokens are never logged.
/// </summary>
public sealed class DiagnosticLog
{
    private const long MaxBytes = 1_000_000;
    private readonly string _path;
    private readonly object _gate = new();

    public DiagnosticLog(string path) => _path = path;

    public string Path => _path;

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
                RollIfNeeded();
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {Sanitize(message)}\n";
                File.AppendAllText(_path, line);
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }

    private void RollIfNeeded()
    {
        var info = new FileInfo(_path);
        if (info.Exists && info.Length > MaxBytes)
            File.Move(_path, _path + ".old", overwrite: true);
    }

    /// <summary>One event, one line, bounded length — log text cannot smuggle newlines.</summary>
    public static string Sanitize(string message)
    {
        var clean = message.Replace('\r', ' ').Replace('\n', ' ');
        return clean.Length <= 500 ? clean : clean[..500];
    }
}
