using System.Text.Json;
using FaceTray.Core;
using Xunit;

namespace FaceTray.Core.Tests;

// v0.3.0 feature tests: pins, undo, power keys, exact timestamps,
// folding, alert rules, Focus Assist gate, re-alerts, digest, accent
// palette, extraction strategies, session classifier, renderer retry,
// staleness, backup/restore, portable mode.

public class PinStoreTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "facetray-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static readonly DateTimeOffset T0 = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Pin_store_round_trips_and_unpins()
    {
        var path = Path.Combine(TempDir(), "pins.json");
        var store = new PinStore(path);
        store.Pin("id-1", T0);
        store.Pin("id-2", T0.AddMinutes(5));
        store.Save();

        var reloaded = new PinStore(path);
        reloaded.Load();
        Assert.True(reloaded.IsPinned("id-1"));
        Assert.Equal(T0, reloaded.PinnedUtc("id-1"));
        Assert.Equal(2, reloaded.Ids.Count);

        Assert.True(reloaded.Unpin("id-1"));
        Assert.False(reloaded.Unpin("id-1"));
        Assert.False(reloaded.IsPinned("id-1"));
    }

    [Fact]
    public void Parser_applies_pin_state()
    {
        const string json = """
        {"signedOut":false,"items":[
          {"text":"A commented on your post.","href":"https://www.facebook.com/photo/?fbid=9&notif_id=511","unread":true,"avatars":[],"timeText":"1h","typeHint":"comment"},
          {"text":"B commented on your post.","href":"https://www.facebook.com/photo/?fbid=9&notif_id=512","unread":true,"avatars":[],"timeText":"1h","typeHint":"comment"}
        ]}
        """;
        var probe = ExtractionParser.Parse(json, null);
        var pinnedId = probe.Items[0].Id;

        var result = ExtractionParser.Parse(json, null, pinnedIds: new HashSet<string> { pinnedId });
        Assert.True(result.Items[0].IsPinned);
        Assert.False(result.Items[1].IsPinned);
    }

    [Fact]
    public void Pinned_cache_entries_are_exempt_from_the_age_prune()
    {
        var path = Path.Combine(TempDir(), "cache.json");
        var pinnedItem = NotificationItem.Create(
            "A commented on your post.", "https://www.facebook.com/photo/?fbid=9&notif_id=521",
            "1h", true, null, "comment")!;
        var plainItem = NotificationItem.Create(
            "B commented on your post.", "https://www.facebook.com/photo/?fbid=9&notif_id=522",
            "1h", true, null, "comment")!;
        var longAgo = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        NotificationCacheStore.SaveEntries(path, new[]
        {
            new CachedNotification(pinnedItem, longAgo),
            new CachedNotification(plainItem, longAgo),
        });

        var loaded = NotificationCacheStore.Load(
            path, null, keepDays: 30,
            nowUtc: new DateTimeOffset(2026, 10, 24, 0, 0, 0, TimeSpan.Zero),
            pinnedIds: new HashSet<string> { pinnedItem.Id });

        var only = Assert.Single(loaded);
        Assert.Equal(pinnedItem.Id, only.Id);
        Assert.True(only.IsPinned);
    }
}

public class ReadStoreUndoTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "facetray-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static readonly DateTimeOffset T0 = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MarkAllRead_returns_only_the_newly_marked_ids()
    {
        var store = new LocalReadStore(Path.Combine(TempDir(), "read.json"));
        var first = store.MarkAllRead(new[] { "a", "b" }, T0);
        Assert.Equal(new[] { "a", "b" }, first.OrderBy(x => x).ToArray());

        var second = store.MarkAllRead(new[] { "b", "c" }, T0);
        Assert.Equal(new[] { "c" }, second);
    }

    [Fact]
    public void Unmark_restores_exactly_those_ids_and_ignores_unknown_ones()
    {
        var path = Path.Combine(TempDir(), "read.json");
        var store = new LocalReadStore(path);
        store.MarkAllRead(new[] { "a", "b", "c" }, T0);

        Assert.Equal(1, store.Unmark(new[] { "a", "never-marked" }));
        Assert.False(store.IsRead("a"));
        Assert.True(store.IsRead("b"));
        Assert.True(store.IsRead("c"));

        store.Save();
        var reloaded = new LocalReadStore(path);
        reloaded.Load();
        Assert.False(reloaded.IsRead("a"));
        Assert.True(reloaded.IsRead("b"));
    }

    [Fact]
    public void Snapshot_and_ReplaceAll_round_trip_for_backup()
    {
        var store = new LocalReadStore(Path.Combine(TempDir(), "read.json"));
        store.MarkAllRead(new[] { "a", "b" }, T0);
        var snapshot = store.Snapshot();
        Assert.Equal(2, snapshot.Count);
        Assert.Equal(T0, snapshot["a"]);

        var other = new LocalReadStore(Path.Combine(TempDir(), "read2.json"));
        other.ReplaceAll(snapshot);
        Assert.True(other.IsRead("a"));
        Assert.True(other.IsRead("b"));
    }
}

