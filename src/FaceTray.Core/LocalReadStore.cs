using System.Text.Json;

namespace FaceTray.Core;

/// <summary>
/// Locally-read notification ids, persisted as a JSON array. Facebook's own
/// read state only changes when the user visits the site; FaceTray marks a
/// notification read locally the moment it is clicked here, so the counter
/// responds instantly. Bounded: the oldest ids are dropped past the cap.
/// </summary>
public sealed class LocalReadStore
{
    public const int MaxIds = 500;

    private readonly string _path;
    private readonly HashSet<string> _ids = new();
    private readonly object _gate = new();

    public LocalReadStore(string path) => _path = path;

    public IReadOnlySet<string> Ids
    {
        get { lock (_gate) return new HashSet<string>(_ids); }
    }

    public bool IsRead(string id)
    {
        lock (_gate) return _ids.Contains(id);
    }

    public void Load()
    {
        lock (_gate)
        {
            _ids.Clear();
            try
            {
                if (!File.Exists(_path))
                    return;
                var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_path));
                if (list is not null)
                    foreach (var id in list.Where(s => !string.IsNullOrWhiteSpace(s)))
                        _ids.Add(id);
            }
            catch
            {
                // A damaged read-state file just means everything looks unread once.
                _ids.Clear();
            }
        }
    }

    public void Save()
    {
        lock (_gate)
        {
            var json = JsonSerializer.Serialize(_ids.ToList(), AtomicJson.IndentedOptions);
            AtomicJson.Write(_path, json);
        }
    }

    /// <summary>Returns true when the id was newly marked (state changed).</summary>
    public bool MarkRead(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;
        lock (_gate)
        {
            if (!_ids.Add(id))
                return false;
            TrimLocked();
            return true;
        }
    }

    public void MarkAllRead(IEnumerable<string> ids)
    {
        lock (_gate)
        {
            foreach (var id in ids)
                if (!string.IsNullOrWhiteSpace(id))
                    _ids.Add(id);
            TrimLocked();
        }
    }

    public void Clear()
    {
        lock (_gate) _ids.Clear();
        try { if (File.Exists(_path)) File.Delete(_path); }
        catch { /* a later Save recreates it */ }
    }

    private void TrimLocked()
    {
        // HashSet has no order; trimming is arbitrary-but-bounded, which is
        // fine: ids past the cap belong to notifications long off the list.
        while (_ids.Count > MaxIds)
            _ids.Remove(_ids.First());
    }
}

/// <summary>Last-good notification list, persisted so the ticker never opens empty after a network blip.</summary>
public static class NotificationCacheStore
{
    public static void Save(string path, IReadOnlyList<NotificationItem> items)
    {
        var json = JsonSerializer.Serialize(items, AtomicJson.IndentedOptions);
        AtomicJson.Write(path, json);
    }

    public static IReadOnlyList<NotificationItem> Load(string path, IReadOnlySet<string>? locallyReadIds)
    {
        try
        {
            if (!File.Exists(path))
                return Array.Empty<NotificationItem>();
            var items = JsonSerializer.Deserialize<List<NotificationItem>>(
                File.ReadAllText(path), AtomicJson.IndentedOptions);
            if (items is null)
                return Array.Empty<NotificationItem>();
            var valid = new List<NotificationItem>();
            foreach (var item in items)
            {
                if (item is null || string.IsNullOrWhiteSpace(item.Id)
                    || !UrlGuard.IsAllowedNotificationUrl(item.Href))
                    continue;
                // Heal v0.1.0 cache entries, whose Text still ends with the
                // relative-time token the ticker prints separately.
                item.Text = NotificationItem.StripTrailingTime(
                    item.Text ?? "", item.RelativeTimeText);
                item.AvatarUrls ??= new List<string>();
                if (locallyReadIds is not null && locallyReadIds.Contains(item.Id))
                    item.IsUnread = false;
                valid.Add(item);
                if (valid.Count >= ExtractionParser.MaxItems)
                    break;
            }
            return valid;
        }
        catch
        {
            return Array.Empty<NotificationItem>();
        }
    }

    public static void Clear(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }
}
