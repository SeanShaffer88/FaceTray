using FaceTray.Core;
using Xunit;

namespace FaceTray.Core.Tests;

public class ProfilesTests
{
    [Theory]
    [InlineData("default", true)]
    [InlineData("p1a2b3c4", true)]
    [InlineData("work-2", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("Work", false)]       // ids are lowercase (they become file names)
    [InlineData("-lead", false)]
    [InlineData("has space", false)]
    [InlineData("toolongtoolongtoolongtoolong", false)]
    public void Id_validity(string? id, bool expected)
        => Assert.Equal(expected, Profiles.IsValidId(id));

    [Fact]
    public void Sanitize_empty_gives_just_the_default_profile()
    {
        var profiles = Profiles.Sanitize(null, null, out var active);
        var only = Assert.Single(profiles);
        Assert.Equal(Profiles.DefaultId, only.Id);
        Assert.Equal(Profiles.DefaultName, only.Name);
        Assert.Equal(Profiles.DefaultId, active);
    }

    [Fact]
    public void Sanitize_keeps_valid_profiles_and_active()
    {
        var input = new[]
        {
            new ProfileInfo { Id = "pabc", Name = "Work" },
            new ProfileInfo { Id = Profiles.DefaultId, Name = "Personal" },
        };
        var profiles = Profiles.Sanitize(input, "pabc", out var active);
        Assert.Equal(2, profiles.Count);
        Assert.Equal(Profiles.DefaultId, profiles[0].Id); // default always first
        Assert.Equal("Personal", profiles[0].Name);
        Assert.Equal("pabc", active);
    }

    [Fact]
    public void Sanitize_repairs_duplicate_and_invalid_ids()
    {
        var input = new[]
        {
            new ProfileInfo { Id = "dup", Name = "One" },
            new ProfileInfo { Id = "dup", Name = "Two" },
            new ProfileInfo { Id = "NOT VALID!", Name = "Three" },
        };
        var profiles = Profiles.Sanitize(input, null, out _);
        Assert.Equal(4, profiles.Count); // + inserted default
        Assert.Equal(4, profiles.Select(p => p.Id).Distinct().Count());
        Assert.All(profiles, p => Assert.True(Profiles.IsValidId(p.Id)));
        Assert.All(profiles, p => Assert.False(string.IsNullOrWhiteSpace(p.Name)));
    }

    [Fact]
    public void Sanitize_unknown_active_falls_back_to_default()
    {
        var profiles = Profiles.Sanitize(
            new[] { new ProfileInfo { Id = "pabc", Name = "Work" } }, "missing", out var active);
        Assert.Equal(Profiles.DefaultId, active);
        Assert.Contains(profiles, p => p.Id == "pabc");
    }

    [Fact]
    public void Sanitize_caps_at_max_profiles()
    {
        var input = Enumerable.Range(0, 12)
            .Select(i => new ProfileInfo { Id = $"p{i}", Name = $"Account {i}" })
            .ToList();
        var profiles = Profiles.Sanitize(input, null, out _);
        Assert.Equal(Profiles.MaxProfiles, profiles.Count);
        Assert.Equal(Profiles.DefaultId, profiles[0].Id);
    }

    [Fact]
    public void Profile_paths_default_keeps_historic_names_others_are_suffixed()
    {
        var root = Path.Combine("some", "root");
        Assert.Equal(Path.Combine(root, "read-state.json"), AppPaths.ReadStatePathIn(root, null));
        Assert.Equal(Path.Combine(root, "read-state.json"), AppPaths.ReadStatePathIn(root, "default"));
        Assert.Equal(Path.Combine(root, "read-state-pabc.json"), AppPaths.ReadStatePathIn(root, "pabc"));
        Assert.Equal(Path.Combine(root, "notifications-cache.json"), AppPaths.CachePathIn(root, null));
        Assert.Equal(Path.Combine(root, "notifications-cache-pabc.json"), AppPaths.CachePathIn(root, "pabc"));
    }
}

public class SnoozeStoreTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "facetray-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Snooze_holds_until_the_instant_then_expires()
    {
        var store = new SnoozeStore(Path.Combine(TempDir(), "snooze.json"));
        store.Snooze("n1", Now.AddHours(1));
        Assert.True(store.IsSnoozed("n1", Now));
        Assert.True(store.IsSnoozed("n1", Now.AddMinutes(59)));
        Assert.False(store.IsSnoozed("n1", Now.AddHours(1)));
        Assert.False(store.IsSnoozed("n1", Now.AddHours(2)));
        Assert.False(store.IsSnoozed("other", Now));
    }