public class TickerKeyMapTests
{
    [Theory]
    [InlineData("J", RowKeyAction.MoveDown)]
    [InlineData("k", RowKeyAction.MoveUp)]
    [InlineData("Enter", RowKeyAction.Open)]
    [InlineData("RETURN", RowKeyAction.Open)]
    [InlineData("M", RowKeyAction.MarkRead)]
    [InlineData("s", RowKeyAction.SnoozeOneHour)]
    [InlineData("P", RowKeyAction.TogglePin)]
    [InlineData("X", RowKeyAction.ToggleExpand)]
    [InlineData("Q", RowKeyAction.None)]
    [InlineData("Space", RowKeyAction.None)]
    [InlineData("", RowKeyAction.None)]
    public void Keys_map_to_row_actions(string key, RowKeyAction expected) =>
        Assert.Equal(expected, TickerKeyMap.Map(key));

    [Fact]
    public void Null_key_is_no_action() => Assert.Equal(RowKeyAction.None, TickerKeyMap.Map(null));
}

public class TimestampParserTests
{
    private static string Payload(string? tsField) => $$"""
        {"signedOut":false,"items":[
          {"text":"A commented on your post.","href":"https://www.facebook.com/photo/?fbid=9&notif_id=601","unread":true,"avatars":[],"timeText":"3h","typeHint":"comment"{{tsField}}}
        ]}
        """;

    [Fact]
    public void Iso_timestamp_is_captured_as_utc()
    {
        var result = ExtractionParser.Parse(Payload(",\"ts\":\"2026-10-21T16:32:00Z\""), null);
        var item = Assert.Single(result.Items);
        Assert.Equal(new DateTimeOffset(2026, 10, 21, 16, 32, 0, TimeSpan.Zero), item.TimestampUtc);
    }

    [Fact]
    public void Rfc_timestamp_from_an_abbr_title_is_captured()
    {
        // 21 Oct 2026 is a Wednesday; .NET's parser validates the weekday
        // in RFC strings, and Facebook's own titles are self-consistent.
        var result = ExtractionParser.Parse(Payload(",\"ts\":\"Wed, 21 Oct 2026 16:32:00 GMT\""), null);
        var item = Assert.Single(result.Items);
        Assert.Equal(new DateTimeOffset(2026, 10, 21, 16, 32, 0, TimeSpan.Zero), item.TimestampUtc);
    }

    [Fact]
    public void Malformed_timestamp_yields_null_and_keeps_the_row()
    {
        var result = ExtractionParser.Parse(Payload(",\"ts\":\"not a time at all\""), null);
        var item = Assert.Single(result.Items);
        Assert.Null(item.TimestampUtc);
    }

    [Fact]
    public void Missing_timestamp_is_null()
    {
        var result = ExtractionParser.Parse(Payload(null), null);
        var item = Assert.Single(result.Items);
        Assert.Null(item.TimestampUtc);
    }
}

public class NotificationFoldingTests
{
    private static NotificationItem Item(string text, string href, string hint = "comment") =>
        NotificationItem.Create(text, href, "1h", true, null, hint)!;

