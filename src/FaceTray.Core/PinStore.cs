using System.Text.Json;

namespace FaceTray.Core;

/// <summary>
/// Pinned notification ids → when each was pinned (v0.3.0). A pinned
/// notification renders in its own “Pinned” section at the top of the
/// ticker and is exempt from the history auto-prune, so it stays until
/// the user unpins it. Persisted as one atomic JSON object
/// (id → ISO-8601 UTC), mirroring <see cref="SnoozeStore"/>.
/// </summary>
public sealed class PinStore
{
    private readonly string _path;
    private readonly Dictionary<string, DateTimeOffset> _pinned = new();
    private readonly object _gate = new();

    public PinStore(string path) => _path = path;

    public string Path => _path;

    public void Load()
    {
        lock (_gate)
        {
            _pinned.Clear();
            try
            {
                if (!File.Exists(_path))
                    return;
                var map = JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(
                    File.ReadAllText(_path));
                if (map is not null)
                    foreach (var (id, pinnedUtc) in map)
                        if (!string.IsNullOrWhiteSpace(id))
                            _pinned[id] = pinnedUtc.ToUniversalTime();
            }
            catch
            {
                // A damaged pin file just means nothing is pinned.
                _pinned.Clear();
            }
        }
    }

    public void Save()
    {
        lock (_gate)
        {
            var json = JsonSerializer.Serialize(
                new Dictionary<string, DateTimeOffset>(_pinned), AtomicJson.IndentedOptions);
            AtomicJson.Write(_path, json);
        }
    }

    public void Pin(string id, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(id))
            return;
        lock (_gate) _pinned[id] = nowUtc.ToUniversalTime();
    }

    /// <summary>Returns true when a pin was actually removed.</summary>
    public bool Unpin(string id)
    {
        lock (_gate) return _pinned.Remove(id);
    }

    public bool IsPinned(string id)
    {
        lock (_gate) return _pinned.ContainsKey(id);
    }

    /// <summary>All pinned ids (a copy).</summary>
    public IReadOnlySet<string> Ids
    {
        get { lock (_gate) return new HashSet<string>(_pinned.Keys); }
    }

    /// <summary>When the id was pinned, or null when it is not pinned.</summary>
    public DateTimeOffset? PinnedUtc(string id)
    {
        lock (_gate)
            return _pinned.TryGetValue(id, out var pinnedUtc) ? pinnedUtc : null;
    }

    /// <summary>Copy of the whole map (backup/restore).</summary>
    public IReadOnlyDictionary<string, DateTimeOffset> Snapshot()
    {
        lock (_gate) return new Dictionary<string, DateTimeOffset>(_pinned);
    }

    /// <summary>Replaces the whole map (backup/restore).</summary>
    public void ReplaceAll(IReadOnlyDictionary<string, DateTimeOffset> pins)
    {
        lock (_gate)
        {
            _pinned.Clear();
            foreach (var (id, pinnedUtc) in pins)
                if (!string.IsNullOrWhiteSpace(id))
                    _pinned[id] = pinnedUtc.ToUniversalTime();
        }
    }

    public void Clear()
    {
        lock (_gate) _pinned.Clear();
        try { if (File.Exists(_path)) File.Delete(_path); }
        catch { /* a later Save recreates it */ }
    }
}