    [Fact]
    public void ActiveIds_and_prune()
    {
        var store = new SnoozeStore(Path.Combine(TempDir(), "snooze.json"));
        store.Snooze("short", Now.AddMinutes(10));
        store.Snooze("long", Now.AddDays(1));
        Assert.Equal(new HashSet<string> { "short", "long" }, store.ActiveIds(Now));
        Assert.Equal(new HashSet<string> { "long" }, store.ActiveIds(Now.AddHours(1)));
        Assert.Equal(1, store.PruneExpired(Now.AddHours(1)));
        Assert.Null(store.SnoozedUntil("short"));
        Assert.NotNull(store.SnoozedUntil("long"));
        Assert.Equal(0, store.PruneExpired(Now.AddHours(1)));
    }

    [Fact]
    public void Unsnooze_removes()
    {
        var store = new SnoozeStore(Path.Combine(TempDir(), "snooze.json"));
        store.Snooze("n1", Now.AddHours(1));
        Assert.True(store.Unsnooze("n1"));
        Assert.False(store.Unsnooze("n1"));
        Assert.False(store.IsSnoozed("n1", Now));
    }

    [Fact]
    public void Roundtrips_through_disk()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "snooze.json");
            var store = new SnoozeStore(path);
            store.Snooze("n1", Now.AddHours(3));
            store.Save();
            var reloaded = new SnoozeStore(path);
            reloaded.Load();
            Assert.True(reloaded.IsSnoozed("n1", Now));
            Assert.Equal(Now.AddHours(3), reloaded.SnoozedUntil("n1"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Tomorrow_morning_is_8am_next_local_day()
    {
        var evening = new DateTimeOffset(2026, 10, 24, 21, 30, 0, TimeSpan.FromHours(-5));
        Assert.Equal(
            new DateTimeOffset(2026, 10, 25, 8, 0, 0, TimeSpan.FromHours(-5)),
            SnoozeTiming.UntilTomorrowMorning(evening));
        var smallHours = new DateTimeOffset(2026, 10, 24, 2, 0, 0, TimeSpan.FromHours(-5));
        Assert.Equal(
            new DateTimeOffset(2026, 10, 25, 8, 0, 0, TimeSpan.FromHours(-5)),
            SnoozeTiming.UntilTomorrowMorning(smallHours));
        Assert.Equal(Now.AddHours(1), SnoozeTiming.OneHourFrom(Now));
    }
}

public class ReadStoreTimestampTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "facetray-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Timestamped_entries_prune_by_age()
    {
        var store = new LocalReadStore(Path.Combine(TempDir(), "read.json"));
        store.MarkRead("old", Now.AddDays(-40));
        store.MarkRead("fresh", Now);
        Assert.Equal(1, store.PruneOlderThan(Now.AddDays(-30)));
        Assert.False(store.IsRead("old"));
        Assert.True(store.IsRead("fresh"));
    }

    [Fact]
    public void Legacy_string_entries_have_no_timestamp_and_are_never_pruned()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "read.json");
            File.WriteAllText(path, """["legacy1","legacy2"]""");
            var store = new LocalReadStore(path);
            store.Load();
            Assert.True(store.IsRead("legacy1"));
            Assert.Equal(0, store.PruneOlderThan(Now.AddYears(100)));
            Assert.True(store.IsRead("legacy1"));

            // …and stay timestamp-less across a save/load cycle.
            store.Save();
            var reloaded = new LocalReadStore(path);
            reloaded.Load();
            Assert.True(reloaded.IsRead("legacy2"));
            Assert.Equal(0, reloaded.PruneOlderThan(Now.AddYears(100)));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void New_entries_roundtrip_with_timestamps()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "read.json");
            var store = new LocalReadStore(path);
            store.MarkRead("n1", Now);
            store.Save();
            Assert.Contains("markedUtc", File.ReadAllText(path));

            var reloaded = new LocalReadStore(path);
            reloaded.Load();
            Assert.True(reloaded.IsRead("n1"));
            Assert.Equal(1, reloaded.PruneOlderThan(Now.AddDays(1)));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}