    [Fact]
    public void Same_path_different_query_folds_into_the_newest()
    {
        var a = Item("A commented on your post.", "https://www.facebook.com/photo/?fbid=9&notif_id=701");
        var b = Item("B commented on your post.", "https://www.facebook.com/photo/?fbid=9&shared=1&notif_id=702");
        var folds = NotificationFolding.Fold(new[] { a, b }, null);

        var fold = Assert.Single(folds);
        Assert.True(fold.IsGroup);
        Assert.Equal(2, fold.Count);
        Assert.Equal(a.Id, fold.Primary.Id);
        Assert.Equal(b.Id, Assert.Single(fold.Folded).Id);
    }

    [Fact]
    public void Different_paths_and_different_groups_do_not_fold()
    {
        var a = Item("A commented on your post.", "https://www.facebook.com/photo/?fbid=9&notif_id=711");
        var otherPath = Item("B commented on your post.", "https://www.facebook.com/posts/123?notif_id=712");
        var otherGroup = Item("C liked your post.", "https://www.facebook.com/photo/?fbid=9&notif_id=713", "like");

        var folds = NotificationFolding.Fold(new[] { a, otherPath, otherGroup }, null);
        Assert.Equal(3, folds.Count);
        Assert.All(folds, f => Assert.False(f.IsGroup));
    }

    [Fact]
    public void Pinned_items_never_fold_and_order_is_preserved()
    {
        var a = Item("A commented on your post.", "https://www.facebook.com/photo/?fbid=9&notif_id=721");
        var mid = Item("X commented on your post.", "https://www.facebook.com/posts/55?notif_id=722");
        var b = Item("B commented on your post.", "https://www.facebook.com/photo/?fbid=9&notif_id=723");

        var unpinned = NotificationFolding.Fold(new[] { a, mid, b }, null);
        Assert.Equal(2, unpinned.Count);
        Assert.Equal(2, unpinned[0].Count); // b folded into a across the interleave

        var pinned = NotificationFolding.Fold(new[] { a, mid, b }, new HashSet<string> { b.Id });
        Assert.Equal(3, pinned.Count);
        Assert.All(pinned, f => Assert.False(f.IsGroup));
    }
}

public class ToastDecisionTests
{
    private static readonly DateTimeOffset Midday = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static NotificationItem Comment() => NotificationItem.Create(
        "A commented on your post.", "https://www.facebook.com/photo/?fbid=9&notif_id=801",
        "1h", true, null, "comment")!;

    private static NotificationItem Like() => NotificationItem.Create(
        "A liked your post.", "https://www.facebook.com/photo/?fbid=9&notif_id=802",
        "1h", true, null, "like")!;

    [Fact]
    public void With_popups_on_every_type_toasts_by_default()
    {
        // ShowToasts itself defaults OFF in the product (no nagging);
        // these tests opt in, then exercise the rest of the decision.
        var settings = new AppSettings { ShowToasts = true };
        Assert.True(ToastDecision.ShouldToast(settings, Comment(), Midday));
        Assert.True(ToastDecision.ShouldToast(settings, Like(), Midday));
    }

    [Fact]
    public void Popups_off_means_no_toast()
    {
        var settings = new AppSettings { ShowToasts = false };
        Assert.False(ToastDecision.ShouldToast(settings, Comment(), Midday));
        Assert.False(ToastDecision.ShouldToast(new AppSettings(), Comment(), Midday)); // default is off
    }

    [Fact]
    public void Alert_type_groups_gate_per_group()
    {
        var settings = new AppSettings { ShowToasts = true };
        settings.AlertTypeGroups = new List<string> { "LikesAndReactions" };
        Assert.False(ToastDecision.ShouldToast(settings, Comment(), Midday));
        Assert.True(ToastDecision.ShouldToast(settings, Like(), Midday));

        settings.AlertTypeGroups = new List<string>();
        Assert.False(ToastDecision.ShouldToast(settings, Like(), Midday));
    }

    [Fact]
    public void Quiet_hours_suppress()
    {
        var settings = new AppSettings
        {
            ShowToasts = true,
            QuietHoursEnabled = true,
            QuietHoursStart = "22:00",
            QuietHoursEnd = "07:00",
        };
        var night = new DateTimeOffset(2026, 10, 24, 23, 30, 0, TimeSpan.Zero);
        Assert.False(ToastDecision.ShouldToast(settings, Comment(), night));
        Assert.True(ToastDecision.ShouldToast(settings, Comment(), Midday));
    }

