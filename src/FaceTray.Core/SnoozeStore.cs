using System.Text.Json;

namespace FaceTray.Core;

/// <summary>
/// Snoozed notification ids → the UTC instant the snooze ends. A snoozed
/// notification renders as read and does not count toward the tray counter;
/// when the instant passes it simply stops being applied, so the item
/// resurfaces as unread on the next poll with no extra bookkeeping.
/// Persisted as one atomic JSON object (id → ISO-8601 UTC).
/// </summary>
public sealed class SnoozeStore
{
    private readonly string _path;
    private readonly Dictionary<string, DateTimeOffset> _until = new();
    private readonly object _gate = new();

    public SnoozeStore(string path) => _path = path;

    public string Path => _path;

    public void Load()
    {
        lock (_gate)
        {
            _until.Clear();
            try
            {
                if (!File.Exists(_path))
                    return;
                var map = JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(
                    File.ReadAllText(_path));
                if (map is not null)
                    foreach (var (id, until) in map)
                        if (!string.IsNullOrWhiteSpace(id))
                            _until[id] = until.ToUniversalTime();
            }
            catch
            {
                // A damaged snooze file just means nothing is snoozed.
                _until.Clear();
            }
        }
    }

    public void Save()
    {
        lock (_gate)
        {
            var json = JsonSerializer.Serialize(
                new Dictionary<string, DateTimeOffset>(_until), AtomicJson.IndentedOptions);
            AtomicJson.Write(_path, json);
        }
    }

    public void Snooze(string id, DateTimeOffset untilUtc)
    {
        if (string.IsNullOrWhiteSpace(id))
            return;
        lock (_gate) _until[id] = untilUtc.ToUniversalTime();
    }

    /// <summary>Returns true when a snooze was actually removed.</summary>
    public bool Unsnooze(string id)
    {
        lock (_gate) return _until.Remove(id);
    }

    public bool IsSnoozed(string id, DateTimeOffset nowUtc)
    {
        lock (_gate)
            return _until.TryGetValue(id, out var until) && until > nowUtc;
    }

    /// <summary>Ids whose snooze is still in force at <paramref name="nowUtc"/>.</summary>
    public IReadOnlySet<string> ActiveIds(DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            var active = new HashSet<string>();
            foreach (var (id, until) in _until)
                if (until > nowUtc)
                    active.Add(id);
            return active;
        }
    }

    public DateTimeOffset? SnoozedUntil(string id)
    {
        lock (_gate)
            return _until.TryGetValue(id, out var until) ? until : null;
    }

    /// <summary>Drops entries whose snooze has expired. Returns how many were removed.</summary>
    public int PruneExpired(DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            var expired = _until.Where(kv => kv.Value <= nowUtc).Select(kv => kv.Key).ToList();
            foreach (var id in expired)
                _until.Remove(id);
            return expired.Count;
        }
    }

    public void Clear()
    {
        lock (_gate) _until.Clear();
        try { if (File.Exists(_path)) File.Delete(_path); }
        catch { /* a later Save recreates it */ }
    }
}

/// <summary>When a snooze ends, in the user's local clock terms.</summary>
public static class SnoozeTiming
{
    public static DateTimeOffset OneHourFrom(DateTimeOffset now) => now.AddHours(1);

    /// <summary>
    /// “Until tomorrow”: 08:00 local time on the day after
    /// <paramref name="now"/> (in now's own UTC offset). Snoozing late in
    /// the evening therefore still buys a full night plus the morning.
    /// </summary>
    public static DateTimeOffset UntilTomorrowMorning(DateTimeOffset now)
    {
        var tomorrow = now.Date.AddDays(1);
        return new DateTimeOffset(tomorrow.AddHours(8), now.Offset);
    }
}
