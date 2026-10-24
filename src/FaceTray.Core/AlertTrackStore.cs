using System.Text.Json;
using System.Text.Json.Serialization;

namespace FaceTray.Core;

/// <summary>
/// First-seen tracking for the one gentle re-alert (v0.3.0): notification
/// id → when FaceTray first saw it, and whether its single re-alert has
/// already been spent. Entries are pruned alongside the notification
/// history (by age, and whenever an id leaves the history). Persisted as
/// one atomic JSON object. Counts and timestamps only — no content.
/// </summary>
public sealed class AlertTrackStore
{
    private sealed class Entry
    {
        [JsonPropertyName("firstSeenUtc")] public DateTimeOffset FirstSeenUtc { get; set; }
        [JsonPropertyName("realerted")] public bool Realerted { get; set; }
    }

    private readonly string _path;
    private readonly Dictionary<string, Entry> _entries = new();
    private readonly object _gate = new();

    public AlertTrackStore(string path) => _path = path;

    public string Path => _path;

    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    public void Load()
    {
        lock (_gate)
        {
            _entries.Clear();
            try
            {
                if (!File.Exists(_path))
                    return;
                var map = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_path));
                if (map is not null)
                    foreach (var (id, entry) in map)
                        if (!string.IsNullOrWhiteSpace(id) && entry is not null)
                            _entries[id] = new Entry
                            {
                                FirstSeenUtc = entry.FirstSeenUtc.ToUniversalTime(),
                                Realerted = entry.Realerted,
                            };
            }
            catch
            {
                // A damaged tracking file just means re-alert clocks restart.
                _entries.Clear();
            }
        }
    }

    public void Save()
    {
        lock (_gate)
        {
            var json = JsonSerializer.Serialize(
                new Dictionary<string, Entry>(_entries), AtomicJson.IndentedOptions);
            AtomicJson.Write(_path, json);
        }
    }

    /// <summary>Records first sight of an id. Returns true when it is genuinely new to the store.</summary>
    public bool Track(string id, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;
        lock (_gate)
        {
            if (_entries.ContainsKey(id))
                return false;
            _entries[id] = new Entry { FirstSeenUtc = nowUtc.ToUniversalTime() };
            return true;
        }
    }

    public DateTimeOffset? FirstSeenUtc(string id)
    {
        lock (_gate)
            return _entries.TryGetValue(id, out var entry) ? entry.FirstSeenUtc : null;
    }

    public bool HasRealerted(string id)
    {
        lock (_gate)
            return _entries.TryGetValue(id, out var entry) && entry.Realerted;
    }

    /// <summary>Spends the id's one re-alert (creating a spent entry for an unknown id is a no-op).</summary>
    public void MarkRealerted(string id)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(id, out var entry))
                entry.Realerted = true;
        }
    }

    /// <summary>Drops entries first seen before <paramref name="cutoffUtc"/>. Returns how many were removed.</summary>
    public int PruneOlderThan(DateTimeOffset cutoffUtc)
    {
        lock (_gate)
        {
            var stale = _entries
                .Where(kv => kv.Value.FirstSeenUtc < cutoffUtc)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var id in stale)
                _entries.Remove(id);
            return stale.Count;
        }
    }

    /// <summary>Drops entries whose id is no longer in the live history. Returns how many were removed.</summary>
    public int PruneExcept(IReadOnlySet<string> liveIds)
    {
        lock (_gate)
        {
            var stale = _entries.Keys.Where(id => !liveIds.Contains(id)).ToList();
            foreach (var id in stale)
                _entries.Remove(id);
            return stale.Count;
        }
    }

    public void Clear()
    {
        lock (_gate) _entries.Clear();
        try { if (File.Exists(_path)) File.Delete(_path); }
        catch { /* a later Save recreates it */ }
    }
}

/// <summary>
/// The one gentle re-alert (v0.3.0): a comment/mention that is still
/// unread <see cref="Delay"/> after FaceTray first saw it earns exactly
/// one more pop-up. Pure decision — the poller supplies the clock and the
/// tracking state, and still applies the toast decision (pop-ups on,
/// alert types, quiet hours) and the Windows notification state on top.
/// </summary>
public static class RealertPolicy
{
    public static readonly TimeSpan Delay = TimeSpan.FromMinutes(30);

    public static bool IsDue(
        NotificationItem item, DateTimeOffset? firstSeenUtc, bool alreadyRealerted, DateTimeOffset nowUtc)
    {
        if (alreadyRealerted || firstSeenUtc is not { } seen)
            return false;
        if (!item.IsUnread)
            return false;
        if (NotificationTypeGroups.Of(item.Type) != NotificationTypeGroup.CommentsAndMentions)
            return false;
        return nowUtc - seen >= Delay;
    }
}