    [Fact]
    public void Alert_groups_sanitize_to_canonical_keys_or_all_when_null()
    {
        var settings = new AppSettings();
        settings.AlertTypeGroups = new List<string> { "likesandreactions", "LikesAndReactions", "bogus" };
        Assert.Equal(new[] { "LikesAndReactions" }, settings.Sanitized().AlertTypeGroups);

        var nulled = new AppSettings();
        nulled.AlertTypeGroups = null!;
        Assert.Equal(4, nulled.Sanitized().AlertTypeGroups.Count);
    }
}

public class ShellNotificationGateTests
{
    [Fact]
    public void Only_accepts_notifications_allows()
    {
        Assert.True(ShellNotificationGate.AllowsNotifications(5));
        foreach (var raw in new[] { 1, 2, 3, 4, 6, 7 })
            Assert.False(ShellNotificationGate.AllowsNotifications(raw));
    }

    [Fact]
    public void Unknown_future_states_allow_and_are_named()
    {
        Assert.True(ShellNotificationGate.AllowsNotifications(99));
        Assert.Equal("AcceptsNotifications", ShellNotificationGate.Name(5));
        Assert.Equal("Unknown(99)", ShellNotificationGate.Name(99));
    }
}

public class AlertTrackAndRealertTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "facetray-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static readonly DateTimeOffset T0 = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Track_records_first_seen_once_and_round_trips()
    {
        var path = Path.Combine(TempDir(), "track.json");
        var store = new AlertTrackStore(path);
        Assert.True(store.Track("id-1", T0));
        Assert.False(store.Track("id-1", T0.AddHours(2))); // first sighting stands
        Assert.Equal(T0, store.FirstSeenUtc("id-1"));
        Assert.False(store.HasRealerted("id-1"));
        store.MarkRealerted("id-1");
        store.Save();

        var reloaded = new AlertTrackStore(path);
        reloaded.Load();
        Assert.True(reloaded.HasRealerted("id-1"));
        Assert.Equal(T0, reloaded.FirstSeenUtc("id-1"));
    }

    [Fact]
    public void PruneExcept_drops_ids_no_longer_in_history()
    {
        var store = new AlertTrackStore(Path.Combine(TempDir(), "track.json"));
        store.Track("keep", T0);
        store.Track("drop", T0);
        Assert.Equal(1, store.PruneExcept(new HashSet<string> { "keep" }));
        Assert.Null(store.FirstSeenUtc("drop"));
    }

    [Fact]
    public void Realert_is_due_only_for_an_old_unread_comment_or_mention_not_yet_realerted()
    {
        var comment = NotificationItem.Create(
            "A commented on your post.", "https://www.facebook.com/photo/?fbid=9&notif_id=901",
            "1h", true, null, "comment")!;
        var now = T0.AddHours(3);

        Assert.True(RealertPolicy.IsDue(comment, now.AddMinutes(-31), false, now));
        Assert.False(RealertPolicy.IsDue(comment, now.AddMinutes(-29), false, now));
        Assert.False(RealertPolicy.IsDue(comment, now.AddMinutes(-31), true, now));   // already realerted
        Assert.False(RealertPolicy.IsDue(comment, null, false, now));                  // never tracked

        comment.IsUnread = false;
        Assert.False(RealertPolicy.IsDue(comment, now.AddMinutes(-31), false, now));   // already read

        var like = NotificationItem.Create(
            "A liked your post.", "https://www.facebook.com/photo/?fbid=9&notif_id=902",
            "1h", true, null, "like")!;
        Assert.False(RealertPolicy.IsDue(like, now.AddMinutes(-31), false, now));      // wrong group
    }
}

public class DigestPolicyTests
{
    private static DateTimeOffset At(int hour) => new(2026, 10, 24, hour, 15, 0, TimeSpan.Zero);

    [Fact]
    public void Due_after_the_time_with_accumulated_items_and_not_yet_sent()
    {
        Assert.True(DigestPolicy.IsDue(true, "08:00", At(9), "", 3));
        Assert.True(DigestPolicy.IsDue(true, "08:00", At(9), "2026-10-23", 1));
    }

