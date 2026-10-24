using FaceTray.Core;
using Xunit;

namespace FaceTray.Core.Tests;

public class SettingsTests
{
    [Theory]
    [InlineData(5, 30)]
    [InlineData(29, 30)]
    [InlineData(30, 30)]
    [InlineData(60, 60)]
    [InlineData(300, 300)]
    [InlineData(301, 300)]
    [InlineData(9999, 300)]
    public void PollInterval_is_clamped(int input, int expected)
    {
        var settings = new AppSettings { PollIntervalSeconds = input }.Sanitized();
        Assert.Equal(expected, settings.PollIntervalSeconds);
    }

    [Fact]
    public void Defaults_are_as_planned()
    {
        var settings = new AppSettings();
        Assert.Equal(60, settings.PollIntervalSeconds);
        Assert.False(settings.StartWithWindows);
        Assert.False(settings.ShowToasts);
        Assert.False(settings.HasSeenFirstRun);
    }

    [Fact]
    public void Store_roundtrips_and_sanitizes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "facetray-tests-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "settings.json");
        try
        {
            SettingsStore.Save(path, new AppSettings { PollIntervalSeconds = 9999, ShowToasts = true });
            var loaded = SettingsStore.Load(path);
            Assert.Equal(300, loaded.PollIntervalSeconds);
            Assert.True(loaded.ShowToasts);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void Corrupt_settings_are_preserved_and_defaults_returned()
    {
        var dir = Path.Combine(Path.GetTempPath(), "facetray-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        try
        {
            File.WriteAllText(path, "{ this is not json");
            var loaded = SettingsStore.Load(path);
            Assert.Equal(60, loaded.PollIntervalSeconds);
            Assert.True(Directory.GetFiles(dir, "settings.json.corrupt-*").Length == 1);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void Missing_settings_file_gives_defaults()
    {
        var loaded = SettingsStore.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json"));
        Assert.Equal(60, loaded.PollIntervalSeconds);
    }

    [Fact]
    public void Clone_is_independent()
    {
        var original = new AppSettings { PollIntervalSeconds = 120 };
        var clone = original.Clone();
        clone.PollIntervalSeconds = 30;
        Assert.Equal(120, original.PollIntervalSeconds);
    }
}

public class NotificationItemTests
{
    [Fact]
    public void Create_rejects_non_facebook_href()
    {
        var item = NotificationItem.Create("Someone commented on your post.", "https://example.com/x?notif_id=1", "1h", true, null, null);
        Assert.Null(item);
    }

    [Fact]
    public void Create_rejects_empty_text()
    {
        var item = NotificationItem.Create("   ", "https://www.facebook.com/photo/?fbid=1&notif_id=2", "1h", true, null, null);
        Assert.Null(item);
    }

    [Fact]
    public void Id_is_stable_for_same_href()
    {
        var a = NotificationItem.Create("A commented on your post.", "https://www.facebook.com/photo/?fbid=1&notif_id=2", "1h", true, null, null);
        var b = NotificationItem.Create("Different text, same link.", "https://www.facebook.com/photo/?fbid=1&notif_id=2#frag", "2h", false, null, null);
        Assert.NotNull(a); Assert.NotNull(b);
        Assert.Equal(a!.Id, b!.Id);
    }

    [Theory]
    [InlineData("Travis Iiams commented on your post.", null, NotificationType.Comment)]
    [InlineData("Ashley Marquez mentioned you in a comment.", null, NotificationType.Mention)]
    [InlineData("Allan Bailey likes your post.", null, NotificationType.Like)]
    [InlineData("It's Norine Morse's birthday today.", null, NotificationType.Birthday)]
    [InlineData("You have a new friend suggestion.", null, NotificationType.Other)]
    [InlineData("Something happened.", "Comment", NotificationType.Comment)]
    [InlineData("Something happened.", "Birthday", NotificationType.Birthday)]
    public void Type_inference(string text, string? hint, NotificationType expected)
        => Assert.Equal(expected, NotificationItem.InferType(text, hint));

    [Fact]
    public void Actor_guess()
    {
        Assert.Equal("Travis Iiams", NotificationItem.GuessActor("Travis Iiams commented on your post."));
        Assert.Equal("Ashley Marquez", NotificationItem.GuessActor("Ashley Marquez mentioned you in a comment."));
        Assert.Equal("", NotificationItem.GuessActor("You have memories to look back on."));
    }

    [Fact]
    public void StripTrailingTime_exact_match_strips()
    {
        Assert.Equal(
            "Travis Iiams commented on your post.",
            NotificationItem.StripTrailingTime("Travis Iiams commented on your post. 9h", "9h"));
    }

    [Fact]
    public void StripTrailingTime_case_difference_still_strips()
    {
        // Text carries “10H”, the scraped time label says “10h”.
        Assert.Equal(
            "Allan Bailey likes your post.",
            NotificationItem.StripTrailingTime("Allan Bailey likes your post. 10H", "10h"));
    }

    [Fact]
    public void StripTrailingTime_just_now_strips()
    {
        Assert.Equal(
            "Dani Sue commented on your post.",
            NotificationItem.StripTrailingTime("Dani Sue commented on your post. Just now", "Just now"));
        Assert.Equal(
            "Dani Sue commented on your post.",
            NotificationItem.StripTrailingTime("Dani Sue commented on your post. just now", null));
    }

    [Fact]
    public void StripTrailingTime_generic_fallback_strips_without_label()
    {
        Assert.Equal(
            "Ashley Marquez mentioned you in a comment.",
            NotificationItem.StripTrailingTime("Ashley Marquez mentioned you in a comment. 1d", null));
        Assert.Equal(
            "It's Norine Morse's birthday today.",
            NotificationItem.StripTrailingTime("It's Norine Morse's birthday today. Yesterday", ""));
    }

    [Fact]
    public void StripTrailingTime_no_trailing_time_keeps_text()
    {
        Assert.Equal(
            "Travis Iiams commented on your post.",
            NotificationItem.StripTrailingTime("Travis Iiams commented on your post.", "9h"));
        Assert.Equal(
            "You have a new friend suggestion.",
            NotificationItem.StripTrailingTime("You have a new friend suggestion.", null));
    }

    [Fact]
    public void StripTrailingTime_text_that_is_only_a_time_is_kept()
    {
        Assert.Equal("10h", NotificationItem.StripTrailingTime("10h", "10h"));
        Assert.Equal("9h", NotificationItem.StripTrailingTime("9h", null));
        Assert.Equal("Just now", NotificationItem.StripTrailingTime("Just now", "Just now"));
    }

    [Fact]
    public void Create_strips_the_duplicated_time_from_text()
    {
        var item = NotificationItem.Create(
            "Allan Bailey, Joseph Robert Boucher and Travis Iiams like your post: " +
            "\"Faking happiness is exhausting\". 9h",
            "https://www.facebook.com/photo/?fbid=9&notif_id=42", "9h", true, null, null);
        Assert.NotNull(item);
        Assert.Equal(
            "Allan Bailey, Joseph Robert Boucher and Travis Iiams like your post: " +
            "\"Faking happiness is exhausting\".",
            item!.Text);
        Assert.Equal("9h", item.RelativeTimeText);
    }

    [Fact]
    public void Avatars_are_capped_and_deduped()
    {
        var item = NotificationItem.Create("X commented on your post.", "https://www.facebook.com/a?notif_id=1", "1h", true,
            new[] { "https://img/1", "https://img/1", "https://img/2", "https://img/3", "https://img/4", "https://img/5" }, null);
        Assert.NotNull(item);
        Assert.Equal(4, item!.AvatarUrls.Count);
        Assert.Equal("https://img/1", item.PrimaryAvatarUrl);
    }
}
