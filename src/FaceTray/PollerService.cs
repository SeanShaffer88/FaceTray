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

    /// <summary>Facebook is holding the session at a “confirm it's you” checkpoint (v0.3.0).</summary>
    Checkpoint,
}

/// <summary>Counts-only snapshot of the most recent poll, for Settings → Diagnostics.</summary>
public sealed class PollStats
{
    public DateTimeOffset AtUtc { get; init; }
    public int Rows { get; init; }
    public int Unread { get; init; }
    public ExtractionHealthState Health { get; init; }

    /// <summary>Which extraction strategy produced the list (v0.3.0).</summary>
    public int Strategy { get; init; } = 1;
}

/// <summary>
/// What one “Mark all read” did, so the ticker can offer an exact undo:
/// the ids it newly marked, and the subset of those that were unread at
/// the time (exactly what an undo may legitimately restore to unread).
/// </summary>
public sealed class MarkAllReadUndo
{
    public IReadOnlyList<string> NewlyMarkedIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> RestoredUnreadIds { get; init; } = Array.Empty<string>();
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
    private readonly PinStore _pinStore;
    private readonly AlertTrackStore _alertTrackStore;
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

    private readonly Dictionary<int, int> _strategyCounts = new();
    private int _newSinceDigest;
    private bool _healing;
    private int _recreations;

    public PollerService(
        AppSettings settings,
        DiagnosticLog log,
        LocalReadStore readStore,
        SnoozeStore snoozeStore,
        PinStore pinStore,
        AlertTrackStore alertTrackStore,
        string cachePath,
        string webViewDataDir,
        Dispatcher dispatcher)
    {
        _settings = settings;
        _log = log;
        _readStore = readStore;
        _snoozeStore = snoozeStore;
        _pinStore = pinStore;
        _alertTrackStore = alertTrackStore;
        _cachePath = cachePath;
        _webViewDataDir = webViewDataDir;
        _dispatcher = dispatcher;
        _currentInterval = TimeSpan.FromSeconds(settings.PollIntervalSeconds);

        var now = DateTimeOffset.UtcNow;
        Items = NotificationCacheStore.Load(
            cachePath, readStore.Ids, snoozeStore.ActiveIds(now),
            settings.HistoryKeepDays, now, pinStore.Ids);
        UnreadCount = ComputeUnread(Items);
    }

    public PollerState State { get; private set; } = PollerState.Starting;
    public string? LastError { get; private set; }
    public IReadOnlyList<NotificationItem> Items { get; private set; }
    public int UnreadCount { get; private set; }
    public bool IsPaused { get; private set; }
    public bool WebReady { get; private set; }

    /// <summary>The Facebook session state the last poll found (v0.3.0; enum only, never a URL).</summary>
    public SessionState SessionState { get; private set; } = SessionState.Unknown;

    /// <summary>When a poll last succeeded end-to-end; drives the ticker's staleness line.</summary>
    public DateTimeOffset? LastSuccessfulPollUtc { get; private set; }

    /// <summary>Strategy the last poll used (0 before any poll).</summary>
    public int LastStrategy => LastPoll?.Strategy ?? 0;

    /// <summary>How often each extraction strategy produced the list this session.</summary>
    public IReadOnlyDictionary<int, int> StrategyCounts => new Dictionary<int, int>(_strategyCounts);

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

    /// <summary>
    /// Raised (UI thread) when a Comments&amp;mentions item has stayed
    /// unread for the re-alert delay (v0.3.0). The consumer decides
    /// (Focus Assist gate etc.) and then calls
    /// <see cref="NoteRealertDelivered"/> — the item is never re-raised.
    /// </summary>
    public event Action<NotificationItem>? RealertReady;

