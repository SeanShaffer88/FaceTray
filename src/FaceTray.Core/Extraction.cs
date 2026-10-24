using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FaceTray.Core;

/// <summary>
/// The extraction script run in the hidden WebView2 on
/// facebook.com/notifications, plus its sentinel contract:
///   • returns a JSON string: {"signedOut":bool,"strategy":1|2|3,"items":[{text,href,unread,avatars,timeText,typeHint,ts}]}
///   • signedOut is true when a login form / login URL is detected.
///
/// The script deliberately uses ONLY semantic signals — anchors and their
/// hrefs (notifications links carry notif_id / notif_t), aria-labels, role
/// attributes, image-bearing elements (img, SVG image, background-image),
/// time carriers (a time element's datetime, an abbr title) and visible
/// text patterns — and never Facebook's obfuscated class names, which
/// change without notice.
///
/// v0.3.0: rows are discovered by THREE strategies, tried in order, so a
/// single Facebook markup change has to break all three before the list
/// goes empty (the health check reports which strategy produced the list):
///   S1 (primary): anchors carrying notif_id / notif_t — the field-
///       verified v0.1 approach.
///   S2 (permalink fallback): anchors to post/story-style targets whose
///       row container carries a visible time leaf.
///   S3 (row fallback): listitem/article rows containing an anchor and a
///       time leaf; the row's first anchor is the target.
/// </summary>
public static class ExtractionScript
{
    public const string Script = """
(() => {
  const out = { signedOut: false, strategy: 1, items: [] };
  try {
    const href = location.href.toLowerCase();
    if (href.includes('/login') || href.includes('/checkpoint')) out.signedOut = true;
    if (document.querySelector('form[action*="login"] input[type="password"], input[name="email"][type="text"]')) {
      // A visible password field on the notifications page means signed out.
      const pw = document.querySelector('input[type="password"]');
      if (pw) out.signedOut = true;
    }
    if (out.signedOut) return JSON.stringify(out);

    const timeRe = /^(\d+)\s*(m|min|mins|h|hr|hrs|d|w|mo|y)$/i;
    const softTimeRe = /^(just now|yesterday)$/i;

    const absOf = (a) => {
      const raw = a.getAttribute('href') || '';
      if (!raw) return '';
      try { return new URL(raw, location.origin).toString(); } catch { return ''; }
    };

    // Row container: climb to the nearest listitem-ish / labelled ancestor.
    const findRow = (a) => {
      let row = a;
      for (let i = 0; i < 6 && row.parentElement; i++) {
        const p = row.parentElement;
        const role = (p.getAttribute && p.getAttribute('role')) || '';
        if (role === 'listitem' || role === 'article' || role === 'row' || p.tagName === 'LI') { row = p; break; }
        row = p;
        if ((row.innerText || '').length > 40) break;
      }
      return row;
    };

    // A leaf element whose whole text is a relative time label (9h, 1d, …).
    const timeLeafOf = (row) => {
      const leaves = row.querySelectorAll('span, abbr, div, time');
      for (const el of leaves) {
        const t = (el.textContent || '').trim();
        if (timeRe.test(t) || softTimeRe.test(t)) return el;
      }
      return null;
    };

    // Shared row reader: from a chosen anchor, build one payload item.
    const readRow = (a, abs) => {
      const row = findRow(a);
      const text = ((row.innerText || a.innerText || a.getAttribute('aria-label') || '') + '')
        .replace(/\s+/g, ' ').trim();
      if (!text || text.length < 8) return null;

      // Unread heuristic: an explicit "Unread" accessible label in the row.
      let unread = false;
      if (row.querySelector('[aria-label*="Unread"], [aria-label*="unread"]')) unread = true;

      // Type hint from accessible labels on icons inside the row.
      let typeHint = '';
      const hintEl = row.querySelector('[aria-label*="comment" i], [aria-label*="like" i], [aria-label*="birthday" i], [aria-label*="mention" i], [aria-label*="react" i]');
      if (hintEl) typeHint = hintEl.getAttribute('aria-label') || '';

      // Avatars: profile pictures in the row (first = primary). Facebook
      // renders them three ways — <img> elements, SVG <image> elements,
      // and elements with a background-image — so collect all three, in
      // that order, http(s) URLs only, deduped, capped at 4. Semantic
      // only: element kinds and URL-bearing attributes, never classes.
      const avatars = [];
      const seenAvatars = new Set();
      const pushAvatar = (u) => {
        if (u && u.startsWith('http') && !seenAvatars.has(u) && avatars.length < 4) {
          seenAvatars.add(u);
          avatars.push(u);
        }
      };
      row.querySelectorAll('img').forEach(img => {
        let src = img.currentSrc || img.getAttribute('src') || '';
        if (!src || src.startsWith('data:')) {
          // currentSrc/src gave nothing usable — take the first srcset URL.
          const first = (img.getAttribute('srcset') || '').split(',')[0].trim().split(/\s+/)[0];
          if (first) src = first;
        }
        pushAvatar(src);
      });
      row.querySelectorAll('image').forEach(im => {
        pushAvatar(im.getAttribute('href')
          || im.getAttributeNS('http://www.w3.org/1999/xlink', 'href')
          || im.getAttribute('xlink:href') || '');
      });
      row.querySelectorAll('*').forEach(el => {
        const bg = getComputedStyle(el).getPropertyValue('background-image') || '';
        const m = bg.match(/url\(["']?(https?:[^"')]+)["']?\)/);
        if (m) pushAvatar(m[1]);
      });

      // Relative time from the leaf, plus the absolute timestamp when a
      // semantic time carrier exposes one: the leaf itself may be a time
      // element (datetime attribute) or an abbr (title attribute), or
      // wrap one; otherwise any time element in the row speaks for it.
      let timeText = '';
      let ts = '';
      const leaf = timeLeafOf(row);
      if (leaf) {
        timeText = (leaf.textContent || '').trim();
        if (leaf.tagName === 'TIME') ts = leaf.getAttribute('datetime') || '';
        else if (leaf.tagName === 'ABBR') ts = leaf.getAttribute('title') || '';
        if (!ts && leaf.querySelector) {
          const inner = leaf.querySelector('time[datetime], abbr[title]');
          if (inner) ts = inner.getAttribute('datetime') || inner.getAttribute('title') || '';
        }
      }
      if (!ts) {
        const carrier = row.querySelector('time[datetime]');
        if (carrier) ts = carrier.getAttribute('datetime') || '';
      }

      return { text, href: abs, unread, avatars, timeText, typeHint, ts };
    };

    // One discovery pass over a candidate anchor list.
    const runPass = (anchors) => {
      const items = [];
      const seen = new Set();
      for (const a of anchors) {
        const abs = absOf(a);
        if (!abs) continue;
        if (!abs.toLowerCase().startsWith('https://')) continue;
        const key = abs.split('#')[0];
        if (seen.has(key)) continue;
        const item = readRow(a, abs);
        if (!item) continue;
        seen.add(key);
        items.push(item);
        if (items.length >= 30) break;
      }
      return items;
    };

    // Strategy 1 (primary): notification markers on the link itself.
    const s1 = [];
    document.querySelectorAll('a[href]').forEach(a => {
      const abs = absOf(a);
      if (!abs) return;
      const low = abs.toLowerCase();
      if (!(low.includes('notif_id') || low.includes('notif_t') || low.includes('/notifications'))) return;
      if (low.endsWith('/notifications') || low.includes('/notifications?')) {
        // The "see all" / footer links point back at the list itself — not a notification.
        if (!low.includes('notif_id') && !low.includes('notif_t')) return;
      }
      s1.push(a);
    });
    let items = runPass(s1);
    if (items.length > 0) { out.strategy = 1; out.items = items; return JSON.stringify(out); }

    // Strategy 2 (permalink fallback): post/story-style targets in a row
    // that carries a visible time leaf.
    const postRe = /\/(posts|story\.php|photo|watch|videos|permalink|share)\b/;
    const s2 = [];
    document.querySelectorAll('a[href]').forEach(a => {
      const abs = absOf(a);
      if (!abs) return;
      let path = '';
      try { path = new URL(abs).pathname.toLowerCase(); } catch { return; }
      if (!postRe.test(path)) return;
      if (!timeLeafOf(findRow(a))) return;
      s2.push(a);
    });
    items = runPass(s2);
    if (items.length > 0) { out.strategy = 2; out.items = items; return JSON.stringify(out); }

    // Strategy 3 (row fallback): structured rows holding an anchor and a
    // time leaf; the row's first anchor is the notification target.
    const s3 = [];
    document.querySelectorAll('[role="listitem"], [role="article"], li').forEach(row => {
      if (!timeLeafOf(row)) return;
      const a = row.querySelector('a[href]');
      if (a) s3.push(a);
    });
    items = runPass(s3);
    if (items.length > 0) { out.strategy = 3; out.items = items; return JSON.stringify(out); }

    // Nothing matched anywhere: report the empty list under the primary
    // strategy (the health check flags the poll from the title count).
    out.strategy = 1;
    out.items = [];
    return JSON.stringify(out);
  } catch (e) {
    out.error = String(e);
    return JSON.stringify(out);
  }
})()
""";
}

