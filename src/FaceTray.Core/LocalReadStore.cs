using System.Text.Json;
using System.Text.Json.Serialization;

namespace FaceTray.Core;

/// <summary>
/// Locally-read notification ids, persisted as JSON. Facebook's own
/// read state only changes when the user visits the site; FaceTray marks a
/// notification read locally the moment it is clicked here, so the counter
/// responds instantly. Bounded: the oldest ids are dropped past the cap.
///
/// File format (v0.2.0): an array of { "id", "markedUtc" } entries.
/// v0.1.x files (a plain array of id strings) still load; those entries
/// carry no timestamp and are never pruned (see PruneOlderThan).
/// </summary>
public sealed class LocalReadStore
{
    public const int MaxIds = 500;

    private sealed class Entry
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("markedUtc")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTimeOffset? MarkedUtc { get; set; }
    }

    private readonly string _path;
    private readonly Dictionary<string, DateTimeOffset?> _marks = new();
    private readonly object _gate = new();

    public LocalReadStore(string path) => _path = path;

    public IReadOnlySet<string> Ids
    {
        get { lock (_gate) return new HashSet<string>(_marks.Keys); }
    }

    public int Count
    {
        get { lock (_gate) return _marks.Count; }
    }

    public bool IsRead(string id)
    {
        lock (_gate) return _marks.ContainsKey(id);
    }

    public void Load()
    {
        lock (_gate)
        {
            _marks.Clear();
            try
            {
                if (!File.Exists(_path))
                    return;
                using var doc = JsonDocument.Parse(File.ReadAllText(_path));
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                    return;
                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    if (element.ValueKind == JsonValueKind.String)
                    {
                        // v0.1.x format: bare ids, no timestamp.
                        var id = element.GetString();
                        if (!string.IsNullOrWhiteSpace(id))
                            _marks[id] = null;
                    }
                    else if (element.ValueKind == JsonValueKind.Object
                             && element.TryGetProperty("id", out var idProp)
                             && idProp.GetString() is { Length: > 0 } id)
                    {
                        DateTimeOffset? marked = null;
                        if (element.TryGetProperty("markedUtc", out var tsProp)
                            && tsProp.ValueKind == JsonValueKind.String
                            && DateTimeOffset.TryParse(tsProp.GetString(), out var parsed))
                            marked = parsed.ToUniversalTime();
                        _marks[id] = marked;
                    }
                }
            }
            catch
            {
                // A damaged read-state file just means everything looks unread once.
                _marks.Clear();
            }
        }
    }

    public void Save()
    {
        lock (_gate)
        {
            var entries = _marks
                .Select(kv => new Entry { Id = kv.Key, MarkedUtc = kv.Value })
                .ToList();
            var json = JsonSerializer.Serialize(entries, AtomicJson.IndentedOptions);
            AtomicJson.Write(_path, json);
        }
    }

    /// <summary>Returns true when the id was newly marked (state changed).</summary>
    public bool MarkRead(string id, DateTimeOffset? nowUtc = null)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;
        lock (_gate)
        {
            if (_marks.ContainsKey(id))
                return false;
            _marks[id] = (nowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
            TrimLocked();
            return true;
        }
    }

    public void MarkAllRead(IEnumerable<string> ids, DateTimeOffset? nowUtc = null)
    {
        var stamp = (nowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        lock (_gate)
        {
            foreach (var id in ids)
                if (!string.IsNullOrWhiteSpace(id) && !_marks.ContainsKey(id))
                    _marks[id] = stamp;
            TrimLocked();
        }
    }

    /// <summary>
    /// Drops entries marked before <paramref name="cutoffUtc"/> (the
    /// auto-prune setting). Legacy entries without a timestamp are kept.
    /// Returns how many were removed.
    /// </summary>
    public int PruneOlderThan(DateTimeOffset cutoffUtc)
    {
        lock (_gate)
        {
            var stale = _marks
                .Where(kv => kv.Value is { } marked && marked < cutoffUtc)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var id in stale)
                _marks.Remove(id);
            return stale.Count;
        }
    }

    public void Clear()
    {
        lock (_gate) _marks.Clear();
        try { if (File.Exists(_path)) File.Delete(_path); }
        catch { /* a later Save recreates it */ }
    }

    private void TrimLocked()
    {
        // Dictionary enumeration is insertion-ordered in practice; trimming
        // from the front drops the longest-held marks, which belong to
        // notifications long off the list.
        while (_marks.Count > MaxIds)
            _marks.Remove(_marks.Keys.First());
    }
}

/// <summary>One cached notification plus when FaceTray saved it (drives history pruning).</summary>
public sealed class CachedNotification
{
    public CachedNotification(NotificationItem item, DateTimeOffset savedUtc)
    {
        Item = item;
        SavedUtc = savedUtc;
    }

    public NotificationItem Item { get; }
    public DateTimeOffset SavedUtc { get; }
}

/// <summary>
/// Last-good notification list, persisted so the ticker never opens empty
/// after a network blip. File format v2 (v0.2.0): an object wrapping
/// per-item { item, savedUtc } entries, capped at
/// <see cref="MaxCachedItems"/>. v0.1.x files (a bare array of items)
/// still load — every entry inherits the file's last-write time as its
/// saved time — and are rewritten in v2 on the next save.
/// </summary>
public static class NotificationCacheStore
{
    public const int MaxCachedItems = 100;

