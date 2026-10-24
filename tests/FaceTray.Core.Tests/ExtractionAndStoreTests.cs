using FaceTray.Core;
using Xunit;

namespace FaceTray.Core.Tests;

public class ExtractionParserTests
{
    private const string SampleJson = """
    {"signedOut":false,"items":[
      {"text":"Travis Iiams commented on your post.","href":"https://www.facebook.com/photo/?fbid=1&notif_id=11","unread":true,"avatars":["https://img/travis"],"timeText":"9h","typeHint":""},
      {"text":"It's Norine Morse's birthday today. Help her have a great day.","href":"https://www.facebook.com/events/birthdays/?notif_id=22","unread":true,"avatars":[],"timeText":"9h","typeHint":""},
      {"text":"Dani Sue commented on your post.","href":"https://www.facebook.com/photo/?fbid=2&notif_id=33","unread":false,"avatars":[],"timeText":"2d","typeHint":""}
    ]}
    """;

    [Fact]
    public void Parses_items_and_counts_unread()
    {
        var result = ExtractionParser.Parse(SampleJson, null);
        Assert.False(result.SignedOut);
        Assert.Equal(3, result.Items.Count);
        Assert.Equal(2, result.UnreadCount);
        Assert.Equal(NotificationType.Comment, result.Items[0].Type);
        Assert.Equal(NotificationType.Birthday, result.Items[1].Type);
        Assert.Equal("https://img/travis", result.Items[0].PrimaryAvatarUrl);
    }

    [Fact]
    public void SignedOut_sentinel_short_circuits()
    {
        var result = ExtractionParser.Parse("""{"signedOut":true,"items":[]}""", null);
        Assert.True(result.SignedOut);
        Assert.Empty(result.Items);
    }

    [Fact]
    public void Locally_read_ids_override_unread()
    {
        var first = ExtractionParser.Parse(SampleJson, null);
        var readIds = new HashSet<string> { first.Items[0].Id };
        var second = ExtractionParser.Parse(SampleJson, readIds);
        Assert.False(second.Items[0].IsUnread);
        Assert.Equal(1, second.UnreadCount);
    }

    [Fact]
    public void Bad_rows_are_dropped_not_fatal()
    {
        const string json = """
        {"signedOut":false,"items":[
          {"text":"Off-site spam","href":"https://example.com/x?notif_id=1","unread":true,"avatars":[],"timeText":"1h","typeHint":""},
          {"text":"","href":"https://www.facebook.com/x?notif_id=2","unread":true,"avatars":[],"timeText":"1h","typeHint":""},
          {"text":"Real one commented on your post.","href":"https://www.facebook.com/x?notif_id=3","unread":true,"avatars":[],"timeText":"1h","typeHint":""}
        ]}
        """;
        var result = ExtractionParser.Parse(json, null);
        Assert.Single(result.Items);
        Assert.Equal(1, result.UnreadCount);
    }

    [Fact]
    public void Duplicates_dedupe_and_cap_at_20()
    {
        var rows = string.Join(",", Enumerable.Range(0, 25).Select(i =>
            $$"""{"text":"Person {{i % 5}} commented on your post.","href":"https://www.facebook.com/x?notif_id={{i % 5}}","unread":true,"avatars":[],"timeText":"1h","typeHint":""}"""));
        var result = ExtractionParser.Parse($$"""{"signedOut":false,"items":[{{rows}}]}""", null);
        Assert.Equal(5, result.Items.Count); // 25 rows, 5 distinct links
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    public void Garbage_input_is_empty_result(string? json)
    {
        var result = ExtractionParser.Parse(json, null);
        Assert.False(result.SignedOut);
        Assert.Empty(result.Items);
        Assert.Equal(0, result.UnreadCount);
    }

    [Fact]
    public void Title_count_lifts_unread_when_rows_missing()
    {
        var result = ExtractionParser.Parse("""{"signedOut":false,"items":[]}""", null, titleCount: 4);
        Assert.Equal(4, result.UnreadCount);
    }

    [Fact]
    public void Unwrap_handles_execute_script_encoding()
    {
        var inner = """{"signedOut":true,"items":[]}""";
        var wrapped = System.Text.Json.JsonSerializer.Serialize(inner);
        Assert.Equal(inner, ExtractionParser.UnwrapScriptResult(wrapped));
        Assert.Null(ExtractionParser.UnwrapScriptResult(null));
        Assert.Null(ExtractionParser.UnwrapScriptResult("42"));
    }

    [Fact]
    public void Script_uses_no_obfuscated_class_selectors()
    {
        // Guard rail for future edits: the script must stay semantic.
        Assert.DoesNotContain("className", ExtractionScript.Script);
        Assert.DoesNotContain("x1", ExtractionScript.Script);
        Assert.Contains("notif_id", ExtractionScript.Script);
        Assert.Contains("aria-label", ExtractionScript.Script);
    }

    [Fact]
    public void Parsed_item_text_has_no_trailing_time_but_keeps_the_label()
    {
        // Raw row text from innerText carries the time leaf; the parser's
        // item must not (v0.1.0 printed the time twice per row).
        const string json = """
        {"signedOut":false,"items":[
          {"text":"Travis Iiams commented on your post. 10h","href":"https://www.facebook.com/photo/?fbid=7&notif_id=77","unread":true,"avatars":[],"timeText":"10h","typeHint":""}
        ]}
        """;
        var result = ExtractionParser.Parse(json, null);
        var item = Assert.Single(result.Items);
        Assert.Equal("Travis Iiams commented on your post.", item.Text);
        Assert.Equal("10h", item.RelativeTimeText);
    }

    [Fact]
    public void Script_captures_img_svg_and_background_avatars()
    {
        // v0.1.1: Facebook also serves profile pictures as SVG <image>
        // elements and background-image elements — all three capture paths
        // must be present, still semantic-only.
        Assert.Contains("currentSrc", ExtractionScript.Script);
        Assert.Contains("srcset", ExtractionScript.Script);
        Assert.Contains("querySelectorAll('image'", ExtractionScript.Script);
        Assert.Contains("getAttributeNS", ExtractionScript.Script);
        Assert.Contains("xlink:href", ExtractionScript.Script);
        Assert.Contains("getComputedStyle", ExtractionScript.Script);
        Assert.Contains("background-image", ExtractionScript.Script);
        Assert.DoesNotContain("className", ExtractionScript.Script);
        Assert.DoesNotContain("x1", ExtractionScript.Script);
    }
}

public class ReadStoreAndCacheTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "facetray-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Read_store_persists_and_marks()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "read.json");
            var store = new LocalReadStore(path);
            store.Load();
            Assert.True(store.MarkRead("abc"));
            Assert.False(store.MarkRead("abc")); // already marked
            store.MarkAllRead(new[] { "def", "ghi" });
            store.Save();