    [Fact]
    public void Not_due_cases()
    {
        Assert.False(DigestPolicy.IsDue(false, "08:00", At(9), "", 3));          // disabled
        Assert.False(DigestPolicy.IsDue(true, "08:00", At(7), "", 3));           // too early
        Assert.False(DigestPolicy.IsDue(true, "08:00", At(9), "2026-10-24", 3)); // already sent today
        Assert.False(DigestPolicy.IsDue(true, "08:00", At(9), "", 0));           // nothing accumulated
        Assert.False(DigestPolicy.IsDue(true, "08:00", At(9), "2026-10-25", 3)); // clock oddity guard
    }

    [Fact]
    public void Digest_settings_sanitize()
    {
        var settings = new AppSettings { DigestTime = "8am", LastDigestDate = "24/10/2026" };
        var clean = settings.Sanitized();
        Assert.Equal("08:00", clean.DigestTime);
        Assert.Equal("", clean.LastDigestDate);

        var ok = new AppSettings { DigestTime = "07:30", LastDigestDate = "2026-10-24" };
        var cleanOk = ok.Sanitized();
        Assert.Equal("07:30", cleanOk.DigestTime);
        Assert.Equal("2026-10-24", cleanOk.LastDigestDate);
    }
}

public class SessionClassifierTests
{
    [Theory]
    [InlineData("/notifications", "", SessionState.Active)]
    [InlineData("/some.page/posts/123", "", SessionState.Active)]
    [InlineData("/login", "", SessionState.SignedOut)]
    [InlineData("/login/device-based/regular/login/", "", SessionState.SignedOut)]
    [InlineData("/recover/initiate", "", SessionState.SignedOut)]
    [InlineData("/checkpoint/1501092823525282", "", SessionState.Checkpoint)]
    [InlineData("", "Log in to Facebook", SessionState.SignedOut)]
    [InlineData("", "Confirm it's you", SessionState.Checkpoint)]
    [InlineData("", "Facebook", SessionState.Unknown)]
    [InlineData("", "", SessionState.Unknown)]
    public void Classifies_paths_then_titles(string path, string title, SessionState expected) =>
        Assert.Equal(expected, SessionClassifier.Classify(path, title));
}

public class RendererRetryPolicyTests
{
    [Fact]
    public void Budget_is_three_recreations_with_a_ten_second_backoff()
    {
        Assert.Equal(3, RendererRetryPolicy.MaxRecreations);
        Assert.Equal(TimeSpan.FromSeconds(10), RendererRetryPolicy.Backoff);
        Assert.True(RendererRetryPolicy.ShouldRecreate(0));
        Assert.True(RendererRetryPolicy.ShouldRecreate(2));
        Assert.False(RendererRetryPolicy.ShouldRecreate(3));
        Assert.False(RendererRetryPolicy.ShouldRecreate(-1));
    }
}

public class StalenessTextTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Formats_fresh_and_stale_lines()
    {
        Assert.Equal("Not updated yet", StalenessText.Format(null, Now, true));
        Assert.Equal("Updated just now", StalenessText.Format(Now.AddSeconds(-20), Now, true));
        Assert.Equal("Updated 5 min ago", StalenessText.Format(Now.AddMinutes(-5), Now, true));
        Assert.Equal("Updated 2 h ago", StalenessText.Format(Now.AddHours(-2), Now, true));
        Assert.Equal("Last updated 26 min ago — showing saved notifications",
            StalenessText.Format(Now.AddMinutes(-26), Now, false));
        Assert.Equal("Last updated just now — showing saved notifications",
            StalenessText.Format(Now.AddSeconds(-10), Now, false));
    }
}

public class AccentPaletteTests
{
    [Fact]
    public void Palette_normalizes_case_and_rejects_anything_else()
    {
        Assert.True(AccentPalette.TryNormalize("#3e8fe1", out var canonical));
        Assert.Equal("#3E8FE1", canonical);
        Assert.False(AccentPalette.TryNormalize("chartreuse", out _));
        Assert.False(AccentPalette.TryNormalize(null, out _));
        Assert.Equal(AccentPalette.DefaultHex, AccentPalette.NormalizeOrDefault("#000000"));
        Assert.Equal("#E5484D", AccentPalette.DefaultHex);
    }

