using System.Windows;
using System.Windows.Threading;
using FaceTray.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace FaceTray;

public enum PollerState
{
    Starting,
    Loading,
    Ready,
    NotSignedIn,
    Error,
}

/// <summary>Counts-only snapshot of the most recent poll, for Settings → Diagnostics.</summary>
public sealed class PollStats
{
    public DateTimeOffset AtUtc { get; init; }
    public int Rows { get; init; }
    public int Unread { get; init; }
    public ExtractionHealthState Health { get; init; }
}

/// <summary>
/// Owns the hidden WebView2 that reads facebook.com/notifications.
///
/// HOSTING DECISION (documented per the v0.1 brief): the WebView2 lives in
/// a dedicated 1×1 host window kept shown but parked far off-screen
/// (-32000,-32000), ShowInTaskbar=false. A WebView2 in a never-shown or
/// hidden window can have its renderer throttled/suspended by Windows,
/// which would silently stall polling; the off-screen-but-shown window
/// renders normally while staying invisible to the user. The ticker
/// window stays purely presentational and can hide/show freely.
///
/// v0.2.0: the service is constructed for ONE account profile (its cache
/// file, read store, snooze store and WebView2 session folder); polls
/// MERGE into the cached history instead of replacing it, the interval
/// adapts (see <see cref="PollPolicy"/>), and every poll is scored by
/// <see cref="ExtractionHealth"/>.
/// </summary>
public sealed class PollerService : IDisposable
{
    private AppSettings _settings;
    private readonly DiagnosticLog _log;
    private readonly LocalReadStore _readStore;
    private readonly SnoozeStore _snoozeStore;
    private readonly string _cachePath;
    private readonly string _webViewDataDir;
    private readonly Dispatcher _dispatcher;

    private Window? _hostWindow;
    private WebView2? _web;
    private DispatcherTimer? _timer;
    private TaskCompletionSource<bool>? _navTcs;
    private bool _pollInFlight;
    private bool _disposed;

    private int _consecutiveEmptyPolls;
    private int _consecutiveUnhealthyPolls;
    private TimeSpan _currentInterval;

    public PollerService(
        AppSettings settings,
        DiagnosticLog log,
        LocalReadStore readStore,
        SnoozeStore snoozeStore,
        string cachePath,
        string webViewDataDir,
        Dispatcher dispatcher)
    {
        _settings = settings;
        _log = log;
        _readStore = readStore;
        _snoozeStore = snoozeStore;
        _cachePath = cachePath;
        _webViewDataDir = webViewDataDir;
        _dispatcher = dispatcher;
        _currentInterval = TimeSpan.FromSeconds(settings.PollIntervalSeconds);

        var now = DateTimeOffset.UtcNow;
        Items = NotificationCacheStore.Load(
            cachePath, readStore.Ids, snoozeStore.ActiveIds(now),
            settings.HistoryKeepDays, now);
        UnreadCount = ComputeUnread(Items);
    }

    public PollerState State { get; private set; } = PollerState.Starting;
    public string? LastError { get; private set; }
    public IReadOnlyList<NotificationItem> Items { get; private set; }
    public int UnreadCount { get; private set; }
    public bool IsPaused { get; private set; }
    public bool WebReady { get; private set; }

    /// <summary>Stats of the most recent completed poll, or null before the first one.</summary>
    public PollStats? LastPoll { get; private set; }

    /// <summary>
    /// True after two consecutive Suspect/Broken extraction-health results;
    /// clears on the next Healthy poll. The ticker shows its warning
    /// banner while this is set.
    /// </summary>
    public bool HealthWarning { get; private set; }

    public AppSettings Settings => _settings;
    public string CachePath => _cachePath;
    public string WebViewDataDir => _webViewDataDir;

    /// <summary>Raised on the UI thread whenever state, items or count change.</summary>
    public event Action? Changed;

    /// <summary>
    /// Raised on the UI thread with the genuinely new unread items a poll
    /// found (ids never seen before), newest first. Drives the toast.
    /// </summary>
    public event Action<IReadOnlyList<NotificationItem>>? NewItemsArrived;