            var reloaded = new LocalReadStore(path);
            reloaded.Load();
            Assert.True(reloaded.IsRead("abc"));
            Assert.True(reloaded.IsRead("def"));
            Assert.False(reloaded.IsRead("zzz"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Read_store_clear_wipes()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "read.json");
            var store = new LocalReadStore(path);
            store.MarkRead("abc");
            store.Save();
            store.Clear();
            Assert.False(store.IsRead("abc"));
            Assert.False(File.Exists(path));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Cache_roundtrips_and_revalidates()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "cache.json");
            var item = NotificationItem.Create("A commented on your post.", "https://www.facebook.com/x?notif_id=1", "3h", true, null, null)!;
            NotificationCacheStore.Save(path, new[] { item });
            var loaded = NotificationCacheStore.Load(path, null);
            Assert.Single(loaded);
            Assert.Equal(item.Id, loaded[0].Id);
            Assert.True(loaded[0].IsUnread);

            var loadedWithRead = NotificationCacheStore.Load(path, new HashSet<string> { item.Id });
            Assert.False(loadedWithRead[0].IsUnread);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Cache_load_heals_v010_duplicated_time()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "cache.json");
            // A v0.1.0 cache entry: Text still ends with the time token.
            var stale = new NotificationItem
            {
                Id = "abc123",
                Text = "Dani Sue commented on your post. 2d",
                Href = "https://www.facebook.com/photo/?fbid=2&notif_id=33",
                RelativeTimeText = "2d",
                IsUnread = true,
            };
            NotificationCacheStore.Save(path, new[] { stale });
            var loaded = NotificationCacheStore.Load(path, null);
            var item = Assert.Single(loaded);
            Assert.Equal("Dani Sue commented on your post.", item.Text);
            Assert.Equal("2d", item.RelativeTimeText);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Cache_of_missing_file_is_empty()
    {
        Assert.Empty(NotificationCacheStore.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "cache.json"), null));
    }
}

public class GroupingTests
{
    private static NotificationItem Item(string text, string time, int n) =>
        NotificationItem.Create(text, $"https://www.facebook.com/x?notif_id={n}", time, false, null, null)!;

    [Theory]
    [InlineData("9h", true)]
    [InlineData("25m", true)]
    [InlineData("1 min", true)]
    [InlineData("Just now", true)]
    [InlineData("1d", false)]
    [InlineData("2d", false)]
    [InlineData("1w", false)]
    [InlineData("3mo", false)]
    [InlineData("Yesterday", false)]
    [InlineData("", false)]
    [InlineData("sometime", false)]
    public void IsToday_cases(string time, bool expected)
        => Assert.Equal(expected, NotificationGrouping.IsToday(time));

    [Fact]
    public void Groups_preserve_order_and_split()
    {
        var items = new[]
        {
            Item("A commented on your post.", "9h", 1),
            Item("B commented on your post.", "1d", 2),
            Item("C mentioned you in a comment.", "2h", 3),
        };
        var groups = NotificationGrouping.GroupForDisplay(items, DateTimeOffset.Now);
        Assert.Equal(2, groups.Count);
        Assert.Equal("Today", groups[0].Title);
        Assert.Equal(2, groups[0].Items.Count);
        Assert.Equal("Earlier", groups[1].Title);
        Assert.Single(groups[1].Items);
    }

    [Fact]
    public void Empty_items_give_no_groups()
        => Assert.Empty(NotificationGrouping.GroupForDisplay(Array.Empty<NotificationItem>(), DateTimeOffset.Now));

    [Fact]
    public void All_earlier_gives_single_group()
    {
        var groups = NotificationGrouping.GroupForDisplay(new[] { Item("A commented.", "4d", 9) }, DateTimeOffset.Now);
        Assert.Single(groups);
        Assert.Equal("Earlier", groups[0].Title);
    }
}