    [Fact]
    public void Profile_accents_sanitize_against_the_palette()
    {
        var settings = new AppSettings();
        settings.Profiles = new List<ProfileInfo>
        {
            new() { Id = "default", Name = "Personal", AccentColor = "#3e8fe1" },
            new() { Id = "work", Name = "Work", AccentColor = "chartreuse" },
        };
        var clean = settings.Sanitized();
        Assert.Equal("#3E8FE1", clean.Profiles.First(p => p.Id == "default").AccentColor);
        Assert.Equal(AccentPalette.DefaultHex, clean.Profiles.First(p => p.Id == "work").AccentColor);
    }
}

public class StrategyParserTests
{
    private static string Payload(int strategy) => $$"""
        {"signedOut":false,"strategy":{{strategy}},"items":[
          {"text":"A commented on your post.","href":"https://www.facebook.com/photo/?fbid=9&notif_id=951","unread":true,"avatars":[],"timeText":"1h","typeHint":"comment"}
        ]}
        """;

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(9, 1)] // out-of-range clamps to the primary rather than breaking
    public void Strategy_is_recorded_on_the_result(int payload, int expected) =>
        Assert.Equal(expected, ExtractionParser.Parse(Payload(payload), null).Strategy);

    [Fact]
    public void Script_carries_all_three_strategies_and_stays_semantic()
    {
        Assert.Contains("strategy", ExtractionScript.Script);
        Assert.Contains("[role=\"listitem\"]", ExtractionScript.Script);
        Assert.Contains("story", ExtractionScript.Script);
        Assert.DoesNotContain("className", ExtractionScript.Script);
        Assert.DoesNotContain("x1", ExtractionScript.Script);
    }

    [Fact]
    public void Diagnostics_report_names_the_strategy_and_session()
    {
        var state = new DiagnosticsState
        {
            AppVersion = "0.3.0",
            ActiveProfileId = "default",
            ActiveProfileName = "Personal",
            ProfileCount = 1,
            LastStrategy = 2,
            StrategyCounts = new Dictionary<int, int> { [1] = 5, [2] = 2 },
            SessionStateName = "Checkpoint",
        };
        var text = DiagnosticsReport.Build(state);
        Assert.Contains("strategy 2 (permalink fallback)", text);
        Assert.Contains("primary 5", text);
        Assert.Contains("Session: Checkpoint", text);
    }
}