public class CacheV2Tests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "facetray-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static NotificationItem Item(string text, int n, bool unread = true) =>
        NotificationItem.Create(text, $"https://www.facebook.com/x?notif_id={n}", "1h", unread, null, null)!;

    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Save_writes_v2_wrapper_with_saved_times()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "cache.json");
            NotificationCacheStore.Save(path, new[] { Item("A commented on your post.", 1) }, Now);
            var text = File.ReadAllText(path);
            Assert.Contains("\"savedUtc\"", text);
            Assert.Contains("\"version\": 2", text);

            var entries = NotificationCacheStore.LoadEntries(path);
            var entry = Assert.Single(entries);
            Assert.Equal(Now, entry.SavedUtc);
            Assert.Equal("A commented on your post.", entry.Item.Text);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Legacy_array_cache_loads_with_file_write_time()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "cache.json");
            var items = new[] { Item("A commented on your post.", 1), Item("B commented on your post.", 2) };
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(items));
            File.SetLastWriteTimeUtc(path, Now.UtcDateTime.AddDays(-40));

            // 40-day-old file, 30-day keep window → pruned away…
            Assert.Empty(NotificationCacheStore.Load(path, null, null, keepDays: 30, nowUtc: Now));
            // …but kept with no pruning, stamped with the file's write time.
            var entries = NotificationCacheStore.LoadEntries(path);
            Assert.Equal(2, entries.Count);
            Assert.Equal(new DateTimeOffset(Now.UtcDateTime.AddDays(-40), TimeSpan.Zero), entries[0].SavedUtc);
            Assert.Equal(2, NotificationCacheStore.Load(path, null).Count);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void V2_entries_prune_individually_by_saved_time()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "cache.json");
            const string json = """
            {"version":2,"items":[
              {"item":{"id":"old1","text":"Old commented on your post.","actorText":"Old","href":"https://www.facebook.com/x?notif_id=91","relativeTimeText":"9d","isUnread":true,"type":1,"avatarUrls":[]},"savedUtc":"2026-09-01T12:00:00+00:00"},
              {"item":{"id":"new1","text":"New commented on your post.","actorText":"New","href":"https://www.facebook.com/x?notif_id=92","relativeTimeText":"1h","isUnread":true,"type":1,"avatarUrls":[]},"savedUtc":"2026-10-24T11:00:00+00:00"}
            ]}
            """;
            File.WriteAllText(path, json);
            var loaded = NotificationCacheStore.Load(path, null, null, keepDays: 30, nowUtc: Now);
            var item = Assert.Single(loaded);
            Assert.Equal("new1", item.Id);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Load_applies_snooze_like_read_state()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "cache.json");
            var item = Item("A commented on your post.", 1);
            NotificationCacheStore.Save(path, new[] { item }, Now);
            var loaded = NotificationCacheStore.Load(path, null, new HashSet<string> { item.Id });
            Assert.False(Assert.Single(loaded).IsUnread);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Merge_puts_fresh_first_dedupes_and_caps()
    {
        var existing = new[] { Item("A commented.", 1), Item("B commented.", 2), Item("C commented.", 3) };
        var fresh = new[] { Item("D commented.", 4), Item("A commented again.", 1) };
        var merged = NotificationCacheStore.Merge(existing, fresh);
        Assert.Equal(new[] { "D commented.", "A commented again.", "B commented.", "C commented." },
            merged.Select(i => i.Text).ToArray());

        var manyExisting = Enumerable.Range(0, 80).Select(i => Item($"E{i} commented.", 1000 + i)).ToArray();
        var manyFresh = Enumerable.Range(0, 40).Select(i => Item($"F{i} commented.", 2000 + i)).ToArray();
        Assert.Equal(NotificationCacheStore.MaxCachedItems,
            NotificationCacheStore.Merge(manyExisting, manyFresh).Count);
    }
}