    private sealed class CacheEntry
    {
        public NotificationItem Item { get; set; } = new();
        public DateTimeOffset SavedUtc { get; set; }
    }

    private sealed class CacheFile
    {
        public int Version { get; set; } = 2;
        public List<CacheEntry> Items { get; set; } = new();
    }

    public static void Save(string path, IReadOnlyList<NotificationItem> items) =>
        Save(path, items, DateTimeOffset.UtcNow);

    public static void Save(string path, IReadOnlyList<NotificationItem> items, DateTimeOffset savedUtc)
    {
        var file = new CacheFile
        {
            Version = 2,
            Items = items
                .Take(MaxCachedItems)
                .Select(item => new CacheEntry { Item = item, SavedUtc = savedUtc.ToUniversalTime() })
                .ToList(),
        };
        AtomicJson.Write(path, JsonSerializer.Serialize(file, AtomicJson.IndentedOptions));
    }

    /// <summary>
    /// Merges a fresh poll into the existing list: fresh items first (in
    /// page order), then older items the page no longer shows, deduped by
    /// id, capped. This is what gives the ticker its deeper history.
    /// </summary>
    public static IReadOnlyList<NotificationItem> Merge(
        IReadOnlyList<NotificationItem> existing,
        IReadOnlyList<NotificationItem> fresh,
        int cap = MaxCachedItems)
    {
        var merged = new List<NotificationItem>();
        var seen = new HashSet<string>();
        foreach (var item in fresh)
            if (seen.Add(item.Id))
                merged.Add(item);
        foreach (var item in existing)
            if (seen.Add(item.Id))
                merged.Add(item);
        if (merged.Count > cap)
            merged.RemoveRange(cap, merged.Count - cap);
        return merged;
    }

    /// <summary>Loads raw entries (either file format) with their saved times, unvalidated.</summary>
    public static IReadOnlyList<CachedNotification> LoadEntries(string path)
    {
        try
        {
            if (!File.Exists(path))
                return Array.Empty<CachedNotification>();
            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text))
                return Array.Empty<CachedNotification>();

            if (text.TrimStart().StartsWith("["))
            {
                // v0.1.x: a bare array of items; all entries share the
                // file's last-write time as their saved time.
                var items = JsonSerializer.Deserialize<List<NotificationItem>>(
                    text, AtomicJson.IndentedOptions);
                if (items is null)
                    return Array.Empty<CachedNotification>();
                var stamp = File.GetLastWriteTimeUtc(path);
                var savedUtc = stamp == DateTime.MinValue
                    ? DateTimeOffset.UtcNow
                    : new DateTimeOffset(stamp, TimeSpan.Zero);
                return items
                    .Where(i => i is not null)
                    .Select(i => new CachedNotification(i, savedUtc))
                    .ToList();
            }

            var file = JsonSerializer.Deserialize<CacheFile>(text, AtomicJson.IndentedOptions);
            if (file?.Items is null)
                return Array.Empty<CachedNotification>();
            return file.Items
                .Where(e => e?.Item is not null)
                .Select(e => new CachedNotification(e.Item, e.SavedUtc.ToUniversalTime()))
                .ToList();
        }
        catch
        {
            return Array.Empty<CachedNotification>();
        }
    }

    /// <summary>
    /// Loads the cache as display items: invalid rows dropped, v0.1.0
    /// duplicated-time text healed, local read + snooze state applied,
    /// entries older than <paramref name="keepDays"/> days pruned
    /// (0 = keep everything), capped at <see cref="MaxCachedItems"/>.
    /// </summary>
    public static IReadOnlyList<NotificationItem> Load(
        string path,
        IReadOnlySet<string>? locallyReadIds,
        IReadOnlySet<string>? snoozedIds = null,
        int keepDays = 0,
        DateTimeOffset? nowUtc = null)
    {
        var entries = LoadEntries(path);
        var cutoff = keepDays > 0
            ? (nowUtc ?? DateTimeOffset.UtcNow).AddDays(-keepDays)
            : DateTimeOffset.MinValue;
        var valid = new List<NotificationItem>();
        foreach (var entry in entries)
        {
            if (entry.SavedUtc < cutoff)
                continue;
            var item = entry.Item;
            if (string.IsNullOrWhiteSpace(item.Id)
                || !UrlGuard.IsAllowedNotificationUrl(item.Href))
                continue;
            // Heal v0.1.0 cache entries, whose Text still ends with the
            // relative-time token the ticker prints separately.
            item.Text = NotificationItem.StripTrailingTime(
                item.Text ?? "", item.RelativeTimeText);
            item.AvatarUrls ??= new List<string>();
            if (locallyReadIds is not null && locallyReadIds.Contains(item.Id))
                item.IsUnread = false;
            if (snoozedIds is not null && snoozedIds.Contains(item.Id))
                item.IsUnread = false;
            valid.Add(item);
            if (valid.Count >= MaxCachedItems)
                break;
        }
        return valid;
    }

    public static void Clear(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }
}
