namespace FaceTray.Core;

/// <summary>
/// One ticker row after repeat-collapsing: a primary notification plus
/// the older notifications folded under it (v0.3.0). A fold of one is
/// just a normal row.
/// </summary>
public sealed class NotificationFold
{
    public NotificationFold(NotificationItem primary, IReadOnlyList<NotificationItem> folded)
    {
        Primary = primary;
        Folded = folded;
    }

    /// <summary>The newest item of the group — the one the row renders.</summary>
    public NotificationItem Primary { get; }

    /// <summary>Older same-target items hidden under the primary until expanded.</summary>
    public IReadOnlyList<NotificationItem> Folded { get; }

    /// <summary>Total items in the fold, primary included.</summary>
    public int Count => 1 + Folded.Count;

    public bool IsGroup => Folded.Count > 0;

    /// <summary>Primary first, then the folded items in list order.</summary>
    public IEnumerable<NotificationItem> All
    {
        get
        {
            yield return Primary;
            foreach (var item in Folded)
                yield return item;
        }
    }
}

/// <summary>
/// Collapses repeats (v0.3.0): notifications that share a type group AND
/// a target path (query string ignored — the same post arrives with
/// different tracking parameters) fold into one row, newest first. The
/// “several comments on your post” case is the target: one expandable
/// row instead of a screen of near-duplicates. Pinned items never fold —
/// each is always its own row. Input order (newest first) is preserved:
/// a fold sits where its primary sits.
/// </summary>
public static class NotificationFolding
{
    /// <summary>The fold identity of an item: type group + target path without query/fragment.</summary>
    public static string FoldKey(NotificationItem item)
    {
        var href = item.Href ?? "";
        string path;
        if (Uri.TryCreate(href, UriKind.Absolute, out var uri))
        {
            path = uri.AbsolutePath;
        }
        else
        {
            path = href;
            var q = path.IndexOf('?');
            if (q >= 0)
                path = path[..q];
            var h = path.IndexOf('#');
            if (h >= 0)
                path = path[..h];
        }
        return NotificationTypeGroups.Key(NotificationTypeGroups.Of(item.Type))
               + "|" + path.ToLowerInvariant();
    }

    public static IReadOnlyList<NotificationFold> Fold(
        IReadOnlyList<NotificationItem> items, IReadOnlySet<string>? pinnedIds)
    {
        var order = new List<NotificationFold>();
        var open = new Dictionary<string, List<NotificationItem>>();
        foreach (var item in items)
        {
            var key = FoldKey(item);
            var canFold = !key.EndsWith("|", StringComparison.Ordinal)
                          && !(pinnedIds is not null && pinnedIds.Contains(item.Id));
            if (!canFold)
            {
                // Pinned items, and items with no usable path to tell them
                // apart by, always stand alone.
                order.Add(new NotificationFold(item, Array.Empty<NotificationItem>()));
                continue;
            }
            if (open.TryGetValue(key, out var folded))
            {
                folded.Add(item);
            }
            else
            {
                var list = new List<NotificationItem>();
                open[key] = list;
                order.Add(new NotificationFold(item, list));
            }
        }
        return order;
    }
}
