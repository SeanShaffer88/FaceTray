using FaceTray.Core;
using Xunit;

namespace FaceTray.Core.Tests;

public class QuietHoursTests
{
    private static DateTimeOffset At(int hour, int minute = 0) =>
        new(2026, 10, 24, hour, minute, 0, TimeSpan.Zero);

    private static readonly TimeSpan Start = new(22, 0, 0);
    private static readonly TimeSpan End = new(7, 0, 0);

    [Theory]
    [InlineData(22, 0, true)]   // window opens
    [InlineData(23, 30, true)]
    [InlineData(0, 0, true)]    // wrapped past midnight
    [InlineData(6, 59, true)]
    [InlineData(7, 0, false)]   // window closes
    [InlineData(12, 0, false)]
    [InlineData(21, 59, false)]
    public void Wrap_midnight_window(int hour, int minute, bool expected)
        => Assert.Equal(expected, QuietHours.IsQuiet(At(hour, minute), Start, End));

    [Theory]
    [InlineData(12, 0, true)]
    [InlineData(13, 59, true)]
    [InlineData(14, 0, false)]
    [InlineData(11, 59, false)]
    public void Same_day_window(int hour, int minute, bool expected)
        => Assert.Equal(expected, QuietHours.IsQuiet(At(hour, minute), new TimeSpan(12, 0, 0), new TimeSpan(14, 0, 0)));

    [Fact]
    public void Equal_start_and_end_is_never_quiet()
    {
        Assert.False(QuietHours.IsQuiet(At(3), new TimeSpan(3, 0, 0), new TimeSpan(3, 0, 0)));
        Assert.False(QuietHours.IsQuiet(At(15), new TimeSpan(3, 0, 0), new TimeSpan(3, 0, 0)));
    }

    [Fact]
    public void Settings_gate_applies()
    {
        var settings = new AppSettings { QuietHoursEnabled = false };
        Assert.False(QuietHours.IsQuiet(At(23), settings));
        settings.QuietHoursEnabled = true;
        Assert.True(QuietHours.IsQuiet(At(23), settings));
        Assert.False(QuietHours.IsQuiet(At(12), settings));
    }
}

public class PollPolicyTests
{
    private static readonly TimeSpan Minute = TimeSpan.FromSeconds(60);

    [Theory]
    // unread waiting → halve, floored at 30s
    [InlineData(60, 2, 0, 30)]
    [InlineData(300, 1, 0, 150)]
    [InlineData(30, 3, 0, 30)]
    // nothing new for 3+ polls → double, capped at 5 min
    [InlineData(60, 0, 3, 120)]
    [InlineData(120, 0, 5, 240)]
    [InlineData(300, 0, 3, 300)]
    // otherwise the configured base
    [InlineData(60, 0, 0, 60)]
    [InlineData(60, 0, 2, 60)]
    [InlineData(120, 0, 1, 120)]
    public void Matrix(int baseSeconds, int unread, int emptyPolls, int expectedSeconds)
        => Assert.Equal(
            TimeSpan.FromSeconds(expectedSeconds),
            PollPolicy.NextInterval(TimeSpan.FromSeconds(baseSeconds), unread, emptyPolls));

    [Fact]
    public void Unread_wins_over_empty_streak()
        => Assert.Equal(TimeSpan.FromSeconds(30),
            PollPolicy.NextInterval(Minute, unreadCount: 1, consecutiveEmptyPolls: 9));
}

