using FaceTray.Core;
using Xunit;

namespace FaceTray.Core.Tests;

public class TitleCountTests
{
    [Theory]
    [InlineData("(3) Facebook", 3)]
    [InlineData("(9+) Facebook", 9)]
    [InlineData("(20+) Notifications", 20)]
    [InlineData("(1) Travis Iiams commented", 1)]
    [InlineData("Facebook", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("Notifications (3)", null)] // only a leading badge counts
    public void Parse_cases(string? title, int? expected)
        => Assert.Equal(expected, TitleCount.Parse(title));

    [Theory]
    [InlineData(2, 5, null, 2)]   // parsed only
    [InlineData(2, 5, 4, 4)]      // title larger (rows below fold) → title wins
    [InlineData(4, 5, 2, 4)]      // parsed larger → parsed wins
    [InlineData(0, 0, 7, 7)]      // no rows extracted → title is the only signal
    [InlineData(0, 0, null, 0)]   // nothing known → 0
    public void ResolveUnreadCount_cases(int parsedUnread, int itemCount, int? title, int expected)
        => Assert.Equal(expected, TitleCount.ResolveUnreadCount(parsedUnread, itemCount, title));
}

public class BadgeTextTests
{
    [Theory]
    [InlineData(0, null)]
    [InlineData(-3, null)]
    [InlineData(1, "1")]
    [InlineData(9, "9")]
    [InlineData(10, "9+")]
    [InlineData(99, "9+")]
    public void Badge_cases(int count, string? expected)
        => Assert.Equal(expected, BadgeText.For(count));

    [Fact]
    public void Tooltip_cases()
    {
        Assert.Equal("FaceTray", BadgeText.TooltipFor(0));
        Assert.Equal("FaceTray — 3 unread", BadgeText.TooltipFor(3));
    }
}

public class UrlGuardTests
{
    [Theory]
    [InlineData("https://www.facebook.com/photo/?fbid=1&notif_id=2", true)]
    [InlineData("https://facebook.com/notifications", true)]
    [InlineData("https://m.facebook.com/story.php?id=1", true)]
    [InlineData("http://www.facebook.com/photo/?fbid=1", false)]       // https only
    [InlineData("https://www.facebook.com.evil.example/photo", false)] // look-alike host
    [InlineData("https://evilfacebook.com/photo", false)]
    [InlineData("https://example.com/facebook", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("/relative/path", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Allowed_cases(string? url, bool expected)
        => Assert.Equal(expected, UrlGuard.IsAllowedNotificationUrl(url));

    [Fact]
    public void Normalize_strips_fragment_keeps_query()
    {
        Assert.True(UrlGuard.TryNormalizeNotificationUrl(
            "https://www.facebook.com/photo/?fbid=1#frag", out var normalized));
        Assert.Equal("https://www.facebook.com/photo/?fbid=1", normalized);
    }

    [Fact]
    public void Redact_never_includes_query()
    {
        var redacted = UrlGuard.RedactForLog("https://www.facebook.com/photo/?fbid=secret123");
        Assert.Equal("www.facebook.com/photo/", redacted);
        Assert.DoesNotContain("secret123", redacted);
    }

    [Theory]
    [InlineData("https://www.facebook.com/login/?next=notifications", true)]
    [InlineData("https://www.facebook.com/checkpoint/123", true)]
    [InlineData("https://www.facebook.com/notifications", false)]
    public void LoginUrl_cases(string url, bool expected)
        => Assert.Equal(expected, UrlGuard.LooksLikeLoginUrl(url));
}