    public async Task StartAsync()
    {
        try
        {
            EnsureHost();
            var env = await WebViewEnv.GetAsync(_webViewDataDir);
            await _web!.EnsureCoreWebView2Async(env);
            Wire(_web.CoreWebView2);
            WebReady = true;
            _log.Info($"WebView2 ready; profile {_webViewDataDir}");
            RestartTimer();
            await PollNowAsync();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            SetError("The WebView2 Runtime is missing. FaceTray needs Microsoft's WebView2 Runtime (the same one MuseDesktop uses) — install it from Microsoft, then restart FaceTray.");
            _log.Error("WebView2 Runtime not found");
        }
        catch (Exception ex)
        {
            SetError("FaceTray couldn't start its Facebook view. Check your connection, then Refresh.");
            _log.Error($"WebView2 init failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void EnsureHost()
    {
        if (_hostWindow is not null)
            return;
        _web = new WebView2();
        _hostWindow = new Window
        {
            Width = 1,
            Height = 1,
            Left = -32000,
            Top = -32000,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            ResizeMode = ResizeMode.NoResize,
            Content = _web,
            Title = "FaceTray poller",
        };
        _hostWindow.Show();
    }

    private void Wire(CoreWebView2 core)
    {
        core.NavigationCompleted += (_, e) => _navTcs?.TrySetResult(e.IsSuccess);
        core.ProcessFailed += (_, e) =>
        {
            _log.Error($"WebView2 process failed: {e.ProcessFailedKind}");
            if (_disposed) return;
            SetError("Facebook's view stopped unexpectedly. Refresh to try again.");
        };
    }

    private void RestartTimer()
    {
        _timer?.Stop();
        _timer = new DispatcherTimer { Interval = _currentInterval };
        _timer.Tick += async (_, _) => await PollNowAsync();
        if (!IsPaused)
            _timer.Start();
    }

    /// <summary>Applies new settings (a saved clone replaces the old object) without a restart.</summary>
    public void ApplySettings(AppSettings settings)
    {
        _settings = settings;
        _currentInterval = PollPolicy.NextInterval(
            TimeSpan.FromSeconds(settings.PollIntervalSeconds), UnreadCount, _consecutiveEmptyPolls);
        RestartTimer();
        // Hidden-type changes move the counter immediately.
        UnreadCount = ComputeUnread(Items);
        RaiseChanged();
    }

    public void SetPaused(bool paused)
    {
        IsPaused = paused;
        if (paused) _timer?.Stop();
        else { RestartTimer(); _ = PollNowAsync(); }
        RaiseChanged();
    }

    public async Task RefreshNowAsync() => await PollNowAsync();

    public async Task PollNowAsync()
    {
        if (_disposed || _pollInFlight || _web?.CoreWebView2 is null)
            return;
        _pollInFlight = true;
        State = PollerState.Loading;
        RaiseChanged();
        try
        {
            var core = _web.CoreWebView2;
            _navTcs = new TaskCompletionSource<bool>();
            core.Navigate(UrlGuard.NotificationsUrl);

            var completed = await Task.WhenAny(_navTcs.Task, Task.Delay(TimeSpan.FromSeconds(30)));
            var ok = completed == _navTcs.Task && await _navTcs.Task;
            if (!ok)
            {
                // A failed main navigation while we still have last-good
                // items degrades to "keep showing them" with an error note.
                SetError("Couldn't load Facebook notifications. You may be offline — showing the last list FaceTray saw.");
                _log.Warn("Notifications navigation failed or timed out");
                return;
            }

            // Let the page's own scripts render the list before extracting.
            await Task.Delay(1800);

            if (UrlGuard.LooksLikeLoginUrl(core.Source))
            {
                SetSignedOut();
                return;
            }

            var raw = await core.ExecuteScriptAsync(ExtractionScript.Script);
            var json = ExtractionParser.UnwrapScriptResult(raw);
            var titleCount = TitleCount.Parse(core.DocumentTitle);
            var now = DateTimeOffset.UtcNow;
            var result = ExtractionParser.Parse(
                json, _readStore.Ids, titleCount, _snoozeStore.ActiveIds(now));

            if (result.SignedOut)
            {
                SetSignedOut();
                return;
            }


            // Extraction health: two consecutive non-Healthy polls raise
            // the ticker's “layout may have changed” banner.
            var health = ExtractionHealth.Evaluate(
                result.Items.Count, result.RawUnreadCount, titleCount);
            if (health == ExtractionHealthState.Healthy)
            {
                if (HealthWarning)
                    _log.Info("Extraction health back to Healthy");
                _consecutiveUnhealthyPolls = 0;
                HealthWarning = false;
            }
            else
            {
                _consecutiveUnhealthyPolls++;
                _log.Warn($"Extraction health {health}: {result.Items.Count} rows, " +
                          $"{result.RawUnreadCount} extracted unread, title count {(titleCount?.ToString() ?? "n/a")} " +
                          $"(consecutive non-healthy: {_consecutiveUnhealthyPolls})");
                if (_consecutiveUnhealthyPolls >= 2)
                    HealthWarning = true;
            }

            // Genuinely new items = ids never seen in the current list,
            // still unread after read/snooze state was applied.
            var knownIds = Items.Select(i => i.Id).ToHashSet();
            var newItems = result.Items
                .Where(i => i.IsUnread && !knownIds.Contains(i.Id))
                .ToList();
            _consecutiveEmptyPolls = newItems.Count == 0 ? _consecutiveEmptyPolls + 1 : 0;

            Items = NotificationCacheStore.Merge(Items, result.Items);
            // The counter reconciles the FRESH page result with the title
            // count (the v0.1 semantics); merged history items are already
            // inside the title count's world, so counting them again here
            // would double-count.
            UnreadCount = NotificationVisibility.CountUnread(
                result.Items, _settings.HiddenTypeGroups.ToHashSet(), titleCount);
            NotificationCacheStore.Save(_cachePath, Items);
            LastPoll = new PollStats
            {
                AtUtc = now,
                Rows = result.Items.Count,
                Unread = result.Items.Count(i => i.IsUnread),
                Health = health,
            };
            State = PollerState.Ready;
            LastError = null;
            _log.Info($"Poll ok: {result.Items.Count} rows ({Items.Count} in history), {UnreadCount} unread, " +
                      $"title count {(titleCount?.ToString() ?? "n/a")}, health {health}");

            // Adaptive cadence for the NEXT poll.
            _currentInterval = PollPolicy.NextInterval(
                TimeSpan.FromSeconds(_settings.PollIntervalSeconds), UnreadCount, _consecutiveEmptyPolls);
            RestartTimer();

            RaiseChanged();
            if (newItems.Count > 0)
                NewItemsArrived?.Invoke(newItems);
        }
        catch (Exception ex)
        {
            SetError("Something went wrong reading Facebook. Showing the last list FaceTray saw — Refresh to retry.");
            _log.Error($"Poll failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _pollInFlight = false;
        }
    }

    /// <summary>
    /// Counter for local state changes (mark read, snooze, settings):
    /// visible unread only. The title-count lift is a poll-time
    /// reconciliation and is not reapplied here — local actions only ever
    /// lower the count, and the next poll reconciles again.
    /// </summary>
    private int ComputeUnread(IReadOnlyList<NotificationItem> items) =>
        NotificationVisibility.CountUnread(items, _settings.HiddenTypeGroups.ToHashSet(), null);

    private void SetSignedOut()
    {
        State = PollerState.NotSignedIn;
        LastError = null;
        _log.Info("Poll: signed out");
        RaiseChanged();
    }

    private void SetError(string message)
    {
        State = PollerState.Error;
        LastError = message;
        RaiseChanged();
    }

    public void MarkRead(string id)
    {
        if (_readStore.MarkRead(id))
            _readStore.Save();
        ApplyLocalState();
    }

    public void MarkAllRead()
    {
        _readStore.MarkAllRead(Items.Select(i => i.Id));
        _readStore.Save();
        ApplyLocalState();
    }

    /// <summary>Snoozes one item until the given UTC instant; it renders read and stops counting.</summary>
    public void Snooze(string id, DateTimeOffset untilUtc)
    {
        _snoozeStore.Snooze(id, untilUtc);
        _snoozeStore.Save();
        _log.Info($"Snoozed one notification until {untilUtc.UtcDateTime:yyyy-MM-dd HH:mm} UTC");
        ApplyLocalState();
    }

    private void ApplyLocalState()
    {
        var now = DateTimeOffset.UtcNow;
        var snoozed = _snoozeStore.ActiveIds(now);
        var list = Items.ToList();
        foreach (var item in list)
            if (_readStore.IsRead(item.Id) || snoozed.Contains(item.Id))
                item.IsUnread = false;
        Items = list;
        UnreadCount = ComputeUnread(list);
        NotificationCacheStore.Save(_cachePath, Items);
        RaiseChanged();
    }

    public void ClearLocalState()
    {
        Items = Array.Empty<NotificationItem>();
        UnreadCount = 0;
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        if (_dispatcher.CheckAccess())
            Changed?.Invoke();
        else
            _dispatcher.BeginInvoke(() => Changed?.Invoke());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer?.Stop();
        try { _hostWindow?.Close(); } catch { }
        _hostWindow = null;
        _web = null;
    }
}