public class BackupServiceTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "facetray-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static readonly DateTimeOffset T0 = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static AppSettings TwoProfileSettings()
    {
        var settings = new AppSettings();
        settings.Profiles = new List<ProfileInfo>
        {
            new() { Id = "default", Name = "Personal" },
            new() { Id = "work", Name = "Work" },
        };
        settings.ActiveProfileId = "default";
        return settings;
    }

    private static Func<string, ProfileFilePaths> PathsFor(string root) => id => new ProfileFilePaths(
        Path.Combine(root, $"read-{id}.json"),
        Path.Combine(root, $"snooze-{id}.json"),
        Path.Combine(root, $"pin-{id}.json"),
        Path.Combine(root, $"cache-{id}.json"));

    [Fact]
    public void Round_trip_restores_every_store_and_reports_counts()
    {
        var rootA = TempDir();
        var rootB = TempDir();
        var pathsA = PathsFor(rootA);
        var settings = TwoProfileSettings();

        // Seed profile "default" in root A; "work" stays empty.
        var p = pathsA("default");
        var read = new LocalReadStore(p.ReadStatePath);
        read.MarkAllRead(new[] { "id-1", "id-2" }, T0);
        read.Save();
        var snooze = new SnoozeStore(p.SnoozePath);
        snooze.Snooze("id-3", T0.AddHours(2));
        snooze.Save();
        var pins = new PinStore(p.PinPath);
        pins.Pin("id-4", T0);
        pins.Save();
        var item = NotificationItem.Create(
            "A commented on your post.", "https://www.facebook.com/photo/?fbid=9&notif_id=991",
            "1h", true, null, "comment")!;
        NotificationCacheStore.SaveEntries(p.CachePath, new[] { new CachedNotification(item, T0) });

        var json = BackupService.Build(settings, pathsA, T0);
        Assert.DoesNotContain("WebView2", json); // sessions are never part of a backup

        Assert.True(BackupService.TryParse(json, out var envelope));
        var report = BackupService.Restore(envelope!, PathsFor(rootB));
        Assert.Equal(2, report.ProfilesRestored);
        Assert.Equal(2, report.ReadMarks);
        Assert.Equal(1, report.Snoozes);
        Assert.Equal(1, report.Pins);
        Assert.Equal(1, report.CachedItems);

        var q = PathsFor(rootB)("default");
        var readB = new LocalReadStore(q.ReadStatePath);
        readB.Load();
        Assert.True(readB.IsRead("id-1"));
        Assert.True(readB.IsRead("id-2"));
        var snoozeB = new SnoozeStore(q.SnoozePath);
        snoozeB.Load();
        Assert.Contains("id-3", snoozeB.ActiveIds(T0));
        var pinsB = new PinStore(q.PinPath);
        pinsB.Load();
        Assert.True(pinsB.IsPinned("id-4"));
        var cacheB = NotificationCacheStore.LoadEntries(q.CachePath);
        Assert.Equal(item.Id, Assert.Single(cacheB).Item.Id);
        Assert.NotNull(envelope!.Settings);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{"format":"something-else","version":1,"exportedUtc":"2026-10-24T12:00:00Z","settings":{},"profiles":[]}""")]
    [InlineData("""{"format":"facetray-backup","version":2,"exportedUtc":"2026-10-24T12:00:00Z","settings":{},"profiles":[]}""")]
    [InlineData("""{"format":"facetray-backup","version":1,"exportedUtc":"2026-10-24T12:00:00Z","settings":{},"profiles":[{"id":"default","readMarks":[],"snoozes":{},"pins":{},"cache":[]},{"id":"default","readMarks":[],"snoozes":{},"pins":{},"cache":[]}]}""")]
    [InlineData("""{"format":"facetray-backup","version":1,"exportedUtc":"2026-10-24T12:00:00Z","settings":{},"profiles":[{"id":"bad id!","readMarks":[],"snoozes":{},"pins":{},"cache":[]}]}""")]
    [InlineData("""{"format":"facetray-backup","version":1,"exportedUtc":"2026-10-24T12:00:00Z","settings":{},"profiles":null}""")]
    public void Malformed_backups_are_rejected_without_throwing(string json)
    {
        Assert.False(BackupService.TryParse(json, out var envelope));
        Assert.Null(envelope);
    }
}

public class DataRootTests
{
    [Fact]
    public void Marker_present_resolves_the_portable_root_beside_the_app()
    {
        string? probed = null;
        var root = DataRoot.TryResolvePortableRoot("/app/base", path =>
        {
            probed = path;
            return true;
        });
        Assert.Equal(Path.Combine("/app/base", "portable.txt"), probed);
        Assert.Equal(Path.Combine("/app/base", "FaceTray-data"), root);
    }

    [Fact]
    public void No_marker_means_no_portable_root()
    {
        Assert.Null(DataRoot.TryResolvePortableRoot("/app/base", _ => false));
        Assert.Null(DataRoot.TryResolvePortableRoot("", _ => true));
    }
}

public class CacheSaveEntriesTests
{
    [Fact]
    public void SaveEntries_preserves_each_entrys_saved_time()
    {
        var dir = Path.Combine(Path.GetTempPath(), "facetray-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "cache.json");
        var t1 = new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2026, 10, 23, 9, 30, 0, TimeSpan.Zero);
        var a = NotificationItem.Create("A commented on your post.",
            "https://www.facebook.com/photo/?fbid=9&notif_id=961", "1h", true, null, "comment")!;
        var b = NotificationItem.Create("B commented on your post.",
            "https://www.facebook.com/photo/?fbid=9&notif_id=962", "2h", false, null, "comment")!;

        NotificationCacheStore.SaveEntries(path, new[]
        {
            new CachedNotification(a, t1),
            new CachedNotification(b, t2),
        });

        var entries = NotificationCacheStore.LoadEntries(path);
        Assert.Equal(2, entries.Count);
        Assert.Equal(t1, entries[0].SavedUtc);
        Assert.Equal(t2, entries[1].SavedUtc);
    }
}