public class UpdateInfoTests
{
    [Fact]
    public void Parses_latest_release_json()
    {
        const string json = """
        {"tag_name":"v0.2.0","html_url":"https://github.com/SeanShaffer88/FaceTray/releases/tag/v0.2.0","prerelease":false,"draft":false}
        """;
        Assert.True(UpdateInfo.TryParse(json, out var info));
        Assert.Equal("v0.2.0", info!.Tag);
        Assert.Equal("0.2.0", info.Version);
        Assert.Contains("github.com", info.Url);
        Assert.True(info.IsNewerThan("0.1.1"));
        Assert.False(info.IsNewerThan("0.2.0"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"tag_name":"v9.9.9","draft":true}""")]
    public void Unparseable_or_draft_is_no_update(string? json)
        => Assert.False(UpdateInfo.TryParse(json, out _));

    [Theory]
    [InlineData("v0.2.0", "0.1.1", true)]
    [InlineData("0.2.0", "0.2.0", false)]
    [InlineData("v0.1.1", "0.2.0", false)]
    [InlineData("v0.10.0", "0.9.9", true)]     // numeric, not lexical
    [InlineData("v1", "0.9.9", true)]
    [InlineData("v0.2", "0.2.0", false)]       // equal core
    [InlineData("v0.2.0-beta", "0.2.0", false)]  // suffix never beats an equal core
    [InlineData("v0.2.0-beta", "0.1.9", true)]    // …but a greater core still counts
    [InlineData("garbage", "0.1.0", false)]
    [InlineData("v0.2.0", "garbage", false)]
    public void IsNewer_matrix(string latest, string current, bool expected)
        => Assert.Equal(expected, UpdateInfo.IsNewer(latest, current));
}

public class NotificationVisibilityTests
{
    private static NotificationItem Item(string text, int n, bool unread = true, string hint = "") =>
        NotificationItem.Create(text, $"https://www.facebook.com/x?notif_id={n}", "1h", unread, null, hint)!;

    private static HashSet<string> Hidden(params NotificationTypeGroup[] groups) =>
        groups.Select(NotificationTypeGroups.Key).ToHashSet();

    [Fact]
    public void Search_matches_actor_and_text_case_insensitively()
    {
        var items = new[]
        {
            Item("Travis Iiams commented on your post.", 1),
            Item("Ashley Marquez mentioned you in a comment.", 2),
        };
        Assert.Single(NotificationVisibility.FilterForDisplay(items, "travis", null, false));
        Assert.Single(NotificationVisibility.FilterForDisplay(items, "MENTIONED", null, false));
        Assert.Equal(2, NotificationVisibility.FilterForDisplay(items, "", null, false).Count);
        Assert.Equal(2, NotificationVisibility.FilterForDisplay(items, null, null, false).Count);
        Assert.Empty(NotificationVisibility.FilterForDisplay(items, "nobody", null, false));
    }

    [Fact]
    public void Hidden_types_leave_the_list()
    {
        var items = new[]
        {
            Item("A commented on your post.", 1),
            Item("B likes your post.", 2),
            Item("It's C's birthday today.", 3),
        };
        var visible = NotificationVisibility.FilterForDisplay(
            items, null, Hidden(NotificationTypeGroup.LikesAndReactions, NotificationTypeGroup.Birthdays), false);
        Assert.Single(visible);
        Assert.Equal(NotificationType.Comment, visible[0].Type);
    }

    [Fact]
    public void Type_group_mapping()
    {
        Assert.Equal(NotificationTypeGroup.CommentsAndMentions, NotificationTypeGroups.Of(NotificationType.Comment));
        Assert.Equal(NotificationTypeGroup.CommentsAndMentions, NotificationTypeGroups.Of(NotificationType.Mention));
        Assert.Equal(NotificationTypeGroup.LikesAndReactions, NotificationTypeGroups.Of(NotificationType.Like));
        Assert.Equal(NotificationTypeGroup.Birthdays, NotificationTypeGroups.Of(NotificationType.Birthday));
        Assert.Equal(NotificationTypeGroup.Other, NotificationTypeGroups.Of(NotificationType.Other));
    }

    [Fact]
    public void Unread_pill_composes_with_search_and_filters()
    {
        var items = new[]
        {
            Item("Travis Iiams commented on your post.", 1, unread: true),
            Item("Travis Iiams commented on your photo.", 2, unread: false),
        };
        var visible = NotificationVisibility.FilterForDisplay(items, "Travis", null, unreadOnly: true);
        Assert.Single(visible);
        Assert.True(visible[0].IsUnread);
    }

    [Fact]
    public void Counter_lifts_with_title_only_when_nothing_is_hidden()
    {
        var items = new[]
        {
            Item("A commented on your post.", 1, unread: true),
            Item("B commented on your post.", 2, unread: false),
        };
        // No hidden types: title count (rows below the fold) wins, as in v0.1.
        Assert.Equal(4, NotificationVisibility.CountUnread(items, null, titleCount: 4));
        // A type hidden (even an unrelated one): only visible unread count.
        Assert.Equal(1, NotificationVisibility.CountUnread(
            items, Hidden(NotificationTypeGroup.Birthdays), titleCount: 4));
    }

    [Fact]
    public void Counter_excludes_hidden_unread()
    {
        var items = new[]
        {
            Item("A commented on your post.", 1, unread: true),
            Item("B likes your post.", 2, unread: true),
        };
        Assert.Equal(1, NotificationVisibility.CountUnread(
            items, Hidden(NotificationTypeGroup.LikesAndReactions), titleCount: null));
    }

    [Theory]
    [InlineData("short", 10, "short")]
    [InlineData("exactly10!", 10, "exactly10!")]
    [InlineData("a much longer piece of text", 10, "a much lo…")]
    [InlineData(null, 10, "")]
    public void Truncate_cases(string? text, int max, string expected)
        => Assert.Equal(expected, DisplayText.Truncate(text, max));
}

public class ExtractionHealthTests
{
    [Theory]
    [InlineData(0, 0, 3, ExtractionHealthState.Broken)]    // title claims unread, no rows at all
    [InlineData(0, 0, 1, ExtractionHealthState.Broken)]
    [InlineData(5, 1, 4, ExtractionHealthState.Suspect)]   // unread mismatch ≥ 2
    [InlineData(5, 4, 1, ExtractionHealthState.Suspect)]
    [InlineData(5, 3, 4, ExtractionHealthState.Healthy)]   // off by one is normal (fold, read state)
    [InlineData(5, 0, 1, ExtractionHealthState.Healthy)]
    [InlineData(0, 0, 0, ExtractionHealthState.Healthy)]
    [InlineData(0, 0, null, ExtractionHealthState.Healthy)] // no title signal → no evidence
    [InlineData(3, 3, 3, ExtractionHealthState.Healthy)]
    public void Matrix(int rows, int extractedUnread, int? title, ExtractionHealthState expected)
        => Assert.Equal(expected, ExtractionHealth.Evaluate(rows, extractedUnread, title));
}

public class ParserSnoozeTests
{
    private const string Json = """
    {"signedOut":false,"items":[
      {"text":"A commented on your post.","href":"https://www.facebook.com/x?notif_id=1","unread":true,"avatars":[],"timeText":"1h","typeHint":""},
      {"text":"B commented on your post.","href":"https://www.facebook.com/x?notif_id=2","unread":true,"avatars":[],"timeText":"2h","typeHint":""}
    ]}
    """;

    [Fact]
    public void Snoozed_items_render_read_but_raw_count_keeps_page_truth()
    {
        var plain = ExtractionParser.Parse(Json, null);
        var snoozed = ExtractionParser.Parse(Json, null, null, new HashSet<string> { plain.Items[0].Id });
        Assert.Equal(2, snoozed.RawUnreadCount);
        Assert.False(snoozed.Items[0].IsUnread);
        Assert.True(snoozed.Items[1].IsUnread);
        Assert.Equal(1, snoozed.Items.Count(i => i.IsUnread));
    }
}

public class DiagnosticsReportTests
{
    [Fact]
    public void Report_carries_versions_and_counts_only()
    {
        var state = new DiagnosticsState
        {
            AppVersion = "0.2.0",
            ActiveProfileId = "default",
            ActiveProfileName = "Personal",
            ProfileCount = 2,
            LastPollUtc = new DateTimeOffset(2026, 10, 24, 17, 0, 0, TimeSpan.Zero),
            LastPollRows = 12,
            LastPollUnread = 3,
            ExtractionHealth = ExtractionHealthState.Suspect,
            PollIntervalSeconds = 60,
            CachedItemCount = 42,
            UnreadCount = 3,
            SessionFolder = @"C:\Users\SEAN\AppData\Local\FaceTray\WebView2",
        };
        var report = DiagnosticsReport.Build(state);
        Assert.Contains("Version: 0.2.0", report);
        Assert.Contains("Profile: Personal (default)", report);
        Assert.Contains("Profiles: 2", report);
        Assert.Contains("12 rows, 3 unread", report);
        Assert.Contains("Extraction health: Suspect", report);
        Assert.Contains("Cached notifications: 42", report);
        // Nothing resembling page content can appear: the state has no
        // field that could carry it.
        Assert.DoesNotContain("commented on your post", report);
        Assert.DoesNotContain("notif_id", report);
    }

    [Fact]
    public void Report_handles_never_polled()
    {
        var report = DiagnosticsReport.Build(new DiagnosticsState { AppVersion = "0.2.0" });
        Assert.Contains("Last poll: never", report);
    }
}

public class SettingsExchangeTests
{
    [Fact]
    public void Export_import_roundtrips()
    {
        var original = new AppSettings
        {
            PollIntervalSeconds = 120,
            ShowToasts = true,
            HiddenTypeGroups = new List<string> { "Birthdays" },
            HistoryKeepDays = 90,
            TickerTextScalePercent = 150,
            Profiles = new List<ProfileInfo>
            {
                new() { Id = "default", Name = "Personal" },
                new() { Id = "pwork", Name = "Work" },
            },
            ActiveProfileId = "pwork",
        };
        var json = SettingsStore.Export(original);
        Assert.True(SettingsStore.TryImport(json, out var imported));
        Assert.Equal(120, imported.PollIntervalSeconds);
        Assert.True(imported.ShowToasts);
        Assert.Equal(new[] { "Birthdays" }, imported.HiddenTypeGroups.ToArray());
        Assert.Equal(90, imported.HistoryKeepDays);
        Assert.Equal(150, imported.TickerTextScalePercent);
        Assert.Equal("pwork", imported.ActiveProfileId);
        Assert.Equal("Work", imported.ActiveProfile.Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("[1,2,3]")]
    [InlineData("42")]
    public void Import_rejects_garbage_without_throwing(string? json)
    {
        Assert.False(SettingsStore.TryImport(json, out var settings));
        Assert.Equal(60, settings.PollIntervalSeconds); // usable defaults
    }

    [Fact]
    public void Import_sanitizes_untrusted_values()
    {
        const string json = """
        {"pollIntervalSeconds": 9999, "hiddenTypeGroups": ["Birthdays", "Bogus", "birthdays"],
         "historyKeepDays": 45, "tickerTheme": "Neon", "tickerTextScalePercent": 137,
         "quietHoursStart": "25:00:00", "profiles": [], "activeProfileId": "ghost"}
        """;
        Assert.True(SettingsStore.TryImport(json, out var settings));
        Assert.Equal(300, settings.PollIntervalSeconds);
        Assert.Equal(new[] { "Birthdays" }, settings.HiddenTypeGroups.ToArray());
        Assert.Equal(30, settings.HistoryKeepDays);
        Assert.Equal(TickerThemes.FacebookDark, settings.TickerTheme);
        Assert.Equal(100, settings.TickerTextScalePercent);
        Assert.Equal("01:00", settings.QuietHoursStart); // 25:00 wraps to 01:00
        Assert.Equal(new TimeSpan(1, 0, 0), settings.QuietHoursStartTime);
        Assert.Equal(Profiles.DefaultId, settings.ActiveProfileId);
        Assert.Single(settings.Profiles);
    }

    [Fact]
    public void New_defaults_are_as_planned()
    {
        var settings = new AppSettings().Sanitized();
        Assert.Empty(settings.HiddenTypeGroups);
        Assert.False(settings.QuietHoursEnabled);
        Assert.Equal("22:00", settings.QuietHoursStart);
        Assert.Equal("07:00", settings.QuietHoursEnd);
        Assert.Equal(new TimeSpan(22, 0, 0), settings.QuietHoursStartTime);
        Assert.Equal(new TimeSpan(7, 0, 0), settings.QuietHoursEndTime);
        Assert.False(settings.PlaySoundOnToast);
        Assert.True(settings.GlobalHotkeyEnabled);
        Assert.True(settings.CheckForUpdates);
        Assert.Equal(30, settings.HistoryKeepDays);
        Assert.Equal(TickerThemes.FacebookDark, settings.TickerTheme);
        Assert.Equal(100, settings.TickerTextScalePercent);
        Assert.Equal(Profiles.DefaultId, settings.ActiveProfileId);
    }
}