    /// <summary>
    /// Raised (UI thread) when the morning digest is due: how many new
    /// items arrived since the last digest, and the newest item to show.
    /// The consumer records the date and calls <see cref="NoteDigestSent"/>.
    /// </summary>
    public event Action<int, NotificationItem?>? DigestReady;

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
        core.ProcessFailed += async (_, e) =>
        {
            _log.Error($"WebView2 process failed: {e.ProcessFailedKind}");
            if (_disposed) return;
            await HealRendererAsync();
        };
    }

    /// <summary>
    /// Renderer self-healing (v0.3.0): a dead WebView2 renderer process is
    /// replaced — dispose the broken browser, rebuild it against the same
    /// session folder (the sign-in survives), resume polling. Budgeted by
    /// <see cref="RendererRetryPolicy"/>: backoff between attempts, a hard
    /// cap per session, then an honest stop.
    /// </summary>
    private async Task HealRendererAsync()
    {
        if (_disposed || _healing)
            return;
        if (!RendererRetryPolicy.ShouldRecreate(_recreations))
        {
            SetError("Facebook's view stopped and FaceTray couldn't restart it. Restart FaceTray to try again.");
            _log.Error($"Renderer healing exhausted after {_recreations} recreations");
            return;
        }
        _healing = true;
        _recreations++;
        _log.Warn($"Recreating the Facebook view (attempt {_recreations} of {RendererRetryPolicy.MaxRecreations})");
        try
        {
            await Task.Delay(RendererRetryPolicy.Backoff);
            if (_disposed || _hostWindow is null)
                return;
            try { _web?.Dispose(); } catch { /* the old renderer is already gone */ }
            _hostWindow.Content = null;
            _web = new WebView2();
            _hostWindow.Content = _web;
            WebViewEnv.Reset();
            var env = await WebViewEnv.GetAsync(_webViewDataDir);
            await _web.EnsureCoreWebView2Async(env);
            Wire(_web.CoreWebView2);
            WebReady = true;
            _log.Info("Facebook view recreated; resuming polls");
            RestartTimer();
            await PollNowAsync();
        }
        catch (Exception ex)
        {
            _log.Error($"Renderer recreation failed: {ex.GetType().Name}: {ex.Message}");
            SetError(RendererRetryPolicy.ShouldRecreate(_recreations)
                ? "Facebook's view stopped unexpectedly. FaceTray will try to restart it."
                : "Facebook's view stopped and FaceTray couldn't restart it. Restart FaceTray to try again.");
        }
        finally
        {
            _healing = false;
        }
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

            // Session-state detection (v0.3.0): judge where the page
            // ended up from its PATH only. A checkpoint is not a sign-out
            // — the session exists but Facebook wants a confirmation, and
            // the ticker must say so instead of “not signed in”.
            var sourcePath = "";
            try { sourcePath = new Uri(core.Source).AbsolutePath; }
            catch { /* an unparseable source says nothing */ }
            var session = SessionClassifier.Classify(sourcePath, core.DocumentTitle);
            SessionState = session;
            if (session == SessionState.Checkpoint)
            {
                State = PollerState.Checkpoint;
                LastError = null;
                _log.Warn("Poll: Facebook checkpoint — session state Checkpoint");
                RaiseChanged();
                return;
            }
            if (session == SessionState.SignedOut)
            {
                SetSignedOut();
                return;
            }

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
                json, _readStore.Ids, titleCount, _snoozeStore.ActiveIds(now), _pinStore.Ids);

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
                Strategy = result.Strategy,
            };
            _strategyCounts[result.Strategy] =
                _strategyCounts.TryGetValue(result.Strategy, out var used) ? used + 1 : 1;
            SessionState = SessionState.Active;
            LastSuccessfulPollUtc = now;
            State = PollerState.Ready;
            LastError = null;
            _log.Info($"Poll ok: {result.Items.Count} rows ({Items.Count} in history), {UnreadCount} unread, " +
                      $"title count {(titleCount?.ToString() ?? "n/a")}, health {health}, strategy {result.Strategy}");

            // Adaptive cadence for the NEXT poll.
            _currentInterval = PollPolicy.NextInterval(
                TimeSpan.FromSeconds(_settings.PollIntervalSeconds), UnreadCount, _consecutiveEmptyPolls);
            RestartTimer();

            RaiseChanged();
            if (newItems.Count > 0)
                NewItemsArrived?.Invoke(newItems);

            // --- v0.3.0: re-alert tracking, digest accounting ---
            _newSinceDigest += newItems.Count;
            var trackChanged = false;
            foreach (var fresh in result.Items)
                trackChanged |= _alertTrackStore.Track(fresh.Id, now);
            var liveIds = Items.Select(i => i.Id).ToHashSet();
            trackChanged |= _alertTrackStore.PruneExcept(liveIds) > 0;
            if (trackChanged)
                _alertTrackStore.Save();

            // One gentle re-alert: the first Comments&mentions item that
            // has stayed unread past the delay and passes the same
            // toast decision as a fresh arrival. Raised at most once per
            // poll; the consumer finishes the job via NoteRealertDelivered.
            foreach (var candidate in result.Items)
            {
                if (!RealertPolicy.IsDue(candidate,
                        _alertTrackStore.FirstSeenUtc(candidate.Id),
                        _alertTrackStore.HasRealerted(candidate.Id), now))
                    continue;
                if (!ToastDecision.ShouldToast(_settings, candidate, now))
                    continue;
                RealertReady?.Invoke(candidate);
                break;
            }

            // Morning digest: once a day, when its time has come and new
            // items have accumulated since the last one.
            if (DigestPolicy.IsDue(
                    _settings.DigestEnabled, _settings.DigestTime, now.ToLocalTime(),
                    _settings.LastDigestDate, _newSinceDigest))
            {
                DigestReady?.Invoke(_newSinceDigest,
                    newItems.FirstOrDefault() ?? result.Items.FirstOrDefault());
            }
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
        SessionState = SessionState.SignedOut;
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

    /// <summary>
    /// Marks everything read and reports exactly what changed, so the
    /// ticker can offer an undo that restores only what this call marked.
    /// </summary>
    public MarkAllReadUndo MarkAllRead()
    {
        var wasUnread = Items.Where(i => i.IsUnread).Select(i => i.Id).ToHashSet();
        var newly = _readStore.MarkAllRead(Items.Select(i => i.Id));
        _readStore.Save();
        ApplyLocalState();
        return new MarkAllReadUndo
        {
            NewlyMarkedIds = newly,
            RestoredUnreadIds = newly.Where(wasUnread.Contains).ToList(),
        };
    }

    /// <summary>Undoes a “Mark all read”: removes its marks and restores the previously-unread items.</summary>
    public void UndoMarkAllRead(MarkAllReadUndo undo)
    {
        if (undo.NewlyMarkedIds.Count == 0)
            return;
        _readStore.Unmark(undo.NewlyMarkedIds);
        _readStore.Save();
        var restore = undo.RestoredUnreadIds.ToHashSet();
        var snoozed = _snoozeStore.ActiveIds(DateTimeOffset.UtcNow);
        var list = Items.ToList();
        foreach (var item in list)
        {
            if (restore.Contains(item.Id) && !snoozed.Contains(item.Id) && !_readStore.IsRead(item.Id))
                item.IsUnread = true;
        }
        Items = list;
        UnreadCount = ComputeUnread(list);
        NotificationCacheStore.Save(_cachePath, Items);
        RaiseChanged();
    }

    /// <summary>Pins or unpins one item (v0.3.0); pinned items get their own ticker section.</summary>
    public void SetPinned(string id, bool pinned)
    {
        if (pinned) _pinStore.Pin(id, DateTimeOffset.UtcNow);
        else _pinStore.Unpin(id);
        _pinStore.Save();
        ApplyLocalState();
    }

    /// <summary>Called by the consumer after a re-alert was shown (or deliberately suppressed): never re-alert that id again.</summary>
    public void NoteRealertDelivered(string id)
    {
        _alertTrackStore.MarkRealerted(id);
        _alertTrackStore.Save();
    }

    /// <summary>Called by the consumer once the morning digest has been delivered: reset the counter.</summary>
    public void NoteDigestSent() => _newSinceDigest = 0;

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
        {
            if (_readStore.IsRead(item.Id) || snoozed.Contains(item.Id))
                item.IsUnread = false;
            item.IsPinned = _pinStore.IsPinned(item.Id);
        }
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
