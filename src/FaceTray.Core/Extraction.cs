using System.Text.Json;
using System.Text.Json.Serialization;

namespace FaceTray.Core;

/// <summary>
/// The extraction script run in the hidden WebView2 on
/// facebook.com/notifications, plus its sentinel contract:
///   • returns a JSON string: {"signedOut":bool,"items":[{text,href,unread,avatars,timeText,typeHint}]}
///   • signedOut is true when a login form / login URL is detected.
///
/// The script deliberately uses ONLY semantic signals — anchors and their
/// hrefs (notifications links carry notif_id / notif_t), aria-labels, role
/// attributes, image-bearing elements (img, SVG image, background-image)
/// and visible text patterns — and never Facebook's obfuscated class names,
/// which change without notice.
/// </summary>
public static class ExtractionScript
{
    public const string Script = """
(() => {
  const out = { signedOut: false, items: [] };
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
    const seen = new Set();
    const anchors = document.querySelectorAll('a[href]');
    for (const a of anchors) {
      const rawHref = a.getAttribute('href') || '';
      if (!rawHref) continue;
      let abs = '';
      try { abs = new URL(rawHref, location.origin).toString(); } catch { continue; }
      const low = abs.toLowerCase();
      if (!low.startsWith('https://')) continue;
      // Notification targets are links carrying Facebook's notification markers.
      if (!(low.includes('notif_id') || low.includes('notif_t') || low.includes('/notifications'))) continue;
      if (low.endsWith('/notifications') || low.includes('/notifications?')) {
        // The "see all" / footer links point back at the list itself — not a notification.
        if (!low.includes('notif_id') && !low.includes('notif_t')) continue;
      }

      // Row container: climb to the nearest listitem-ish / labelled ancestor.
      let row = a;
      for (let i = 0; i < 6 && row.parentElement; i++) {
        const p = row.parentElement;
        const role = (p.getAttribute && p.getAttribute('role')) || '';
        if (role === 'listitem' || role === 'article' || role === 'row' || p.tagName === 'LI') { row = p; break; }
        row = p;
        if ((row.innerText || '').length > 40) break;
      }

      const text = ((row.innerText || a.innerText || a.getAttribute('aria-label') || '') + '')
        .replace(/\s+/g, ' ').trim();
      if (!text || text.length < 8) continue;

      // Unread heuristic: an explicit "Unread" label, or Facebook's unread
      // dot element (aria-hidden visual indicator with an "unread" label nearby).
      let unread = false;
      const labelled = row.querySelector('[aria-label*="Unread"], [aria-label*="unread"]');
      if (labelled) unread = true;

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

      // Relative time: a leaf element whose whole text is like 9h / 1d / 25m.
      let timeText = '';
      const leaves = row.querySelectorAll('span, abbr, div');
      for (const el of leaves) {
        const t = (el.textContent || '').trim();
        if (timeRe.test(t)) { timeText = t; break; }
        if (/^(just now|yesterday)$/i.test(t)) { timeText = t; break; }
      }

      const key = abs.split('#')[0];
      if (seen.has(key)) continue;
      seen.add(key);
      out.items.push({ text, href: abs, unread, avatars, timeText, typeHint });
      if (out.items.length >= 30) break;
    }
  } catch (e) {
    out.error = String(e);
  }
  return JSON.stringify(out);
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
}

/// <summary>
/// Turns the script's JSON into validated <see cref="NotificationItem"/>s:
/// drops non-Facebook / non-https links, dedupes by id, caps at
/// <see cref="MaxItems"/>, applies the locally-read set and active
/// snoozes, and counts unread.
/// </summary>
public static class ExtractionParser
{
    /// <summary>Per-poll depth: how many of the page's rows one poll keeps.</summary>
    public const int MaxItems = 30;

    private sealed class Envelope
    {
        [JsonPropertyName("signedOut")] public bool SignedOut { get; set; }
        [JsonPropertyName("items")] public List<RawNotification>? Items { get; set; }
    }

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    /// <param name="json">The inner JSON string produced by the script (already unwrapped from ExecuteScriptAsync's string encoding).</param>
    /// <param name="locallyReadIds">Ids marked read locally; they override the page's unread flag.</param>
    /// <param name="titleCount">Count parsed from the page title, or null.</param>
    /// <param name="snoozedIds">Ids snoozed right now; like local reads, they render as read.</param>
    public static ExtractionResult Parse(
        string? json,
        IReadOnlySet<string>? locallyReadIds,
        int? titleCount = null,
        IReadOnlySet<string>? snoozedIds = null)
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
            if (locallyReadIds is not null && locallyReadIds.Contains(item.Id))
                item.IsUnread = false;
            if (snoozedIds is not null && snoozedIds.Contains(item.Id))
                item.IsUnread = false;
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
        };
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