/// <summary>One raw row as the script returns it (before C# validation).</summary>
public sealed class RawNotification
{
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("href")] public string? Href { get; set; }
    [JsonPropertyName("unread")] public bool Unread { get; set; }
    [JsonPropertyName("avatars")] public List<string>? Avatars { get; set; }
    [JsonPropertyName("timeText")] public string? TimeText { get; set; }
    [JsonPropertyName("typeHint")] public string? TypeHint { get; set; }

    /// <summary>Absolute time text from a semantic time carrier, when the row exposed one.</summary>
    [JsonPropertyName("ts")] public string? Ts { get; set; }
}

/// <summary>The outcome of one extraction.</summary>
public sealed class ExtractionResult
{
    public bool SignedOut { get; init; }
    public IReadOnlyList<NotificationItem> Items { get; init; } = Array.Empty<NotificationItem>();
    public int UnreadCount { get; init; }

    /// <summary>
    /// Unread flags exactly as the page reported them, before the local
    /// read store and snoozes were applied. The extraction-health check
    /// compares this against the page-title count.
    /// </summary>
    public int RawUnreadCount { get; init; }

    /// <summary>
    /// Which discovery strategy produced the items (v0.3.0): 1 primary,
    /// 2 permalink fallback, 3 row fallback. Recorded on the poll result
    /// and surfaced in diagnostics — a strategy above 1 means Facebook's
    /// markup moved under the primary.
    /// </summary>
    public int Strategy { get; init; } = 1;
}

