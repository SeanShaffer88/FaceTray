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
/// </summary>
public sealed class PollerService : IDisposable
{
    private readonly AppSettings _settings;
    private readonly DiagnosticLog _log;
    private readonly LocalReadStore _readStore;
    private readonly Dispatcher _dispatcher;

    private Window? _hostWindow;
    private WebView2? _web;
    private DispatcherTimer? _timer;
    private TaskCompletionSource<bool>? _navTcs;
    private bool _pollInFlight;
    private bool _disposed;

    public PollerService(AppSettings settings, DiagnosticLog log, LocalReadStore readStore, Dispatcher dispatcher)
    {
        _settings = settings;
        _log = log;
        _readStore = readStore;
        _dispatcher = dispatcher;
        Items = NotificationCacheStore.Load(AppPaths.CachePath, readStore.Ids);
        UnreadCount = Items.Count(i => i.IsUnread);
    }

    public PollerState State { get; private set; } = PollerState.Starting;
    public string? LastError { get; private set; }
    public IReadOnlyList<NotificationItem> Items { get; private set; }
    public int UnreadCount { get; private set; }
    public bool IsPaused { get; private set; }
    public bool WebReady { get; private set; }

    /// <summary>Raised on the UI thread whenever state, items or count change.</summary>
    public event Action? Changed;

    /// <summary>Raised on the UI thread when a poll finds more unread than before (for toasts).</summary>
    public event Action<int>? NewUnreadArrived;

    public async Task StartAsync()
    {
        try
        {
            EnsureHost();
            var env = await WebViewEnv.GetAsync();
            await _web!.EnsureCoreWebView2Async(env);
            Wire(_web.CoreWebView2);
            WebReady = true;
            _log.Info($"WebView2 ready; profile {AppPaths.WebViewDataDir}");
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
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_settings.PollIntervalSeconds) };
        _timer.Tick += async (_, _) => await PollNowAsync();
        if (!IsPaused)
            _timer.Start();
    }

    /// <summary>Applies a changed poll interval from Settings without a restart.</summary>
    public void ApplySettings() => RestartTimer();

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
            var result = ExtractionParser.Parse(json, _readStore.Ids, titleCount);

            if (result.SignedOut)
            {
                SetSignedOut();
                return;
            }

            var previousUnread = UnreadCount;
            Items = result.Items;
            UnreadCount = result.UnreadCount;
            NotificationCacheStore.Save(AppPaths.CachePath, Items);
            State = PollerState.Ready;
            LastError = null;
            _log.Info($"Poll ok: {Items.Count} items, {UnreadCount} unread, title count {(titleCount?.ToString() ?? "n/a")}");
            RaiseChanged();
            if (UnreadCount > previousUnread)
                NewUnreadArrived?.Invoke(UnreadCount - previousUnread);
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
        ApplyLocalRead();
    }

    public void MarkAllRead()
    {
        _readStore.MarkAllRead(Items.Select(i => i.Id));
        _readStore.Save();
        ApplyLocalRead();
    }

    private void ApplyLocalRead()
    {
        var list = Items.ToList();
        foreach (var item in list)
            if (_readStore.IsRead(item.Id))
                item.IsUnread = false;
        Items = list;
        UnreadCount = list.Count(i => i.IsUnread);
        NotificationCacheStore.Save(AppPaths.CachePath, Items);
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
