using System.Text.Json;
using System.Text.Json.Serialization;

namespace FaceTray.Core;

/// <summary>The four data files that make up one profile's local state.</summary>
public sealed record ProfileFilePaths(
    string ReadStatePath,
    string SnoozePath,
    string PinPath,
    string CachePath);

/// <summary>One read mark in a backup (id + when it was marked, when known).</summary>
public sealed class BackupReadMark
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("markedUtc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? MarkedUtc { get; set; }
}

/// <summary>One cached notification in a backup, with when FaceTray saved it.</summary>
public sealed class BackupCacheEntry
{
    [JsonPropertyName("item")] public NotificationItem Item { get; set; } = new();
    [JsonPropertyName("savedUtc")] public DateTimeOffset SavedUtc { get; set; }
}

/// <summary>One profile's slice of a backup.</summary>
public sealed class BackupProfile
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("readMarks")] public List<BackupReadMark> ReadMarks { get; set; } = new();
    [JsonPropertyName("snoozes")] public Dictionary<string, DateTimeOffset> Snoozes { get; set; } = new();
    [JsonPropertyName("pins")] public Dictionary<string, DateTimeOffset> Pins { get; set; } = new();
    [JsonPropertyName("cache")] public List<BackupCacheEntry> Cache { get; set; } = new();
}

/// <summary>The backup file envelope. Strictly validated on read.</summary>
public sealed class BackupEnvelope
{
    [JsonPropertyName("format")] public string Format { get; set; } = "";
    [JsonPropertyName("version")] public int Version { get; set; }
    [JsonPropertyName("exportedUtc")] public DateTimeOffset ExportedUtc { get; set; }
    [JsonPropertyName("settings")] public AppSettings? Settings { get; set; }
    [JsonPropertyName("profiles")] public List<BackupProfile>? Profiles { get; set; }
}

/// <summary>What a restore put back (counts only).</summary>
public sealed class BackupRestoreReport
{
    public int ProfilesRestored { get; init; }
    public int ReadMarks { get; init; }
    public int Snoozes { get; init; }
    public int Pins { get; init; }
    public int CachedItems { get; init; }
}

/// <summary>
/// Full backup &amp; restore (v0.3.0): ONE JSON file carrying the settings
/// plus, per account profile, the read marks, snoozes, pins and cached
/// notification history — everything FaceTray keeps locally EXCEPT the
/// WebView2 sign-in sessions, which are never read, never included and
/// never touched (a restored PC still needs its own sign-in per account,
/// exactly like a fresh install).
///
/// Restore validates the envelope strictly before any file is written;
/// anything malformed is rejected without throwing and without touching
/// existing data.
/// </summary>
public static class BackupService
{
    public const string FormatName = "facetray-backup";
    public const int FormatVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Builds the backup JSON from the live settings and each profile's store files.</summary>
    public static string Build(
        AppSettings settings, Func<string, ProfileFilePaths> pathsFor, DateTimeOffset nowUtc)
    {
        var envelope = new BackupEnvelope
        {
            Format = FormatName,
            Version = FormatVersion,
            ExportedUtc = nowUtc.ToUniversalTime(),
            Settings = settings.Clone().Sanitized(),
            Profiles = new List<BackupProfile>(),
        };
        foreach (var profile in settings.Profiles)
            envelope.Profiles.Add(CaptureProfile(profile.Id, pathsFor(profile.Id)));
        return JsonSerializer.Serialize(envelope, Options);
    }

    private static BackupProfile CaptureProfile(string profileId, ProfileFilePaths paths)
    {
        var readStore = new LocalReadStore(paths.ReadStatePath);
        readStore.Load();
        var snoozeStore = new SnoozeStore(paths.SnoozePath);
        snoozeStore.Load();
        var pinStore = new PinStore(paths.PinPath);
        pinStore.Load();

        return new BackupProfile
        {
            Id = profileId,
            ReadMarks = readStore.Snapshot()
                .Select(kv => new BackupReadMark { Id = kv.Key, MarkedUtc = kv.Value })
                .ToList(),
            Snoozes = new Dictionary<string, DateTimeOffset>(snoozeStore.Snapshot()),
            Pins = new Dictionary<string, DateTimeOffset>(pinStore.Snapshot()),
            Cache = NotificationCacheStore.LoadEntries(paths.CachePath)
                .Select(e => new BackupCacheEntry { Item = e.Item, SavedUtc = e.SavedUtc })
                .ToList(),
        };
    }

    /// <summary>
    /// Strictly validates backup JSON. False (never throws) on: invalid
    /// JSON, wrong format marker, unsupported version, missing settings
    /// or profile list, an invalid or duplicated profile id, or a profile
    /// slice with missing parts.
    /// </summary>
    public static bool TryParse(string? json, out BackupEnvelope? envelope)
    {
        envelope = null;
        if (string.IsNullOrWhiteSpace(json))
            return false;
        BackupEnvelope? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<BackupEnvelope>(json, Options);
        }
        catch (JsonException)
        {
            return false;
        }
        if (parsed is null)
            return false;
        if (parsed.Format != FormatName || parsed.Version != FormatVersion)
            return false;
        if (parsed.Settings is null || parsed.Profiles is null)
            return false;
        var seen = new HashSet<string>();
        foreach (var profile in parsed.Profiles)
        {
            if (profile is null || !Profiles.IsValidId(profile.Id) || !seen.Add(profile.Id))
                return false;
            if (profile.ReadMarks is null || profile.Snoozes is null
                || profile.Pins is null || profile.Cache is null)
                return false;
        }
        envelope = parsed;
        return true;
    }

    /// <summary>
    /// Writes every profile slice of a validated envelope back to its
    /// store files (atomically, per file). The envelope's settings are
    /// NOT applied here — the caller saves them (sanitized) itself, so it
    /// controls the restart that applying them needs.
    /// </summary>
    public static BackupRestoreReport Restore(
        BackupEnvelope envelope, Func<string, ProfileFilePaths> pathsFor)
    {
        var readMarks = 0;
        var snoozes = 0;
        var pins = 0;
        var cached = 0;
        var profiles = 0;
        foreach (var profile in envelope.Profiles ?? new List<BackupProfile>())
        {
            var paths = pathsFor(profile.Id);

            var readStore = new LocalReadStore(paths.ReadStatePath);
            var marks = new Dictionary<string, DateTimeOffset?>();
            foreach (var mark in profile.ReadMarks)
                if (!string.IsNullOrWhiteSpace(mark.Id))
                    marks[mark.Id] = mark.MarkedUtc?.ToUniversalTime();
            readStore.ReplaceAll(marks);
            readStore.Save();
            readMarks += marks.Count;

            var snoozeStore = new SnoozeStore(paths.SnoozePath);
            snoozeStore.ReplaceAll(profile.Snoozes);
            snoozeStore.Save();
            snoozes += profile.Snoozes.Count;

            var pinStore = new PinStore(paths.PinPath);
            pinStore.ReplaceAll(profile.Pins);
            pinStore.Save();
            pins += profile.Pins.Count;

            NotificationCacheStore.SaveEntries(
                paths.CachePath,
                profile.Cache
                    .Where(e => e?.Item is not null)
                    .Select(e => new CachedNotification(e.Item, e.SavedUtc))
                    .ToList());
            cached += profile.Cache.Count;
            profiles++;
        }
        return new BackupRestoreReport
        {
            ProfilesRestored = profiles,
            ReadMarks = readMarks,
            Snoozes = snoozes,
            Pins = pins,
            CachedItems = cached,
        };
    }
}