/// <summary>
/// Turns the script's JSON into validated <see cref="NotificationItem"/>s:
/// drops non-Facebook / non-https links, dedupes by id, caps at
/// <see cref="MaxItems"/>, applies the locally-read set, active snoozes
/// and pins, and counts unread.
/// </summary>
public static class ExtractionParser
{
    /// <summary>Per-poll depth: how many of the page's rows one poll keeps.</summary>
    public const int MaxItems = 30;

    private sealed class Envelope
    {
        [JsonPropertyName("signedOut")] public bool SignedOut { get; set; }
        [JsonPropertyName("strategy")] public int Strategy { get; set; } = 1;
        [JsonPropertyName("items")] public List<RawNotification>? Items { get; set; }
    }

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    /// <param name="json">The inner JSON string produced by the script (already unwrapped from ExecuteScriptAsync's string encoding).</param>
    /// <param name="locallyReadIds">Ids marked read locally; they override the page's unread flag.</param>
    /// <param name="titleCount">Count parsed from the page title, or null.</param>
    /// <param name="snoozedIds">Ids snoozed right now; like local reads, they render as read.</param>
    /// <param name="pinnedIds">Ids pinned by the user; flagged on the items for the ticker's Pinned section.</param>
    public static ExtractionResult Parse(
        string? json,
        IReadOnlySet<string>? locallyReadIds,
        int? titleCount = null,
        IReadOnlySet<string>? snoozedIds = null,
        IReadOnlySet<string>? pinnedIds = null)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new ExtractionResult { SignedOut = false };

        Envelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<Envelope>(json, Options);
        }
        catch (JsonException)
        {
            return new ExtractionResult { SignedOut = false };
        }
        if (envelope is null)
            return new ExtractionResult { SignedOut = false };
        if (envelope.SignedOut)
            return new ExtractionResult { SignedOut = true };

        var items = new List<NotificationItem>();
        var seenIds = new HashSet<string>();
        var rawUnread = 0;
        foreach (var raw in envelope.Items ?? new List<RawNotification>())
        {
            var item = NotificationItem.Create(
                raw.Text, raw.Href, raw.TimeText, raw.Unread, raw.Avatars, raw.TypeHint);
            if (item is null)
                continue;
            if (!seenIds.Add(item.Id))
                continue;
            if (item.IsUnread)
                rawUnread++;
            item.TimestampUtc = ParseTimestamp(raw.Ts);
            if (locallyReadIds is not null && locallyReadIds.Contains(item.Id))
                item.IsUnread = false;
            if (snoozedIds is not null && snoozedIds.Contains(item.Id))
                item.IsUnread = false;
            if (pinnedIds is not null && pinnedIds.Contains(item.Id))
                item.IsPinned = true;
            items.Add(item);
            if (items.Count >= MaxItems)
                break;
        }

        var parsedUnread = items.Count(i => i.IsUnread);
        return new ExtractionResult
        {
            SignedOut = false,
            Items = items,
            UnreadCount = TitleCount.ResolveUnreadCount(parsedUnread, items.Count, titleCount),
            RawUnreadCount = rawUnread,
            Strategy = envelope.Strategy is >= 1 and <= 3 ? envelope.Strategy : 1,
        };
    }

    /// <summary>
    /// Best-effort conversion of a scraped absolute-time string to UTC:
    /// invariant culture first, then the current culture; anything
    /// unparseable (or missing) yields null — a timestamp is a tooltip
    /// nicety, never a reason to drop or break a row.
    /// </summary>
    internal static DateTimeOffset? ParseTimestamp(string? ts)
    {
        if (string.IsNullOrWhiteSpace(ts))
            return null;
        var text = ts.Trim();
        const DateTimeStyles styles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, styles, out var parsed))
            return parsed.ToUniversalTime();
        if (DateTimeOffset.TryParse(text, CultureInfo.CurrentCulture, styles, out parsed))
            return parsed.ToUniversalTime();
        return null;
    }

    /// <summary>
    /// ExecuteScriptAsync returns a JSON-encoded string; this unwraps it to
    /// the inner JSON text. Returns null when the result is not a string.
    /// </summary>
    public static string? UnwrapScriptResult(string? executeScriptResultJson)
    {
        if (string.IsNullOrWhiteSpace(executeScriptResultJson))
            return null;
        try
        {
            return JsonSerializer.Deserialize<string>(executeScriptResultJson);
        }
        catch (JsonException)
        {
            // Some hosts return the raw JSON object instead of a string.
            return executeScriptResultJson.Trim().StartsWith("{") ? executeScriptResultJson : null;
        }
    }
}
