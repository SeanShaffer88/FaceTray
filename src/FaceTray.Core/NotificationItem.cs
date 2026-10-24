using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace FaceTray.Core;

/// <summary>What a notification is about — drives the ticker's type badge (glyph + colour).</summary>
public enum NotificationType
{
    Other,
    Comment,
    Mention,
    Like,
    Birthday,
}

/// <summary>
/// One Facebook notification, as shown in the ticker. Plain serializable
/// data: the shell never keeps WebView2 objects in the cache, only this.
/// </summary>
public sealed class NotificationItem
{
    /// <summary>Stable id: a short hash of the (normalized) target link.</summary>
    public string Id { get; set; } = "";

    /// <summary>Full notification text, e.g. “Travis Iiams commented on your post.”</summary>
    public string Text { get; set; } = "";

    /// <summary>The actor prefix of <see cref="Text"/> when it could be separated, else empty.</summary>
    public string ActorText { get; set; } = "";

    /// <summary>Absolute https://facebook.com link opened in the default browser on click.</summary>
    public string Href { get; set; } = "";

    /// <summary>Facebook's relative time label as scraped (“9h”, “1d”, …), may be empty.</summary>
    public string RelativeTimeText { get; set; } = "";

    public bool IsUnread { get; set; }

    public NotificationType Type { get; set; } = NotificationType.Other;

    /// <summary>Avatar image URLs, first is the primary avatar. Multi-avatar rows carry several.</summary>
    public List<string> AvatarUrls { get; set; } = new();

    public string? PrimaryAvatarUrl => AvatarUrls.Count > 0 ? AvatarUrls[0] : null;

    /// <summary>
    /// Builds an item from raw scraped parts. Returns null when the link is
    /// not an allowed Facebook URL or the text is empty — the parser drops
    /// those rows rather than showing something unclickable.
    /// </summary>
    public static NotificationItem? Create(
        string? text,
        string? href,
        string? relativeTimeText,
        bool isUnread,
        IEnumerable<string>? avatarUrls,
        string? typeHint)
    {
        var relTime = (relativeTimeText ?? "").Trim();
        // The row's innerText includes the relative-time leaf, so the raw
        // text usually ends with the same token RelativeTimeText carries —
        // strip it or the ticker prints the time twice (v0.1.0 field bug).
        var cleanText = StripTrailingTime(CleanText(text), relTime);
        if (cleanText.Length == 0)
            return null;
        if (!UrlGuard.TryNormalizeNotificationUrl(href, out var normalizedHref))
            return null;

        return new NotificationItem
        {
            Id = ComputeId(normalizedHref),
            Text = cleanText,
            ActorText = GuessActor(cleanText),
            Href = normalizedHref,
            RelativeTimeText = relTime,
            IsUnread = isUnread,
            Type = InferType(cleanText, typeHint),
            AvatarUrls = (avatarUrls ?? Enumerable.Empty<string>())
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .Distinct()
                .Take(4)
                .ToList(),
        };
    }

    // Generic trailing relative-time token (“9h”, “25 min”, “just now”, …).
    private static readonly Regex TrailingTimeToken = new(
        @"\s+(just now|yesterday|\d+\s*(m|min|mins|h|hr|hrs|d|w|mo|y))$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Removes a trailing relative-time token from notification text, so it
    /// is not duplicated by the ticker's own time line. Rules, in order:
    /// (a) when <paramref name="relativeTimeText"/> is non-empty and the text
    /// ends with " " + that value (case-insensitive), that exact suffix goes;
    /// (b) otherwise a generic trailing token matching
    /// "just now" / "yesterday" / N + unit (m, min, mins, h, hr, hrs, d, w,
    /// mo, y) is stripped when it follows other text. Text that is ONLY a
    /// time token is returned unchanged.
    /// </summary>
    public static string StripTrailingTime(string text, string? relativeTimeText)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var rel = (relativeTimeText ?? "").Trim();
        if (rel.Length > 0)
        {
            var suffix = " " + rel;
            if (text.Length > suffix.Length &&
                text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                var cut = text[..^suffix.Length].TrimEnd();
                if (cut.Length > 0)
                    return cut;
            }
        }

        var match = TrailingTimeToken.Match(text);
        if (match.Success && match.Index > 0)
        {
            var cut = text[..match.Index].TrimEnd();
            if (cut.Length > 0)
                return cut;
        }
        return text;
    }

    /// <summary>Stable, short, collision-resistant id for a normalized href.</summary>
    public static string ComputeId(string normalizedHref)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedHref));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    public static string CleanText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";
        var collapsed = Regex.Replace(text, @"\s+", " ").Trim();
        return collapsed.Length <= 500 ? collapsed : collapsed[..500].TrimEnd();
    }

    /// <summary>
    /// Best-effort actor prefix: the text before the first action verb
    /// (“commented”, “mentioned”, “likes”, …). Empty when no verb is found,
    /// in which case the ticker bolds nothing rather than guessing wrong.
    /// </summary>
    public static string GuessActor(string text)
    {
        var match = Regex.Match(text,
            @"^(?<actor>.+?)\s+(?:commented|mentioned|liked|likes|reacted|shared|posted|invited|tagged|accepted|sent|is celebrating)\b",
            RegexOptions.IgnoreCase);
        if (match.Success)
            return match.Groups["actor"].Value.Trim();
        if (Regex.IsMatch(text, @"^It'?s .+? birthday\b", RegexOptions.IgnoreCase))
            return ""; // “It's X's birthday” reads fine unbolded / whole-line.
        return "";
    }

    /// <summary>Type from a scraped icon hint first, then from the text itself.</summary>
    public static NotificationType InferType(string text, string? typeHint)
    {
        var hint = (typeHint ?? "").ToLowerInvariant();
        if (hint.Contains("birthday") || hint.Contains("cake"))
            return NotificationType.Birthday;
        if (hint.Contains("mention"))
            return NotificationType.Mention;
        if (hint.Contains("comment"))
            return NotificationType.Comment;
        if (hint.Contains("like") || hint.Contains("reaction") || hint.Contains("thumb"))
            return NotificationType.Like;

        var t = text.ToLowerInvariant();
        if (t.Contains("birthday"))
            return NotificationType.Birthday;
        if (t.Contains("mentioned you"))
            return NotificationType.Mention;
        if (t.Contains("commented") || t.Contains("comment on") || t.Contains("replied to your comment"))
            return NotificationType.Comment;
        if (t.Contains("likes your") || t.Contains("like your") || t.Contains("reacted to"))
            return NotificationType.Like;
        return NotificationType.Other;
    }
}
